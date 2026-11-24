# Docker／NAS部署与运维

适用版本：0.6.1，API v1，SQLite schema15。推荐在开发机交叉发布，NAS仅构建运行时镜像。Web、资料补全队列和SQLite在一个服务内运行，无需另部署数据库、消息队列或自动化服务。以下目录、域名及代际名均为示例。

## 1. 部署内容与准备条件

NAS需要Docker Engine及`docker compose`，有权管理本项目的管理员、可持久读写的本机磁盘目录，以及访问VNDB API／图片源和微软基础镜像仓库的网络。SQLite放本机卷；客户端通过HTTP API访问，不通过SMB／NFS直接读写数据库。

| 交付物 | 用途 |
| --- | --- |
| Linux runtime归档及其SHA收据 | `web/`、`cli/`、`admin/`、运行时Dockerfile及release-manifest |
| [compose.yaml](../deploy/compose.yaml) | 单服务模板；默认仅绑定127.0.0.1:5187 |
| [.env.example](../deploy/.env.example) | 绑定地址和宿主端口模板，不包含密钥 |
| [backup.sh](../deploy/backup.sh) | 手动完整快照入口，不安装定时任务 |

Linux x64 NAS选择`linux-x64`；脚本也支持`linux-arm64`，需匹配实际CPU并核对目标平台兼容性。桌面端和CLI说明见[桌面](DESKTOP.md)、[CLI](CLI.md)，接口见[Web API](WEB-API.md)。

## 2. 准备部署包

从干净Git工作区发布，不在发布过程改源码。开发机使用仓库`global.json`指定的.NET10 SDK、PowerShell7、Python3。执行标准检查后顺序发布，避免共享中间文件被并行改写。产物和日志默认在仓库旁的`VndbCli.private`，可用`VN_PRIVATE_ROOT`指定库外根。

```powershell
Remove-Item Env:VN_LIBRARY_KEY, Env:VNDB_API_TOKEN -ErrorAction SilentlyContinue
./scripts/Test.ps1
# OutputRoot必须是全新目录；脚本会在下面创建linux-x64。
./scripts/Publish-Runtime.ps1 -Runtime linux-x64 -OutputRoot ../VndbCli.private/releases/0.6.1/runtime/example-generation
python -B scripts/release_guard.py verify ../VndbCli.private/releases/0.6.1/runtime/example-generation/linux-x64 --kind runtime
python -B scripts/package_release.py runtime ../VndbCli.private/releases/0.6.1/runtime-example.tar.gz --runtime-root ../VndbCli.private/releases/0.6.1/runtime/example-generation/linux-x64
```

脚本生成唯一binlog，核对Web／CLI／Admin实际依赖、SDK发布清单、源码提交／树摘要、RID和逐文件SHA256，再封存。打包器拒绝未封存、被改写或含私有文件的目录，也拒绝覆盖已有归档。保留失败暂存，另选新代际重试；不要往旧发布目录追加本机配置。根多阶段Dockerfile供源码构建参考，NAS推荐使用发布目录内的运行时Dockerfile。

## 3. 首次部署目录

示例部署根为`/srv/vndb-local`，可改为NAS专用Docker配置目录中的本项目子目录。不要把整个共享目录当构建上下文。

```text
/srv/vndb-local/
  compose.yaml
  .env
  backup.sh
  release/                   # 已核验的runtime构建上下文
    Dockerfile
    release-manifest.json
    web/ cli/ admin/
  secrets/library_write_key  # 独立生成，绝不放入release/
  data/
    library.db               # 首次启动生成
    covers/
    service-instance-id      # 稳定实例身份，升级保留
    backups/
  audit/                     # 私有部署／回退证据
```

先传输归档、SHA收据、Compose模板、`.env.example`和backup.sh至项目目录。验证归档SHA256与可信收据一致，检查成员没有绝对路径、`..`或链接，再解压至新的`release/`，对照release-manifest核对内容。不能从含数据／密钥的目录执行`docker build .`。已有封存产物保留原样，不手工改Dockerfile或清单。

以下初始化只用于**全新且没有data／secrets的项目**。有现存数据库时跳到升级流程，沿用原属主／密钥，不执行首次初始化：

```sh
cd /srv/vndb-local
if [ -e data ] || [ -e secrets ]; then
  printf '已有data或secrets，停止初始化，请使用升级流程。\n' >&2
else
  sudo install -d -m 0750 -o 1654 -g 1654 data
  sudo install -d -m 0700 secrets
fi
```

当前运行时镜像使用数字UID:GID `1654:1654`，新数据目录须允许该身份读写。FNOS ACL与普通Linux权限可能不同，应确认有效访问权限；不要凭NAS用户名猜UID，**不要对已有共享目录／数据库递归chown、chmod或设777**。

用密码管理器或随机数工具生成独立应用管理密钥。NAS已有Python3时可用以下命令写入新文件，不把正文输出到终端：

```sh
sudo python3 - <<'PY'
import os, pathlib, secrets
target = pathlib.Path('/srv/vndb-local/secrets/library_write_key')
fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as output:
    output.write(secrets.token_urlsafe(48))
os.chown(target, 1654, 1654)
os.chmod(target, 0o400)
PY
```

它不是SSH密码或VNDB令牌。单独安全保管一份，向桌面／浏览器输入时通过管理员安全渠道取得，不粘贴进命令历史、工单或日志。升级不重新生成；生产环境缺少密钥时Web拒绝启动。

复制`.env.example`为项目`.env`后编辑：`VNDB_PORT`为宿主端口，默认`VNDB_BIND_IP=127.0.0.1`仅本机可访问。需要局域网客户端时改为NAS实际局域网接口IP。真实`.env`不进Git；面向可信局域网使用，跨非可信网络沿用已有HTTPS入口，不直接暴露公网HTTP。

## 4. 构建与启动

以下在部署根运行；系统要求时给Docker命令加`sudo`。模板context为`./release`，必须与目录一致。

```sh
docker compose config --quiet
docker compose build library
docker compose up -d --no-build library
docker compose ps library
docker compose logs --tail 100 library
```

首次构建需取得`mcr.microsoft.com/dotnet/aspnet:10.0-noble`，不在NAS装SDK。容器内8080，宿主默认5187。模板包含只读根、可写`/data`、只读密钥挂载、64MiB tmpfs、cap-drop ALL、no-new-privileges、768MiB内存上限、2CPU及有界日志。健康检查有20秒起始宽限、30秒间隔；失败先看日志，Running不等于Healthy。

按实际绑定地址验证，默认绑定可执行：

```sh
curl --fail http://127.0.0.1:5187/healthz
curl --fail http://127.0.0.1:5187/api/v1/info
curl --fail http://127.0.0.1:5187/api/v1/stats
```

若绑定特定局域网IP，相应替换URL，不能假定宿主回环也监听。浏览器打开根页面；写操作输入应用密钥。新库为空是正常状态，有权限的查询或JSON导入会逐步建库。

## 5. 配置与后台任务

| 环境变量／模板项 | 当前值或含义 |
| --- | --- |
| `VNDB_BIND_IP`、`VNDB_PORT` | Compose宿主监听地址／端口 |
| `Library__DataDirectory` | `/data`，权威库及封面 |
| `Library__WriteKeyFile` | `/run/secrets/library_write_key`；推荐文件挂载，优先于明文配置 |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `CatalogJobs__Enabled` | 默认true；关闭现有消费者不删除保存的任务 |
| `MetadataImports__MaximumBytes` | 默认8388608；允许1024–33554432 |
| `MetadataImports__MaximumItems` | 默认500；允许1–5000 |

限额由`GET /api/v1/info`公布，HTTP信封另允许1MiB。客户端本机JSON与响应缓冲各有独立8MiB限制，提高Web限额不代表客户端也支持更大文件。已发送／不明批次保留原ID与原件，不能自动拆分重发。

页面：`/`默认进入我的书架；`/?view=local`为公共资料目录，`/?view=plays`为游玩记录；`/manage.html`为书架／metadata导入；`/quality.html`为资料缺口；`/sessions.html`为会话／活动导入／冲突；`/openapi/v1.json`为v1机器规范。浏览不用账号，单管理密钥保护写入，无多用户隔离。

metadata在同一Web内持久排队，本地逐项处理与联网补全分开，联网任务至少间隔5秒；Core继续串行节流及有限重试。旧资料补全任务间隔支持3–3600秒。关闭页面不会取消队列；通过界面暂停／恢复明确任务，不另装轮询服务。接收成功不代表匹配或封面全部完成。

## 6. 手动完整备份与隔离恢复

从部署根手动执行：

```sh
sh ./backup.sh
```

脚本定位原容器／镜像ID，读取`data/library.db`的**文件**有效UID:GID，在该身份调用Admin snapshot。`data/backups/<时间戳>/`包含一致SQLite、全部已登记封面、原service-instance-id及snapshot-manifest。密钥只留指纹，正文另存；不只复制library.db，不删除在线WAL／SHM。验证清单后将快照复制到独立介质，同盘副本不能单独承担灾难恢复。

以下恢复到新隔离目录，不改变原服务；`SNAPSHOT`替换为实际快照，`RESTORE`不得已存在：

```sh
cd /srv/vndb-local
OWNER=$(stat -c '%u:%g' data/library.db)
SNAPSHOT=/data/backups/your-snapshot
RESTORE=/data/backups/your-isolated-restore
docker compose exec -T --user "$OWNER" library dotnet /app/admin/vndb-admin.dll \
  library restore "$SNAPSHOT" "$RESTORE" --data-dir /data \
  --key-file /run/secrets/library_write_key --offline-maintenance --json
```

restore验证文件集合、SHA、封面登记、schema10–15、SQLite integrity／外键和原实例。密钥不匹配、文件损坏或目标已存在均失败保留原件。演练不自动切换数据卷；真实恢复需停写，单独审核目标挂载／属主／匹配镜像及原密钥，再检查健康和数据。不能生成新service-instance-id来“修复”恢复。

Admin的`library backup`只备份DB，不能替代完整snapshot；Admin是受控维护入口，客户端日常走API。旧含位置的数据库会被Web拒绝启动，不能直接放回活动目录或用旧位置接口上线。

## 7. 后续升级与回退

每次使用新封存代际和唯一镜像标签，即使产品版本相同也区分源码／代际。新目录如`releases/example-next/runtime/`，不覆盖现有release：

1. 私有audit保存原Compose、镜像ID、UID:GID、端口／挂载／安全设置、密钥指纹及服务实例。只检查本项目，避免完整环境转储泄露密钥。
2. 上传／核验新runtime，在原Web运行时先构建唯一标签镜像。例如从项目根执行`docker build --label org.opencontainers.image.revision=<完整提交> -t vndb-local:0.6.1-example-next -f releases/example-next/runtime/Dockerfile releases/example-next/runtime`；先替换占位符。
3. 完整snapshot并隔离restore验证；短暂停原Web保存最终一致DB副本／逐行摘要。所有SQLite读取和备份用原文件属主，不用管理员身份打开活库。
4. 只改Compose的image及build context，保留其余配置；执行`docker compose up -d --no-deps --no-build library`，有界等待Healthy。
5. 比较info身份、schema、原始行、封面SHA、密钥及属主；核对新程序集／静态文件与封存清单，再用已有ID测API／CLI本地优先查询。区分合法任务推进和原数据丢失，不能只比较总数。

启动／数据保护校验失败时，恢复保留的原Compose和旧镜像，重新检查Healthy。schema或数据已改变时镜像回退不等于数据回退，需完整快照受控恢复。本版schema15；迁移后的镜像回退必须配套旧库快照，不能覆盖活库。验收前保留旧镜像、配置、备份及失败证据，不执行`down -v`、镜像清理或自动删备份。

## 8. 排障与审计

| 现象 | 处理 |
| --- | --- |
| 连接失败 | 核对绑定IP／端口、容器及防火墙；默认回环不提供局域网访问 |
| 401 | 检查应用管理密钥及X-Library-Request，不使用NAS管理员密码 |
| 413／项数超限 | 先读info；只拆分未提交的新文件，待确认原件保持原ID |
| 目录权限错误 | 记录原UID:GID／挂载／ACL，先诊断，不递归改共享权限 |
| 启动隐私校验失败 | 保留原库及证据，按[数据边界](DATA-AND-PRIVACY.md)受控迁移 |
| 队列慢／上游429 | 遵循既有节流退避，观察任务，不增加并发爬取器 |
| 健康探针正常但文字搜索500 | 探针不代表全库可读；部署后同时验收作品ID读取与文字搜索。保留原库，在独立目录提取／重建并核对个人记录及完整性，通过后再受控切换，不能直接覆盖活库 |
| 旧样式 | 核对响应和镜像代际再刷新；未提交稿件先保留，静态资源使用no-cache |

真实连接配置、个人数据库、密钥与运维审计保存在私有位置，不加入源码或可分发包。客户端与Web的数据位置及备份范围见[数据与隐私](DATA-AND-PRIVACY.md)。

## 9. 版本兼容与离线恢复

当前运行代码使用schema15。新工具可以读取清单与DB一致的schema10–15快照，恢复本身不升级数据库；旧镜像不能写入较新schema，回退必须配套对应旧快照并保护新增个人数据。旧含客户端位置的数据库不能直接放回活动服务。

完整快照格式为`vn-library-snapshot-v2`，包含数据库、已登记封面、原实例、原镜像ID、可选密钥指纹以及公共目录／任务覆盖摘要。原密钥正文独立保管。新工具继续读取v1，旧工具不能读取v2，应将快照与匹配镜像一起保管。

`backup.sh`默认包含未完成任务引用且校验通过的原包／规范暂存，不纳入未登记半包或废弃代际。完成的公共目录可直接随DB恢复后检索；下载未完成仍需重新下载，预检未完成从保存的原包重新预检。包含输入的快照若校验失败，应保留现场，不将普通DB快照当作离线续跑保证。任务管理见[公共资料目录](CATALOG.md)。

没有外网时，先校验并加载已保管的镜像，再将快照恢复到全新目录；不以联网构建或拉取作为恢复步骤：

```sh
sha256sum -c runtime-images.tar.sha256
docker load -i runtime-images.tar
NEW_IMAGE=vndb-local:your-verified-generation
OWNER=1654:1654

docker run --rm --network none --user "$OWNER" --read-only \
  --mount type=bind,src=/srv/vndb-recovery,dst=/recovery \
  --mount type=bind,src=/srv/vndb-secrets/library_write_key,dst=/run/secrets/library_write_key,readonly \
  --entrypoint dotnet "$NEW_IMAGE" /app/admin/vndb-admin.dll \
  library restore /recovery/snapshot /recovery/restored \
  --data-dir /recovery --key-file /run/secrets/library_write_key --offline-maintenance --json
```

`NEW_IMAGE`及`OWNER`应与快照对应，恢复父目录事先允许该数字UID:GID写入；目标`restored`不得存在。验证后将Compose数据挂载指向新恢复目录，保留原实例并只读挂载原密钥，使用已加载镜像启动。首次只读检查可关闭`CatalogJobs__Enabled`，核对健康、info身份、公共目录与已知个人记录后再受控启用写入。

将公共资料复制到另一机器建立独立库时，使用[离线部署与公共导入](OFFLINE-DISTRIBUTION.md)，不用个人快照和原服务身份进行初始化。


### 个人分类存储迁移

`personal_organization`的历史迁移将schema13增量升级为14，新增个人收藏／虚拟集合／标签表；本版还会继续升级至schema15（见下节）；既有作品、个人记录和存在标记保留，数据库仍为原数据卷中的`library.db`。先按本说明创建并验证快照，再切换镜像。旧schema13镜像不能写入schema14及更高版本，镜像回退必须搭配原备份。个人分类同步要求Web与客户端均支持该可选能力；旧客户端的书架／个人条目CRUD仍可使用。客户端本机资源档案不进入Docker数据卷。

### 官方资料同步状态迁移

启动时将schema14增量升级为15，仅新增单行`dataset_sync`操作配置表，保存可选调度、上次发现日期和限流／重试时间；原作品、个人记录、封面和导入队列保持。无需新环境变量、容器或宿主计划任务。定时默认关闭，在Web“资料管理 → 导入 → 可选定时同步”显式启用，使用现有`CatalogJobs:Enabled`后台任务（默认开启）。外网只需访问既有官方HTTPS域名`dl.vndb.org`。

完整数据库快照会包含调度状态；恢复后仍遵守原检查周期和等待时间。向另一台机器分发公共资料继续使用公共导入包，不复制个人快照中的调度/服务身份。升级前按原规则验证完整备份；旧schema14镜像不能写schema15，回退需使用对应旧快照。输入与暂存仍保留在数据卷`datasets`，自动同步不会自动清理已接收旧输入；空间不足时原任务保留并等待人工处理。

Web 导入页提供容量摘要与手动清理预览，`dataset_maintenance` 不增加数据库 schema 或新服务。上传、下载和预检以同一数据目录文件租约串行维护输入，重复上传仍验证完整内容，已接收同包不新增长期原包；空包、断开及超限的未接收尝试清理。保留至少4GiB准备空间及现有数据库／封面／完整备份额外空间。清理仅处理核验过的无引用临时文件与已完成暂存，保留原包及恢复所需输入。预览与中断意图位于数据卷 `dataset-cleanups`；异机部署使用正式公共资料包或完整备份，不依赖这些维护意图。
