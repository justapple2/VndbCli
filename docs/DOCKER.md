# Docker／NAS 部署与运维

适用版本：0.7.0，API v1，SQLite schema15。推荐在开发机发布 Linux runtime，NAS 只构建运行时镜像。Web、SQLite 和后台任务运行在同一服务内。以下目录、地址和代际名均为示例，按实际环境替换。

## 1. 部署内容与准备条件

NAS 需要 Docker Engine、`docker compose`、本机持久卷和管理本项目的权限。SQLite 不放 SMB／NFS 共享盘，客户端通过 API 访问。在线构建需能取得微软 .NET 基础镜像；查询补全及资料更新还需访问 VNDB 官方来源。

| 文件 | 用途 |
| --- | --- |
| 已封存的 Linux runtime 及 SHA 收据 | `web/`、`cli/`、`admin/`、Dockerfile、许可和发布清单 |
| [compose.yaml](../deploy/compose.yaml) | 单服务模板；默认仅绑定 `127.0.0.1:5187` |
| [.env.example](../deploy/.env.example) | 监听地址与端口，不含密钥 |
| [backup.sh](../deploy/backup.sh) | 手动完整快照，不安装定时任务 |

发布脚本支持 `linux-x64`、`linux-arm64`，必须匹配 NAS CPU；目标平台还需实际验证。Windows 桌面使用独立发布包。职责见[架构](ARCHITECTURE.md)，客户端说明见[桌面](DESKTOP.md)与 [CLI](CLI.md)。

## 2. 准备部署包

从固定源码树构建，在发布期间保持源码不变。开发机需要 `global.json` 兼容的 .NET 10 SDK、PowerShell 7、Python 3、Node.js；Node.js 用于 Web 测试。构建、验证日志及产物默认在仓库旁 `VndbCli.private`，可用 `VN_PRIVATE_ROOT` 指定库外根。

在仓库根按顺序执行，避免并发争用中间输出：

```powershell
./scripts/Test.ps1
./scripts/Publish-Runtime.ps1 -Runtime linux-x64 -OutputRoot ../VndbCli.private/releases/0.7.0/runtime/example-generation
python -B scripts/release_guard.py verify ../VndbCli.private/releases/0.7.0/runtime/example-generation/linux-x64 --kind runtime
python -B scripts/package_release.py runtime ../VndbCli.private/releases/0.7.0/runtime-example.tar.gz --runtime-root ../VndbCli.private/releases/0.7.0/runtime/example-generation/linux-x64
```

每次选择全新输出目录和归档名；失败后保留证据并另选代际，不覆盖封存目录。脚本检查实际 SDK 发布成员、依赖边界、版本、API／schema、源码身份及逐文件 SHA256。归档旁的 JSON 收据记录归档 SHA256；NAS 上传后重新核对，并检查归档成员没有绝对路径、`..` 或链接，再解压至新的构建上下文。构建上下文只包含 runtime，不包含数据、密钥或整个共享目录。

桌面与 CLI 可使用同一源码树串行发布：

```powershell
./scripts/Publish-Desktop.ps1
./scripts/Publish.ps1 -Runtime win-x64
```

桌面发布目标为独立 Windows x64 目录；CLI 可按脚本支持的 RID 另行发布。保留整个客户端目录，不向发布目录追加运行数据。根 [Dockerfile](../Dockerfile)提供源码构建方式；低内存 NAS 推荐上述交叉发布方式。

## 3. 首次部署目录与密钥

示例根为 `/srv/vndb-local`：

```text
/srv/vndb-local/
  compose.yaml
  .env
  backup.sh
  release/                   # 已核验的 runtime
    Dockerfile
    release-manifest.json
    web/ cli/ admin/
  secrets/library_write_key  # 独立管理密钥，只读挂载
  data/
    library.db
    covers/
    service-instance-id
    datasets/
    backups/
```

将模板放到部署根，将核验后的 runtime 解压到 `release/`，与模板的 build context 保持一致。复制 `.env.example` 为 `.env`，修改 `VNDB_BIND_IP`、`VNDB_PORT`。默认回环绑定仅允许宿主访问；局域网部署改为 NAS 的实际局域网接口地址。

以下仅用于**全新、没有 data 和 secrets 的部署**。已有服务跳到升级章节，不重新生成密钥或实例身份：

```sh
cd /srv/vndb-local
if [ -e data ] || [ -e secrets ]; then
  printf '已有数据或密钥，请使用升级流程。\n' >&2
  exit 1
else
  sudo install -d -m 0750 -o 1654 -g 1654 data
  sudo install -d -m 0700 secrets
fi
```

当前镜像使用数字 UID:GID `1654:1654`。新数据目录需允许该身份写入；已有目录应保留原有效属主／ACL，不递归修改共享权限。独立应用密钥可用以下方式创建，命令不输出正文；文件若已存在会拒绝覆盖：

```sh
sudo python3 - <<'PY'
import os, pathlib, secrets
path = pathlib.Path('/srv/vndb-local/secrets/library_write_key')
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
with os.fdopen(fd, 'w') as output:
    output.write(secrets.token_urlsafe(48))
os.chown(path, 1654, 1654)
os.chmod(path, 0o400)
PY
```

密钥不是 NAS 账户密码或 VNDB 令牌，须独立安全保管。不要写进 `.env`、源码、镜像、命令历史或公开日志。容器通过只读文件挂载读取；Production 缺少应用密钥时拒绝启动。

## 4. 构建、启动与确认

在部署根执行；系统要求时为 Docker 命令使用相应权限：

```sh
docker compose config --quiet
docker compose build library
docker compose up -d --no-build library
docker compose ps library
docker compose logs --tail 100 library
```

镜像基于 `mcr.microsoft.com/dotnet/aspnet:10.0-noble`，NAS 无需 .NET SDK。容器内端口 8080，宿主默认 5187。模板采用只读根、可写 `/data`、只读密钥、64 MiB tmpfs、cap-drop ALL、no-new-privileges、768 MiB 内存上限、2 CPU 及有界日志。

按实际绑定地址检查；默认回环绑定可执行：

```sh
curl --fail http://127.0.0.1:5187/healthz
curl --fail http://127.0.0.1:5187/api/v1/info
curl --fail http://127.0.0.1:5187/api/v1/stats
```

绑定特定局域网 IP 时替换 URL，宿主回环不一定监听。Running 不等于 Healthy；健康检查有起始宽限，失败时先看本项目日志。`info` 应显示预期版本、API、schema、实例和能力。部署后还应读取已知作品 ID、测试标题搜索与个人记录，健康探针不代表数据库所有页均可读。

浏览器打开服务根进入“我的书架”。新库为空是正常状态，有权限的查询、JSON 导入或官方包导入会逐步建库。浏览无需账号，写入需要独立应用密钥；系统没有多用户隔离，面向可信局域网，跨不可信网络需另行提供 HTTPS 访问入口。

## 5. 配置、数据导入与更新

| 配置 | 默认／含义 |
| --- | --- |
| `VNDB_BIND_IP`、`VNDB_PORT` | Compose 宿主监听地址和端口 |
| `Library__DataDirectory` | `/data`，权威库、封面与任务数据 |
| `Library__WriteKeyFile` | `/run/secrets/library_write_key`；优先使用只读文件 |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `CatalogJobs__Enabled` | 默认 true；控制 Web 内既有后台任务 |
| `MetadataImports__MaximumBytes` | 默认 8388608；范围 1024–33554432 |
| `MetadataImports__MaximumItems` | 默认 500；范围 1–5000 |

JSON 导入限额由 `/api/v1/info` 公布，HTTP 信封另允许 1 MiB。客户端本机 JSON 与 HTTP 响应还有独立限额，不能仅提高 Web 限额就发送任意大文件。结果不明的批次按原 ID 和内容重试。

| 入口 | 用途 |
| --- | --- |
| `/` | 个人书架；`/?view=local` 为公共资料目录 |
| `/manage.html?tab=imports` | JSON 导入、官方更新、任务与容量管理 |
| `/quality.html` | 资料缺口及候选核对 |
| `/sessions.html` | 活动／会话导入及冲突 |
| `/openapi/v1.json` | API v1 机器规范 |

在导入页可只“检查最新版”，或“检查并更新”。前者仅对固定官方来源发 HEAD 获取日期；后者将新日期交给原有导入队列。状态读取不联网，只有完整导入完成后才推进本库快照日期。可选定时同步默认关闭、默认间隔 7 天，可设置 1–30 天；需要启用既有后台任务，不添加宿主计划任务或新服务。

联网补全、官方请求和重试沿用既定节流。关闭网页不取消已接收任务；暂停、失败需明确处理。预检至少预留 4 GiB 额外空间，数据库、封面、索引、已接收输入和完整备份另计。“容量与暂存”先预览再清理，不清除数据库、封面、官方原包或恢复所需输入。使用范围及同步语义见[公共资料目录](CATALOG.md)。

## 6. 手动完整备份与隔离恢复

在部署根执行：

```sh
sh ./backup.sh
```

脚本使用实际数据库文件属主调用 Admin，生成并校验 `data/backups/<唯一名称>/`。完整快照包括一致 SQLite、已登记封面、原 `service-instance-id`、恢复清单，以及可选的已验证任务输入。密钥正文不在快照内，仅记录指纹；须另行安全保管。不要直接复制在线主库、删除 WAL／SHM 或以管理员身份打开活库来修复权限。将验证过的快照复制到独立介质，同卷副本不是完整灾备。

隔离恢复只写**全新目录**，不改变原服务；替换示例快照名，目标不得存在：

```sh
cd /srv/vndb-local
OWNER=$(stat -c '%u:%g' data/library.db)
SNAPSHOT=/data/backups/your-snapshot
RESTORE=/data/backups/your-isolated-restore

docker compose exec -T --user "$OWNER" library dotnet /app/admin/vndb-admin.dll \
  library restore "$SNAPSHOT" "$RESTORE" --data-dir /data \
  --key-file /run/secrets/library_write_key --offline-maintenance --json
```

restore 核验清单、文件集合、SHA、封面登记、SQLite 完整性／外键、schema 和实例；目标已存在或密钥不符会失败。当前工具读取 schema10–15 的一致快照，恢复本身不升级数据库。完整快照格式为 `vn-library-snapshot-v2`，也可读取 v1；旧工具未必能读取 v2，应保存匹配镜像。

真正恢复时，先确认新目录、有效属主、原密钥及匹配镜像，再停止本项目写入并切换数据挂载；检查健康、实例、查询及个人资料后才继续使用。Admin 的单 DB `backup` 不替代完整 snapshot。备份客户端时另行退出桌面并保存整个 JSON 数据根；Web 快照不含游戏或本机启动路径。

## 7. 升级与回退

每次发布使用新目录和唯一镜像标签，保留现有 `.env`、密钥、数据卷、端口及安全设置：

1. 核对新 runtime 的封存收据，在旧 Web 运行期间构建新标签镜像，例如 `vndb-local:0.7.0-example-next`。构建上下文指向新 runtime，不覆盖旧代际。
2. 保存本项目原 Compose、镜像 ID、实例、属主及完整快照，做隔离恢复验证。准备切换时短暂停写，补做最终一致备份，避免遗漏期间新增个人记录。
3. 只更新 Compose 的 `image` 与 `build.context`，执行 `docker compose config --quiet`，再执行 `docker compose up -d --no-deps --no-build library`。
4. 等待 Healthy，核对 `info`、已知作品及标题搜索、个人记录、封面和文件属主；将运行文件与发布清单比较。数据变化应区分正常任务推进与资料丢失。

当前 schema15 的增量升级保留原有作品、个人记录和导入任务；`dataset_sync` 保存可选调度及日期检查状态，定时默认关闭。新版本可能自动迁移数据库，因此**镜像回退不等于数据回退**：数据库已升级时，须使用对应旧快照恢复到新目录后切换，不能把旧镜像直接指向新库。切换前保护升级后新产生的个人数据。

确认升级前保留恢复点；不使用 `down -v`、自动删备份或无差别镜像清理作为升级步骤。旧含客户端位置的数据库不能重新启用位置 API，应按[数据边界](DATA-AND-PRIVACY.md)处理。

## 8. 跨机器与离线部署

区分两种用途：

- **恢复同一个人库**：完整快照、原密钥、实例身份和匹配镜像一起保管；恢复后身份保持。
- **建立独立新库**：使用通用应用、官方公共资料原包及其许可。首次启动生成新身份和密钥，不分发个人数据库或原服务配置。

仅有 runtime 目录不能保证断网构建成功。离线部署前，在联网且平台匹配的机器上构建并保存应用镜像，同时保存 Compose、使用文档、快照或公共资料原包及各自校验收据：

```sh
docker save -o runtime-images.tar vndb-local:0.7.0
sha256sum runtime-images.tar > runtime-images.tar.sha256
```

离线机器先核验再加载，使用相同镜像标签配置 Compose，禁止联网拉取：

```sh
sha256sum -c runtime-images.tar.sha256
docker load -i runtime-images.tar
docker compose up -d --no-build --pull never library
```

个人库可先在全新恢复父目录中执行以下离线恢复；父目录需允许快照对应的数字 UID:GID 写入，原密钥须已存在：

```sh
docker run --rm --network none --user 1654:1654 --read-only \
  --cap-drop ALL --security-opt no-new-privileges \
  --tmpfs /tmp:size=64m,mode=1777 \
  --mount type=bind,src=/srv/vndb-recovery,dst=/recovery \
  --mount type=bind,src=/srv/vndb-secrets/library_write_key,dst=/run/secrets/library_write_key,readonly \
  --entrypoint dotnet vndb-local:0.7.0 /app/admin/vndb-admin.dll \
  library restore /recovery/snapshot /recovery/restored --data-dir /recovery \
  --key-file /run/secrets/library_write_key --offline-maintenance --json
```

示例镜像和 UID:GID 必须替换为快照对应值。恢复完成后按第 6 节切换数据挂载，可暂时关闭后台任务做只读确认。未完成资料任务只有在快照包含其已验证输入时才可能离线续跑；下载半包和未完成预检不能当作可恢复输入。

独立新库使用第 3 节初始化，再通过 `POST /api/v1/datasets/upload` 上传官方 `.tar.zst`，而不是使用 JSON 文件选择器。该接口接收 `application/octet-stream`，最大 512 MiB，需要管理密钥、`X-Library-Request: 1` 和稳定幂等键；具体契约见 [API](WEB-API.md#官方导出任务)。直到任务 `completed` 才算完成导入。公共包不含全部图片，也不会自动创建个人条目或游玩记录。

## 9. 排障与资料保管

| 现象 | 检查 |
| --- | --- |
| 无法连接 | 绑定 IP／端口、容器健康和网络；默认回环不开放局域网访问 |
| 401 | 应用管理密钥与请求头；不要使用 NAS 账户密码 |
| 409 | 保留原稿和操作 ID，读取当前修订，区分冲突与结果不明 |
| JSON 超限 | 读取 info；只拆分尚未提交的新文件，不改写待确认批次 |
| 权限错误 | 数据文件原 UID:GID、挂载及 ACL；不递归放宽共享权限 |
| 健康正常但搜索报错 | 保留原库，在独立副本核验完整性和个人记录，不直接覆盖活库 |
| 上游限流／导入失败 | 遵守等待时间，核对原任务后明确恢复，不并发重复抓取 |
| 界面仍是旧内容 | 核对镜像与资源代际，先保留未提交草稿再刷新 |

源码和可分发包只保留项目说明、通用配置、程序与许可。真实连接配置、密钥、数据库、个人 JSON、运维记录和验证截图放在私有位置。数据目录与客户端备份范围见[数据与隐私](DATA-AND-PRIVACY.md)。
