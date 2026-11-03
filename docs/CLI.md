# CLI使用说明

适用版本0.5.0。`vndb`面向命令行和脚本；日常查询、元数据同步与经过批准的本机整理复用共享模块。CLI不带数据库，不直接读NAS SQLite，不提供通用个人CRUD／游戏启动命令；个人编辑和启动使用[桌面](DESKTOP.md)，第三方集成使用[Web API](WEB-API.md)。

## 1. 安装与帮助

使用匹配平台的CLI发布包，解压并保留整个目录。Windows为`vndb.exe`，Linux为`vndb`；可加入个人PATH或使用完整路径。发布脚本支持Windows／Linux多种RID，已交付实测Windows x64；其他目标需单独验证。

```powershell
./vndb.exe --help
./vndb.exe vn --help
./vndb.exe organize --help
./vndb.exe organize register --help
```

下面省略可执行文件目录，以`vndb`表示。源码开发可用`dotnet run --project src/Vndb.Cli -- <参数>`；普通用户直接用已发布程序，无需SDK。

### 本机登记的轻量索引迁移

退出所有桌面与 CLI 写入任务，先备份整个本机数据根。以下命令只操作指定数据根的 JSON，不联网、不触碰游戏；默认根与桌面一致，可用 `--identity-dir <数据根>` 明确指定。

```powershell
vndb local-data index-preview --json --output ./index-preview.json
# 核对预览的目录、条目数、原件与索引大小，并使用 stderr 给出的批准摘要
vndb local-data index-apply --preview-json ./index-preview.json --approve <批准SHA256> --json
```

预览文件也含本机目录，只能存放在库外私有位置。启用后使用 `local-library.v2.json` 及 `local-metadata/`，保留 `local-library.json` 原件。原件或预览变化会拒绝切换；中断后没有完整索引就仍使用旧登记，可以重新预览。旧版本不能维护新索引：不要混用旧程序写入原件，否则新程序会停止读写并提示核对。

迁移不改设备、启动配置、组件 ID、待同步稿和回执。尚未产生新编辑时可在备份后将 v2 索引移出数据根以回到原件；产生新编辑后不能直接退回旧原件，应保留整个数据根进行核对。不要删除引用的详情快照或未同步资料。

## 2. 连接配置与数据根

| 项目 | 优先级／含义 |
| --- | --- |
| Web根地址 | `--server` > `VN_LIBRARY_URL` > 客户端JSON的`server` |
| 查询JSON根 | `--cache-dir` > `VN_LIBRARY_CACHE` > JSON的`cache_directory` > 本机数据根 |
| 本机数据根 | `VN_DESKTOP_HOME`，未设置为系统文档下`VndbLocal` |
| 配置文件 | `--config`显式指定；默认优先desktop-settings.json，否则client.json |
| Web管理密钥 | 当前进程`VN_LIBRARY_KEY`；CLI不会自动解密桌面的DPAPI凭据 |
| VNDB令牌 | 仅显式`authinfo`读取`VNDB_API_TOKEN`，公开查询不需要 |

配置示例只包含非敏感字段，不放密钥：

```json
{
  "server": "http://nas.local:5187/",
  "cache_directory": null
}
```

配置文件放用户数据目录或私有运维目录，不放源码／发布包。Web地址只允许HTTP(S)根地址，不得带凭据、子路径、query或fragment。查询根下仍创建`servers/<origin SHA256>/`，`--cache-dir`不是直接pending目录，也不改变安装／启动配置／设备ID根。

PowerShell7可在当前进程临时输入管理密钥，避免将字面值写进命令历史，完成后清除：

```powershell
$env:VN_LIBRARY_KEY = Read-Host '应用管理密钥' -MaskInput
try {
    vndb vn get v17 --server http://nas.local:5187/ --json
} finally {
    Remove-Item Env:VN_LIBRARY_KEY -ErrorAction SilentlyContinue
}
```

该变量仍是进程内明文，应避免环境转储和不必要子进程。不要使用SSH管理员密码。已有本库GET不需要密钥；首次缺失查询／补全及同步需要。

## 3. 常用查询

```powershell
vndb vn search "Example" --server http://nas.local:5187/ --limit 10 --page 1 --json
vndb vn get v17 --server http://nas.local:5187/ --json
vndb vn get https://vndb.org/v17 --source vndb --json
vndb producer search "Example" --server http://nas.local:5187/ --json
vndb producer get p98 --server http://nas.local:5187/ --json
vndb producer vns p98 --server http://nas.local:5187/ --limit 10 --page 1 --json
vndb vn search "Example" --developer p98 --server http://nas.local:5187/ --json
vndb stats --scope library --server http://nas.local:5187/ --json
vndb stats --scope vndb --json
```

`stats`默认范围是**vndb全站**，要查NAS本库必须明确`--scope library`。会社为全部关联开发者，不自动推断唯一主开发商或发行商；不同版本／组件需人工核对。VNDB未知日期可能不是完整年月日，不应强制解释成某日。

### 来源选择

| `--source` | 行为 |
| --- | --- |
| `auto`（默认） | 配置了Web先查询Web；其不可用时先本机快照，再Core上游。未配置Web可直连VNDB |
| `web` | 强制Web，错误原样报告，不直连VNDB回退 |
| `vndb` | 显式直连公开VNDB，保存本机快照／待导入批次 |
| `cache` | 只读当前服务／offline作用域已有查询JSON，缺失报告错误，不联网 |

Web本身先查库，缺失才联网并持久化。Web权限、冲突、协议、上游失败／限流和有效空结果不触发自动绕过Web。返回`source`为`library`、`query_cache`或`vndb`；本机缓存另有`pending_import`／验证状态，不应仅凭source推断Web已接收。

### 通用参数

| 参数 | 用法 |
| --- | --- |
| `--json` | stdout输出结构化响应，snake_case |
| `--output`／`-o` | 写入新文件，拒绝覆盖；metadata查询转换成可导入批次 |
| `--timeout` | 单次HTTP超时1–300秒，默认30；重试退避另计 |
| `--limit` | 默认10，本地浏览1–100；经Web联网最多20，显式Core直连仍受上游约束 |
| `--page` | 从1开始，只查询所选页，不自动取全库 |
| `--config`、`--server`、`--cache-dir` | 上述连接与JSON作用域覆盖 |

错误主要写stderr，脚本以退出码判断成功，不能仅看stdout是否非空。`--output`不保证适用于所有管理命令，见整理部分。

## 4. JSON导出与同步

在本机私有工作目录保存新文件，例如：

```powershell
vndb vn get v17 --source vndb --output ./example-metadata.json
vndb sync --server http://nas.local:5187/ --json
```

`--json`输出MetadataResponse供脚本读取，`--output`对元数据输出`vn-library-batch`。上游结果即使未指定输出，也保存当前作用域快照和不可变pending，原件不因回执而删除。自动查询同步最多顺带处理少量待提交文件，显式`sync`处理当前Web作用域；需要正确密钥。

来源选择只决定查询路由。配置了Web且当前进程有密钥时，显式`--source vndb`成功后也可能顺带同步pending；只做独立上游查询时不提供Web密钥。

无服务时产生的offline JSON不会猜测绑定到未来某个NAS；可以导出至Web「JSON导入」手工预览，或按已证明来源迁移。换地址／实例、摘要或回执不匹配时保留待确认项；不要复制pending到另一server哈希目录或修改batch_id强行同步。

Web默认每文件8MiB／500项，以`/api/v1/info`发现为准，HTTP信封另允许1MiB；本地JSON和响应仍有独立8MiB限制。只拆分未提交的新清单，已封存／结果不明批次沿原ID重试，不自动拆分或重发新ID。接收排队不等于补全完成。

## 5. 设备、存在标记与旧查询数据

```powershell
vndb device show --json
vndb device register --server http://nas.local:5187/ --json
vndb query-data status --server http://nas.local:5187/ --json
vndb query-data preview --server http://nas.local:5187/ --json --output ./query-preview.json
vndb query-data apply --server http://nas.local:5187/ --preview-json ./query-preview.json --approve <预览SHA256> --json
```

show创建／读取本机随机稳定身份，不扫描游戏。`device claim`仅为历史兼容命令，当前Web未提供claim路由，不作为本版可用认领流程；不要用它修复本机映射。

`query-data preview`只读来源证明，未知pending留原处；apply按批准摘要复制已证明来源JSON并留清单，不删／覆盖旧原件。`--legacy-directory`可明确旧查询根。

存在标记命令不会扫描游戏：

```powershell
vndb presence status --server http://nas.local:5187/ --json
vndb presence retry --server http://nas.local:5187/ --json
vndb presence recheck --server http://nas.local:5187/ --json --output ./presence-preview.json
vndb presence apply --server http://nas.local:5187/ --preview-json ./presence-preview.json --approve <预览SHA256> --json
```

明确拒绝才允许重新核对并封存旧拒绝原件；apply保存替代意图，不自动提交，随后retry。结果不明始终原批次重试。`presence`／`organize`／`device`的`--identity-dir`用于明确隔离状态，普通用户不要意外创建第二设备身份。

## 6. 本机登记与整理工作流

以下`F:\VN`均为合成范围，不能直接当真实操作授权。先选定范围／目标并人工核对；输出放游戏目录外的私有工作区。

```powershell
# 默认只预览；确认相同输入后追加--confirm才登记，不移动／启动。
vndb organize register --directory "F:\Unsorted\Example Game" --title "Example Game" --company "Example Studio" --item-json ./chosen-item.json --json
vndb organize register --directory "F:\Unsorted\Example Game" --title "Example Game" --company "Example Studio" --item-json ./chosen-item.json --confirm --json
vndb organize registration-sync --server http://nas.local:5187/ --json
```

`--item-json`接受明确选定Web条目或单条metadata／查询导出；不能模糊多选首项。合集用`--components-json`提供RegistrationComponent数组，不能与item-json并用；每组件有`work`、`relative_path`、`kind`和可选label，work为RegistrationSelection（title、可选item／metadata／company）。`registration-export <local-item-id>`导出公开活动JSON，不带物理路径，供Web活动导入。完整登记细节以`organize register --help`及[桌面登记](DESKTOP.md)为准。

| 命令 | 作用 |
| --- | --- |
| `organize scan <scope> --depth 3` | 只读发现目录候选，已知完整合集不拆分 |
| `organize package-scan <scope> --depth 3` | 只读按包／镜像文件名列候选及建议查询标题，不解压 |
| `organize package-query <scope> --relative-path ... [--query ...]` | 文件名或人工修正标题走Web优先查询，只输出候选 |
| `organize package-register --scan-root ... --relative-path ... --item-json ... [--confirm]` | 明确选择单作品／精确条目，预览并确认本机包关联 |
| `organize packages` / `organize package-verify` | 本机清单／明确核对元数据；不创建启动安装 |
| `organize inspect <directory> [--query ...]` | 读取PE／配置／说明／注册线索，不执行；查询候选不等于自动确认 |
| `organize audit --scope ... --target-root ...` | 核对已有本机安装／组件／启动线索，输出只存本机 |
| `organize installation <id>` | 读本机安装及全部组件，可导出规划快照 |
| `organize plan --installation <id> --scope ... --target-root ...` | 只读计划，可用installation-json替代，选填company／title |
| `organize execute <plan-json> --approve <SHA256>` | 批准后同卷直接rename，保存本机回执 |
| `organize resume --run-directory ...` | 验证原意图后续跑，不重复移动已完成目录 |
| `organize cancel --run-directory ... --approve <SHA256>` | 只撤销尚未移动且身份匹配的计划 |
| `organize restore-plan --run-directory ...` | 生成当前完整目录的复原预览 |
| `organize restore --run-directory ... --plan-json ... --approve <SHA256>` | 按新批准摘要完整回迁，保留新增存档 |

plan、restore-plan、query-data／presence预览的批准SHA在stderr显示，必须核对后原样使用，不是输出文件字节SHA。执行／续跑／复原类用`--json`重定向到本机私有日志，不使用`--output`；证据已另行持久化。管理输出不能放游戏物理目录。扫描／登记／整理JSON可能含路径，不得当metadata或activity上传Web。

整理保持`目标根/会社/游戏兼容日文名`、既有会社布局及完整内部组件。只同卷rename，不复制／删游戏、不创建链接、不合并冲突。实际路径／身份／内容变化使旧计划失效，必须重新预览；详见[命名规则](FILESYSTEM-NAMING.md)。

## 7. 安装包登记

安装包示例（均为合成范围）：

```powershell
vndb organize package-scan "F:\VN-Packages" --depth 3 --json
vndb organize package-scan "F:\" --depth 6 --outside-installations --json
vndb organize package-query "F:\VN-Packages" --relative-path "Studio/Game.7z" --server http://nas.local:5187/ --json
# 从候选中明确选出一条metadata或书架条目另存chosen-item.json，不把整个带路径的扫描报告上传Web。
vndb organize package-register --scan-root "F:\VN-Packages" --relative-path "Studio/Game.7z" --item-json ./chosen-item.json --json
vndb organize package-register --scan-root "F:\VN-Packages" --relative-path "Studio/Game.7z" --item-json ./chosen-item.json --confirm --json
vndb organize registration-sync --server http://nas.local:5187/ --json
```

包清单、检索根与相对路径只留本机；同一作品的版本须选精确条目。核对文件只读，未知观测不会直接撤销存在标记。支持常见压缩包和镜像，分卷只建议首卷，不判断是否齐全，不解压或执行。资源位置与未知观测规则见[数据与隐私](DATA-AND-PRIVACY.md)。

全盘查找未安装资料时，可用`--outside-installations`跳过本机JSON中现存且标为present的完整安装目录，避免重复遍历引擎资源和把游戏内补丁作为独立安装包。默认扫描行为不变；需要检查游戏内部资料时不加此选项。

## 8. 退出码与故障处理

| 码 | 含义 |
| --- | --- |
| 0 | 成功 |
| 1 | 意外错误保留码 |
| 2 | 参数、平台或操作前提错误 |
| 3 | 按ID查询未找到 |
| 4 | 网络失败或离线缓存缺失 |
| 5 | API／协议／鉴权／冲突／导入校验错误 |
| 6 | 文件、权限、输出或数据无效 |
| 130 | 取消 |

401核对密钥，409保留原稿并重读，429遵循Retry-After／原请求重试；网络失败不删除pending。文件已存在另选新输出名，损坏JSON不能重置为空。代理可通过标准环境配置，不能将Web管理密钥发送VNDB。`authinfo`是可选令牌诊断，正常使用不需要它。

## 9. 语言与发行版本

使用 `vndb vn releases v17 --json` 获取已收录发行版本，`--refresh` 明确刷新；同样支持 `--source web/vndb/cache/auto` 和 `--output`。Web不可达时沿用Core查询与JSON缓存/待导入流程，旧Web没有该能力时明确报错，不隐式绕过。实际安装版本由用户核对；未知目录不显示为零版本。

## 10. 一次性本机观测

`observe` 只操作本机元数据与JSON，不联网、不提供常驻watch命令，不移动、删除、安装或启动游戏。修改范围前退出桌面客户端，使用与桌面相同的数据根；省略 `--identity-dir` 为默认用户文档目录。

```powershell
vndb observe root-add --root "E:\SyntheticGames" --kind games --depth 2 --identity-dir "D:\SyntheticClient" --json
vndb observe check --identity-dir "D:\SyntheticClient" --json
# 缺失需要间隔至少10秒后再次check；新候选约15秒稳定后等待人工匹配
vndb observe candidates --identity-dir "D:\SyntheticClient" --json
vndb observe status --identity-dir "D:\SyntheticClient" --json
```

示例目录必须替换为实际存在、明确管理的本机NTFS目录。kind 为 games／packages；深度1–3，32个管理根上限，不能嵌套、选择盘根或包含客户端数据根。root-add 捕获目录／卷身份，保留桌面的启用开关状态，不在CLI创建后台宿主。check 只保存实际变化；输出含本机候选和观测信息，只能存放在库外私有位置，不能拿来导入Web。没有变动不会重复推进安装修订；Unknown不当作卸载，现有 presence 命令继续负责同步。


## 个人分类与本机资源档案

`personal status`读取当前服务作用域的分类快照／离线稿，`personal pull`读取Web，`personal queue --change-json change.json`登记明确变更而不联网，`personal sync`按原操作同步。使用既有`--server`／`--cache-dir`选择与桌面相同作用域；`VN_LIBRARY_KEY`提供Web管理密钥。明确冲突后可用`personal pull --resolve-conflict`保留旧稿并读取Web，结果不明不能放弃原操作。

`change.json`内容为`{"expected_revision":0,"label":{"id":"my-list","name":"想玩","kind":"collection"}}`；状态ID可由`personal status --json`核对。成员修改示例：`{"expected_revision":1,"items":[{"item_id":"item-example","favorite":true,"label_ids":["my-list"]}]}`。只对准确个人条目分类，不使用物理路径或VN ID。

`resources list --identity-dir <本机数据根> [--item <个人条目ID>]`读取本机档案；`resources save --record-json resource.json`保存明确资源，修改时加`--id`与`--expected-revision`。输入包含`item_id`、`kind`、`root_path`、根内`relative_path`，其他版本／来源／核对字段可选；目标条目须已有本机登记。`resources check --id <档案ID>`仅检查元数据，`resources hash --id <档案ID> --expected-revision <修订>`明确计算所选文件SHA256（Ctrl+C可取消）。这些命令不上传位置、不解压、不执行或删除文件；JSON输出含本机资料，应存放库外且不得写回游戏源目录。
