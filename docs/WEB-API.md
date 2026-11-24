# Web API使用与交换契约

适用版本0.6.1／API v1／schema15。**客户端使用`/api/v1`**，机器规范为运行中`/openapi/v1.json`；数据库schema不是客户端协议。示例使用虚构个人资料和`http://nas.local:5187/`，请替换为自己的服务。封面修复与官方更新分别通过`cover_repair`、`dataset_sync`可选能力协商。

封面补全：`POST /api/v1/metadata/covers/repair`，请求`{"item_ids":["item-example"]}`（1–100个唯一且未移除的个人书架ID），沿用管理密钥、`X-Library-Request: 1`与`Idempotency-Key`。返回每项`item_id`、`vndb_id`、`state`和可选`job_id`。已有封面／确认无图／未关联分别为`ready`／`no_cover`／`unlinked`；其余复用原metadata队列，按VN ID去重。明确重试可重新排队已结束的失败任务，已有暂停任务保持暂停；用既有jobs入口查看和控制。请求不改个人关联、修订或游玩数据，也不遍历公共全库。网络补全按既有至少5秒间隔及有限退避执行，返回queued不代表图片已下载。

## 1. 连接、鉴权及公共规则

只读GET通常不需密钥；所有`/api`下POST／PUT／PATCH／DELETE需：

```http
Content-Type: application/json
X-Library-Request: 1
X-Library-Key: <应用管理密钥>
Idempotency-Key: <本次操作稳定唯一键>
```

Idempotency-Key用于带该参数的业务变更；metadata查询／refresh主要按规范ID持久化，活动批次及会话另有自身稳定身份，不能假定所有POST都由请求头统一去重。密钥只发送配置Web origin，不随重定向发送，不给VNDB。浏览无账号隔离，部署边界是可信局域网，见[Docker](DOCKER.md)。

Content-Type仅在有JSON请求体时设置。无业务请求体的resolve／commit／pause等不要发送空的application/json，否则空JSON会被校验拒绝；业务写请求头仍须提供。

JSON为snake_case；时间戳ISO8601，事件按UTC保存，日历日期`YYYY-MM-DD`。未知字段值使用null，不能改为0／空数组来伪装未知。可选null字段部分省略，按OpenAPI／Contracts处理。分页从1开始；一般本地每页1–100，联网查询最多20，结果`total`可为null，继续翻页看`more`。

`GET /healthz`返回健康摘要；`GET /api/v1/info`返回：

| 字段 | 含义 |
| --- | --- |
| `service_instance_id` | 原数据卷的稳定服务身份；恢复后应保持 |
| `api_versions`、`build_version`、`schema_version` | 分别为协议版本、产品版本和服务端schema |
| `write_key_required` | 是否要求应用写密钥；生产应为true |
| `capabilities` | 当前已实现能力，不表示提供本机路径接口 |
| `metadata_format_versions`、`metadata_import_available` | 元数据格式与导入支持 |
| `import_limits.maximum_bytes`、`maximum_items` | 活跃导入限额 |

查询读取保持本库优先：本库命中和 `local_only` 不等待其他联网查询；同一缺失查询进入联网段后复核本库，并复用已有查询结果。`explicit_refresh` 仍明确访问上游，不绕过原有节流、鉴权或错误处理。进程负结果缓存有界且保留有效期，协议／联网失败不会保存为空结果。

## 2. 元数据查询

以下路径均相对`/api/v1`：

| 方法与路径 | 输入／输出 |
| --- | --- |
| `GET /info` | ServiceInfo |
| `GET /stats` | LocalStatistics：novels／producers／covers／cover_failures／scope=library |
| `GET /vns` | `q`、`producer_id`、`page=1`、`page_size=10`；MetadataResponse，本地只读 |
| `GET /vns/{id}` | v开头ID；本地详情，缺失404／not_found |
| `GET /vns/{id}/cover` | 图片、Content-Type、ETag、Range；无封面404，非JSON |
| `POST /queries/vns` | MetadataQuery，本地优先搜索并收录缺失上游资料 |
| `POST /vns/{id}/resolve` | 无业务body，本地优先详情，缺失时联网入库 |
| `POST /vns/{id}/refresh` | 无业务body，显式联网刷新公共资料 |
| `GET /producers` | `q`、`page`、`page_size`；本地会社搜索 |
| `GET /producers/{id}` | p开头ID，本地会社详情 |
| `POST /queries/producers` | MetadataQuery，本地优先会社搜索 |
| `POST /producers/{id}/resolve` | 本地优先会社详情 |
| `POST /producers/{id}/refresh` | 显式刷新会社 |
| `POST /producers/{id}/vns/query` | MetadataQuery，按会社查询作品 |

MetadataQuery示例：

```json
{
  "query": "Example",
  "producer_id": null,
  "page": 1,
  "page_size": 10,
  "mode": "local_first"
}
```

模式支持`local_only`、`local_first`、`explicit_refresh`。先查本库并利用查询缓存，有有效结果可直接返回，不保证补齐VNDB全量；`partial`提示局部资料。联网搜索需非空文字。refresh不修改个人感想／时间。

MetadataResponse字段：`items`、`source`（library／query_cache／vndb）、`fetched_at`、`page`、`page_size`、`total`、`more`、`partial`、`needs_remote`、`pending_import`、`verification_status`及可选`warnings`。本机回退也沿用该结构，但pending与验证状态不同；不能只凭source认定已同步。

### 个人书架的轻量读取

`GET /info` 的 `shelf_summary_v1`、`item_heads_v1` 分别声明以下可选能力；API v1 与原 `/items`、`/plays` 和导出保持兼容，不需升级数据库 schema。能力缺失可使用旧接口；401、协议错误或声明支持后接口出错不能静默降级。

| 方法与路径 | 用途 |
| --- | --- |
| `GET /shelf` | 个人卡片摘要，支持与 `/items` 相同的 q／deleted／unmatched／progress／status／is_local 筛选；page 从1开始，page_size 默认100，最大100 |
| `GET /item-heads` | 权威核对，仅返回 id、revision、deleted；page／page_size／deleted，不返回作品详情或本机路径 |
| `GET /items/{id}` | 打开详情或编辑前获取完整条目，包括备注与作品资料 |
| `GET /plays?item_id={id}` | 打开个人明细时获取该精确条目的记录，不按作品名猜关联 |

`/shelf` 返回 `format="vn-shelf-summary"`、`format_version=1`、items／page／page_size／total／more。items 沿用 LibraryItemView 的身份和展示字段，但不含书架长备注、作品简介、发行目录、关系及别名；`state.progress` 是完整状态聚合，`state.latest_status` 是最近记录状态，`play_count` 是记录数量。摘要缺少这些重字段不代表资料库没有它们，**摘要不可作为完整编辑、元数据导入或备份请求**。重玩不会抹去已玩标记。`/item-heads` 同样返回分页包，items 为 `{id, revision, deleted}`。

Web快捷视图直接组合这些谓词，例如已有且未玩为 `progress=unplayed&is_local=true`，游玩中为 `status=playing`。status匹配任一有效关联经历，progress使用整个条目的互斥聚合分类；两者和存在布尔值可同时指定。没有安装、压缩包路径或启动就绪的Web筛选参数，这些信息仅归本机客户端管理。

每个MetadataSnapshot含`vndb_id`、`title`、`kind`（vn／producer）、`novel`、全部`developers`、可选`producer`、`cover`、`fetched_at`、`provenance`、`completeness`。cover含status、Web可访问url、content_type、sha256，不包含NAS文件位置。不同公司角色不等价于唯一主会社，未知发行日期不强制转完整日期。

### 最小查询示例

```powershell
$base = 'http://nas.local:5187'
Invoke-RestMethod "$base/api/v1/info"
Invoke-RestMethod "$base/api/v1/vns/v17"
# 本地缺失才使用resolve；密钥由安全入口设置到当前进程，勿写字面值。
$headers = @{ 'X-Library-Request'='1'; 'X-Library-Key'=$env:VN_LIBRARY_KEY }
Invoke-RestMethod "$base/api/v1/vns/v17/resolve" -Method Post -Headers $headers
```

联网类入口共享每分钟20次的Web固定窗口限制，429含Retry-After。Core还有独立串行节流和上游重试；不能通过多实例／并行请求绕过限制。

## 3. 个人书架与游玩

ItemId是个人书架稳定ID，NovelId是可空VNDB ID；PlayId是一条游玩／重玩经历。不得因标题或同VNDB ID就合并不同版本、条目或重玩。

| 方法与路径 | 输入／输出 |
| --- | --- |
| `GET /items` | q、deleted=false、unmatched=false、page=1、page_size=24；ItemPage |
| `GET /items/{id}` | LibraryItemView：item／metadata／play_count |
| `POST /items` | ItemWrite创建，返回LibraryItem |
| `PATCH /items/{id}` | ItemWrite，必须当前expected_revision |
| `DELETE /items/{id}` | 查询参数expected_revision，软移除 |
| `POST /items/{id}/restore` | RevisionRequest，恢复原条目 |
| `POST /items/{id}/link` | ItemLink：novel_id、expected_revision |
| `GET /plays` | 可选item_id、deleted=false；PersonalPlayView数组，无通用分页 |
| `GET /plays/{id}` | 精确经历，PersonalPlayView |
| `POST /plays` | PersonalPlayWrite创建／新建重玩 |
| `PATCH /plays/{id}` | PersonalPlayWrite修改准确经历 |
| `DELETE /plays/{id}` | 查询参数expected_revision，软移除 |
| `POST /plays/{id}/restore` | RevisionRequest |

ItemWrite为`{ "details": { ... }, "expected_revision": null }`。LibraryItemData字段：title必填，novel_id、company_hint、title_override、company_override、notes、extra_notes可空。更新发送完整保留字段，不把PATCH理解为逐属性JSON Patch；缺省null可能清空原值。

PersonalPlayWrite含details、item_id、expected_revision。details字段与规则：

| 字段 | 约束 |
| --- | --- |
| title、novel_id | title为1–500字符；novel_id可空，规范v ID |
| status | not_started／playing／played／completed／on_hold／dropped |
| started_on、finished_on | 可空日期，结束不能早于开始 |
| duration_minutes | 可空非负整数，历史／手工基数 |
| score | 可空0–10，最多一位小数 |
| review | 可空，最多20000字符 |
| notes、extra_notes | 两栏备注，各最多10000字符 |
| language | 可空，最多40字符 |

not_started不允许实际游玩日期或正时长。v1 PersonalPlayView保留revision、时间、删除状态、has_played、可选ItemId及Time投影，不含Excel／工作表Source。未知ItemId保持缺失，不猜测经历归属。

创建例，替换真实ItemId后发送；`example-*`均为示意，不能当已有资源ID：

```json
{
  "details": {
    "title": "Example Game",
    "novel_id": null,
    "status": "playing",
    "started_on": "2026-10-28",
    "finished_on": null,
    "duration_minutes": null,
    "score": null,
    "review": null,
    "notes": null,
    "extra_notes": null,
    "language": "ja"
  },
  "item_id": "example-item-id",
  "expected_revision": null
}
```

写入前读当前revision，保持原稿与稳定Idempotency-Key；同键同内容返回原结果，不同内容冲突。409返回ApiProblem，不承诺正文附当前记录，客户端须主动GET并展示比较。软移除不删除游戏，重新导入也不应自动复活被移除个人项。

字段对照复用这些既有GET与个人分类GET，不增加比较／历史／诊断写入接口，API v1保持兼容。原始编辑基准只保存在客户端；GET观测值不作为写入回执，也不改变原请求内容、修订、关联或幂等键。明确拒绝后保留原稿再重读编辑；结果不明按原键原内容重试，不能用“读到相同值”推定已确认。

## 4. 元数据导入／队列

| 方法与路径 | 输入／输出 |
| --- | --- |
| `GET /imports` | 已有批次摘要列表 |
| `POST /imports/preview` | ImportPreviewRequest：payload、add_to_library=false；返回ImportBatchReceipt |
| `GET /imports/{id}` | 批次／逐项回执 |
| `POST /imports/{id}/commit` | 提交已预览批次，稳定幂等键 |
| `POST /imports/{id}/pause` | 暂停批次 |
| `POST /imports/{id}/resume` | 恢复批次 |
| `POST /sync/batches` | 直接提交metadata payload，预览／提交复用同一流程 |
| `GET /sync/batches/{id}` | 同类批次回执 |
| `GET /metadata/candidates` | 待核对候选数组 |
| `POST /metadata/candidates/{id}/accept` | CandidateDecision：expected_revision、可选novel_id |
| `POST /metadata/candidates/{id}/reject` | 同模型，明确拒绝候选 |
| `GET /metadata/jobs` | MetadataJob数组 |
| `POST /metadata/jobs/{id}/{action}` | action仅pause／resume／retry，稳定幂等键 |
| `GET /metadata/{id}/provenance` | MetadataProvenance或404 |
| `GET /schemas/metadata-batch-v1.json` | 元数据JSON Schema |

metadata信封示例，只含规范ID时进入补全流程，不冒充完整快照：

```json
{
  "format": "vn-library-batch",
  "format_version": 1,
  "kind": "metadata",
  "batch_id": "example-metadata-001",
  "exported_at": "2026-10-28T00:00:00Z",
  "items": [{ "vndb_id": "v17" }]
}
```

batch_id在该输入中可选，客户端使用稳定值，长度不超过160，仅ASCII字母数字及`-_.`。条目至少有规范ID或标题。完整Core快照还需novel／producer等结构及可信图片信息；建议由CLI／桌面生成，不能手工填一个title就声明full_text。离线完整快照标记client_vndb_import，来源声明不是认证；与现存资料有差异进入候选，不按客户端时间无条件覆盖。

preview请求外包一层`{ "payload": <上述对象>, "add_to_library": false }`。预览会持久保存不可变批次及证据，**不是纯无副作用校验**；commit才推进业务导入。sync直接发送上述payload，不再套payload。默认导入8MiB／500项，以info为准，预览HTTP信封另允许1MiB；空／格式错误／错误种类不能当网络失败。

ImportBatchReceipt：id、payload_sha256、client_batch_id、state、accepted、total、completed、items、created_at、service_instance_id、raw_sha256。逐项含index、operation_id、vndb_id、title、status、message，以及可选item_id／candidate_id／enrichment_id。accepted表示请求已接收，completed／逐项status才反映处理进度；queued、needs_review等不是“全部完成”。

客户端确认必须匹配origin／实例、原批次ID、实际wire原始摘要、规范化摘要、数量及逐项身份／状态。不能把磁盘文件SHA或自行格式化后的JSON摘要替代wire证明；规范化算法使用共享Contracts.MetadataPayload，别自行再写一套。证明不足保持待确认，原操作重试，不清pending或换ID。回执可因同内容去重复用原存储批次，仍须证明本次输入。

## 5. 设备与本机存在布尔值

| 方法与路径 | 输入／输出 |
| --- | --- |
| `GET /devices` | LibraryDevice数组 |
| `POST /devices` | DeviceWrite：id、details{name,platform}、expected_revision |
| `POST /devices/{id}/seen` | RevisionRequest，本次连接标记 |
| `GET /presence` | PublicPresence数组：novel_id、is_local，多设备聚合 |
| `GET /presence/devices/{id}` | PresenceHead：device_id、revision、items |
| `POST /presence` | PresenceBatch，返回PresenceReceipt |

设备ID是本机随机稳定GUID，不用机器名推断身份。当前没有`/devices/{id}/claim`端点；历史CLI兼容命令不代表Web提供它。

```json
{
  "format": "vn-library-presence",
  "format_version": 1,
  "id": "00000000-0000-4000-8000-000000000101",
  "service_instance_id": "00000000-0000-4000-8000-000000000102",
  "device_id": "00000000-0000-4000-8000-000000000103",
  "expected_revision": 0,
  "items": [{ "item_id": "example-item-id", "is_local": true }]
}
```

示例GUID须替换真实身份／新批次。items为本次明确更新的1–2000个唯一ItemId布尔值，未列出项不会自动清空。实例不符、当前设备head revision不符、条目被移除返回冲突；回执含id、实例、payload_sha256及新revision。只有明确拒绝才能经重新核对替换批次，不明结果原ID重试。

## 6. 活动、会话与时间

| 方法与路径 | 输入／输出 |
| --- | --- |
| `POST /activity/batches` | WebActivityBatch，返回ActivityReceipt |
| `GET /activity/batches/{id}` | 回执或404 |
| `GET /play-sessions` | 可选play_id，PlaySession数组 |
| `POST /play-sessions` | SessionSample，按session ID＋sequence去重 |
| `POST /play-sessions/{id}/decision` | expected_revision、include，稳定幂等键 |
| `GET /plays/{id}/time` | PlayTimeSummary |
| `POST /time-adjustments` | id、play_id、seconds、reason、expected_revision，稳定幂等键 |
| `GET /play-edit-conflicts` | 个人编辑冲突数组 |
| `POST /play-edit-conflicts/{id}/decision` | expected_revision、play_revision、use_incoming，稳定幂等键 |

WebActivityBatch格式为`vn-library-activity`／format_version=1／kind=play_activity，必需batch_id、device_id、device{name,platform}；可带metadata、items、plays、play_sessions、play_edits。installations兼容槽只能为空，不得上传安装树；未知顶层字段拒绝。推荐使用客户端导出，不手工重建对象摘要。

items为`{client_id,details}`，plays为`{client_id,item_ref,details}`，play_edits为`{id,play_ref,expected_revision,details}`。引用可为真实服务端ID或明确`client:<客户端创建键>`；先按回执映射条目／经历，再处理依赖会话／编辑，不能猜另一重玩ID。回执含batch_id、payload_sha256、service_instance_id、results、complete、received_at；逐项kind/key/status/server_id和可选current/incoming。HTTP200也可能有dependency_pending／conflict，必须检查逐项结果。去重依赖批次／操作身份，不是仅靠Idempotency-Key头。

SessionSample含id、device_id、play_id、started_at、checkpoint_at、ended_at、sequence、cumulative_seconds、state、reason、needs_review。Web的installation_id、component_id必须null／省略。序号从1起，累计秒数不回退；state为active／paused／ended／interrupted／review，终态必须有ended_at。身份、开始时间及经历绑定不能改变；旧序号不回退，终态不能被迟到心跳重新开启。同设备只能有一个活动会话，跨设备重叠可能待人工核对。

PlayTimeSummary含play_id、base_minutes、session_seconds、adjustment_seconds、total_seconds、pending_sessions、revision。历史基数未知则保持null，会话累计与调整不改写原分钟；TimeAdjustment可正负秒，需原因及当前**时间汇总**revision，不是个人经历revision。人工计入／排除针对准确已结束会话，不把运行中样本当最终时间。

## 7. 资料质量及隐私退役入口

`GET /api/v1/quality`只读，参数device_id、kind、q、page=1、page_size=30；返回QualityPage（items、counts、total、page、page_size、more），不访问客户端文件系统或执行上游查询。

以下前缀及其子路径一律410／local_only，**不是可用API**：

- `/api/v1/installations`
- `/api/v1/installation-bundles`
- `/api/v1/legacy-locations`
- `/api/v1/archive-runs`
- `/api/v1/quality/installation-audits`
- `/api/archives`
- `/api/catalog/local`

旧`/installations.html`、`/organization.html`跳转质量页。请求含本机目录、EXE、工作目录、工具配置、注册表或文件证据会被隐私边界拒绝；也不能把路径藏进notes／review／query。Web只接受作品信息、个人资料、必要设备／同步身份及存在布尔值。客户端不得把本机安装树作为“数据库备份”上传。

## 8. 错误与重试

v1业务错误通常为`{code,title,status,trace_id}`；部分框架资源404或图片响应可能无该JSON，不能强制反序列化。协议错误不是空书架。

| HTTP／code | 处理 |
| --- | --- |
| 400 invalid_request／invalid_json | 修正格式／参数，保留原稿 |
| 400 local_only_data | 移除本机信息，不变换编码绕过检查 |
| 400 import_kind_mismatch／import_item_limit | 核对导入类型／项数 |
| 401 unauthorized | 核对应用密钥及请求头，不自动直连回退 |
| 404 not_found | 本地资源缺失；需联网时明确resolve，不等于网络失败 |
| 409 conflict | 保留原稿，重读当前状态比较，不自动覆盖 |
| 410 local_only | 使用本机JSON功能，不重启旧位置API |
| 413 import_too_large／request_too_large | payload或信封超限，待确认批次不拆分换ID |
| 429 rate_limited | 按Retry-After重试原请求 |
| 499 cancelled | 取消，勿伪报成功 |
| 502 upstream_unavailable／upstream_protocol | 上游或协议问题，保留本库及待同步数据 |
| 503 upstream_throttled | 遵循上游退避，不额外并发 |
| 500 library_unavailable | 检查服务及数据卷，结果可能不明，幂等重试原操作 |

客户端`response_too_large`是独立响应缓冲错误，不是Web断线。网络中断／成功正文无法证明／写入成功但刷新失败时，不换键重做；先查已有回执或原操作重试。明确业务拒绝后才修改原稿并产生新操作。不要对所有POST设置无条件自动重放。

## 9. 兼容`/api`入口

这些服务现有Web页面，未全部纳入v1 OpenAPI；新集成优先v1，不把旧Library DTO当公开数据模型。旧路由可能使用ProblemDetails或旧分页／Source投影，与v1不同：

| 路径组 | 当前方法及用途 |
| --- | --- |
| `/api/config`、`/api/stats`、`/api/producers` | GET配置／统计／会社 |
| `/api/library` | GET本库列表，q／producer／page／limit |
| `/api/library/{id}`、`/cover`、`/export` | GET旧资料／封面／单条导出 |
| `/api/imports/{id}` | POST按ID入库，refresh可选 |
| `/api/search` | GET本地；POST LibrarySearchRequest(query,producer,page,limit)，本地优先入库 |
| `/api/remote/search` | GET但会联网入库，要求写请求头，标记Deprecation |
| `/api/plays` | GET旧分页筛选；POST创建；`/{id}` GET／PUT；`/stats` GET |
| `/api/plays/imports` | POST旧PlayImportBatch，background可选；不是metadata／activity通用入口 |
| `/api/plays/{id}/queue`、`/enrich` | POST旧资料补全 |
| `/api/catalog`、`/stats` | GET旧补全列表／统计 |
| `/api/catalog/enrich`、`/search`、`/resolve` | POST旧补全／候选／关联 |
| `/api/catalog/jobs` | POST排队；`/latest` GET；`/{id}/pause`、`/resume` POST；`/{id}/export` GET |

旧游玩列表仍是历史投影，可能含工作表Source；第三方个人读取使用v1。旧DTO中用于历史迁移的location类型不意味着存在HTTP上传权限。接口权威来源为[端点代码](../src/Vndb.Web/V1Endpoints.cs)、[管理端点](../src/Vndb.Web/ManagementEndpoints.cs)、[活动端点](../src/Vndb.Web/ActivityEndpoints.cs)、[Contracts](../src/Vndb.Contracts/Metadata.cs)及运行中的OpenAPI。

## 10. 发行版本

/info公布release_catalog能力，以下路由返回MetadataResponse，数据位于items[].novel.release_catalog（items、fetched_at）：

| 路由 | 行为 |
| --- | --- |
| GET /api/v1/vns/{id}/releases | 仅本库；尚未查询404/not_found，完成且无版本返回空目录 |
| POST /api/v1/vns/{id}/releases/resolve | 本库优先，缺失联网并持久化；无请求体 |
| POST /api/v1/vns/{id}/releases/refresh | 明确刷新；无请求体 |

POST沿用写鉴权/限流。单作品每页25条，以r ID游标读取；最多1000条且汇总4MiB，未完成不保存新目录。作品不存在时POST返回空作品集合。字段为id/title/alttitle/released/official/patch、languages(lang/title/latin/mtl/main)、platforms、vns(id/rtype)、producers(id/name/original/developer/publisher)。日期保留部分日期/TBA，未来日期非当前可用承诺。

普通刷新/旧JSON缺少release_catalog不会清掉已有目录；缺失字段不参与序列化及规范哈希。可通过原metadata渠道导入，冲突仍走候选审核，不覆盖个人记录。官方公共资料任务使用下述独立datasets入口。

## 11. 公共目录、个人状态与本地存在

存在包含客户端确认的安装目录或安装包，不表示Web／当前客户端可以启动。包清单、检索根和相对路径不在API模型中。普通本库标题查询和公共目录默认按“曾玩过→已有→其余”全局排序后分页；公共目录可显式`sort=personal`或原有排序。精确ItemId状态先计算，再作VN级浏览汇总；异常关联及移除记录不能获得已玩优先级。详见[数据与隐私](DATA-AND-PRIVACY.md)。

GET /api/v1/vns 和 /stats 的library口径是已收录公共资料，不能当作个人书架数量。公共资料更新/默认metadata导入不创建个人条目或游玩记录；导入时明确选择add_to_library才创建个人条目，仍不自动创建not_started记录。

GET /api/v1/items支持可选筛选：progress=unrecorded/unplayed/played/review，status为原六种状态，is_local=true/false；可与q、unmatched组合，先全个人书架筛选再排序分页。progress=played沿用IsPlayed，包含游玩中/搁置/放弃；不等于completed。status=not_started可命中已有完成经历且另有待玩重玩记录的条目，其progress仍为played。

每个LibraryItemView支持可选state：progress含has_record、has_played、needs_review、statuses和派生state；is_local表示任意客户端对本ItemId上报存在。没有个人经历为unrecorded；明确not_started且没有玩过经历为unplayed；历史关联不一致为review。状态仅按play_item_links的ItemId计算；同VN不同Item不共享个人经历或存在标记。删除的个人条目不计入活跃统计，删除的游玩经历不参与状态。PlayCount只表示活跃记录数，不能判断玩过。

GET /api/v1/collection/stats返回public_novels、personal_items、played、unplayed、unrecorded、needs_review、local_items和scope=collection。四类progress统计互斥且合计为活跃personal_items；local_items是独立维度，不能相加。service/info公布collection_state能力。旧响应没有state时客户端显示状态未读取，不猜测未玩；字段为空时忽略序列化，保留旧本机JSON兼容。

## 官方导出任务

`/api/v1/info`公布`dataset_import`能力。所有POST沿用`X-Library-Request`、`X-Library-Key`和`Idempotency-Key`；其余JSON限制不变。

| API | 请求与行为 |
| --- | --- |
| `GET /api/v1/datasets` | 最近20个任务；只读，不含文件位置、游标或暂存结构 |
| `POST /api/v1/datasets` | `{"snapshot_date":"2026-10-27"}`；日期为示例，只生成固定官方HTTPS地址 |
| `POST /api/v1/datasets/upload` | `application/octet-stream`原始`.tar.zst`，最多512MiB |
| `POST /api/v1/datasets/{id}/pause` | `{"expected_revision":当前修订}` |
| `POST /api/v1/datasets/{id}/resume` | 相同修订请求；暂停／失败后继续，遵守`earliest_resume_at` |
| `POST /api/v1/datasets/{id}/cancel` | 相同修订请求；保留已提交公共资料与原输入 |

响应为`DatasetJob`，含`id/source/state/phase/revision/created_at/sha256/snapshot_at/total/processed/written/kept/download_bytes/error/completed_at/earliest_resume_at`。`queued`只表示接收，完成状态和计数需另行读取；关闭页面不取消任务。

进度更新可能令控制请求返回409，先刷新修订；写入结果不明时保持原请求及幂等键。同包或同日期重复提交返回已有任务，同一时间只允许一个活动任务，包含暂停和失败状态。上传会先验证完整内容身份，相同键不同内容返回409；相同内容重复上传复用已登记任务／输入，未接收临时文件清理。损坏上传只形成失败任务，不执行包内内容。使用、空间与恢复条件见[公共资料目录](CATALOG.md)。

### 容量摘要与手动清理

`info.capabilities` 包含 `dataset_maintenance`。保持 API v1 与 schema15，POST 沿用上述管理鉴权和幂等键，不接收文件路径。

| 接口 | 说明 |
| --- | --- |
| `GET /api/v1/datasets/maintenance` | 数据卷 `available_bytes`、`input_bytes/stage_bytes` 及数量，`completed_snapshot_at`、`active_job`、`wait_reason`；只读，无外网请求 |
| `POST /api/v1/datasets/maintenance/preview` | 无请求体；返回 `id/sha256/entries/reclaimable_bytes`。同键返回原预览；新预览使用新键 |
| `POST /api/v1/datasets/maintenance/cleanup` | `{"preview_id":"预览ID","sha256":"预览证明"}`；返回 `preview_id/removed/released_bytes`；同键重试复用回执 |

`entries` 仅含不透明 `id`、`kind`、`bytes`、可选 `job_id`；kind 为 `unreferenced_input`、`unreferenced_stage` 或 `completed_stage`，不返回服务器目录。生成预览不清理文件。执行前核对引用和文件证明，活动／暂停／失败任务存在或证明变化返回409；原包、数据库、封面及完整恢复材料不在清理范围。清理中断保留原预览与意图，可用同一内容重试，不得修改证明继续执行。

## 官方资料更新与可选调度

`info.capabilities`包含`dataset_sync`。所有POST需要管理密钥、`X-Library-Request: 1`及固定`Idempotency-Key`；保持API v1，不接受下载URL或本机位置。

| API | 请求与行为 |
| --- | --- |
| `GET /api/v1/datasets/sync` | 只读数据库状态，无上游请求 |
| `POST /api/v1/datasets/sync/check` | 无请求体；只对固定官方latest别名发HEAD，严格校验日期化重定向，不跟随下载 |
| `POST /api/v1/datasets/sync/update` | 无请求体；先检查，再为准确日期排入已有导入队列，原键重试不改目标日期 |
| `POST /api/v1/datasets/sync/settings` | `{"expected_revision":0,"enabled":false,"interval_days":7}`；周期1–30天，默认关闭／7天 |

状态包含`settings`（`revision/enabled/interval_days`）、`latest_snapshot_date`、`last_checked_at`（最近成功检查）、`last_attempt_at`、`check_not_before`、`next_check_at`、`error`、`checking`、`completed_snapshot_at`、`completed_at`、`active_job`、`update_available`和`worker_enabled`。尚未检查时`update_available=null`；它比较完整导入与最近成功发现的日期，不代表全部网站内容或全部字段实时一致。失败时保留原日期并显示错误。状态不公开内部检查令牌、暂存或路径。

`update`返回`DatasetSyncResult`：`state`为`queued`（已排队）、`existing`（复用原任务）、`current`（已导入最近检查到的日期）、`blocked`（先处理原任务）、`checking`、`check_failed`或`not_checked`，另含`snapshot_date`和可选`job`。200仅表示操作结果明确；`queued/existing`不表示导入完成。结果不明仍用原键重试，即使官方latest改变也不会重定向原操作；状态GET不能代替写入回执。

检查最短间隔15分钟，限流／维护遵守Retry-After（等待有界在15分钟至1天），普通错误按1小时退避。调度在现有Web worker中执行，先持久预留重试窗口；相同包去重、单活动任务和人工暂停规则沿用。关闭/修改调度使尚未排队的旧调度失效，已接收任务另行控制。`CatalogJobs:Enabled=false`时不运行调度且禁止新启用开关，仍允许关闭已有设置。设置保存只推进配置修订，后台检查不会制造表单修订冲突；409保留原稿并明确重读。

## 公共目录只读浏览

`GET /api/v1/catalog`无需写入密钥，不自动联网补全或创建个人记录；`info.capabilities`包含`public_catalog`。筛选和稳定排序在全库执行后分页：

| 参数 | 默认／限制 |
| --- | --- |
| `q` | 空，最多200字符；标题／日文／别名／罗马字／ID子串 |
| `producer_id` | 空或p开头的VNDB会社ID |
| `language` | 空或发行语言代码，如ja、en、zh-Hans、zh-Hant；最多20字符 |
| `release_type` | 空／complete／trial／partial／patch |
| `platform` | 空或平台代码，如win、lin、mac；最多30字符 |
| `include_mtl` | false；只影响语言筛选 |
| `sort` | personal（默认）／updated／title／released_desc／released_asc；ID稳定决胜，未知发售日期最后 |
| `page`／`page_size` | 1／24；页码1–100000，每页1–48 |

例如：`GET /api/v1/catalog?language=ja&release_type=complete&platform=win&sort=title&page=1&page_size=24`。

返回`PublicCatalogPage`：`items[].metadata`仅为有界卡片文字摘要，不带大段简介、关系或全部发行版；`personal`是个人汇总，`releases_known`区分未知与已查空目录；`coverage`包含公共作品数、最近完整快照日期和未完成任务进度；`observed_at`为页面读取时间。完整资料、发行版及metadata导出使用明细接口，不能将卡片摘要当作完整导出。

`GET /api/v1/catalog/producers?q=名称或p编号`返回最多60项会社ID、名称、原名及关联作品数。非法参数返回400。语言／类型／平台条件须命中同一发行版，合集类型对应当前作品；默认排除机翻，可明确包含。筛选可能包含未来发行和部分日期，不承诺当前可购买或本机版本语言支持。


## 个人收藏、虚拟集合与标签（可选能力）

`GET /info`的`personal_organization`能力声明此功能。`GET /personal-organization`读取轻量个人分类（不含作品详情）：`revision`、`labels[{id,name,kind}]`、`items[{item_id,favorite,label_ids}]`。`kind`为`collection`或`tag`。标签ID稳定；条目使用准确个人书架ID，不能用VN ID替代。

`POST /personal-organization`沿用管理密钥、`X-Library-Request: 1`及`Idempotency-Key`。请求为`{service_instance_id,change:{expected_revision,label?,remove_label?,items?}}`，实例必须与`GET /info`一致。每次只能提供一个`label`（创建／改名）、`remove_label`（解除分类），或1–100项`items`（明确替换这些条目的收藏与分类）。其他条目保持。响应为新分类快照；批次全成或全不成。分类最多200项，名称1–80字，同类型不重名；每条目最多32分类；最多20000条非空分类关系、4MiB。未分类且未收藏的条目不占快照行。

`GET /items`及`GET /shelf`增加可选`favorite=true|false`、`label=<稳定分类ID>`，与原查询组合并在分页前生效。删除分类不移除个人条目、游玩或文件。旧客户端原个人条目CRUD不会清空这些分类。

此小型个人组织使用独立整体修订，两个客户端并发修改不同条目也可能返回409。客户端保留原ID／内容，明确冲突后重读再编辑，不自动覆盖；请求结果不明时原ID重试可取得原回执。分类不改变个人条目修订、关联或游玩状态。本机资源档案无Web接口；路径、EXE、参数、适用本机补丁和手工应用状态仍只在Local JSON。
