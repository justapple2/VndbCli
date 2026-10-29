using System.Text.Json;
using Vndb.Core;

namespace Vndb.Contracts;

/// <summary>The typed remote protocol consumed by local journals; no filesystem capability.</summary>
public interface ISyncApi
{
    Task<ServiceInfo> OrganizationInfoAsync(CancellationToken ct);
    Task<LibraryItemView> ItemAsync(string id, CancellationToken ct = default);
    Task<ItemPage> ItemsAsync(int page = 1, CancellationToken ct = default);
    Task<ItemPage> RemovedItemsAsync(int page = 1, CancellationToken ct = default);
    Task<PersonalPlayView> PersonalPlayAsync(string id, CancellationToken ct = default);
    Task<PersonalPlayView> SavePersonalPlayAsync(PersonalPlayWrite request, string? id, string operationKey, CancellationToken ct = default);
    Task<ActivityReceipt> SubmitActivityAsync(ActivityBatch batch, CancellationToken ct = default);
    Task<PresenceHead> PresenceHeadAsync(string deviceId, CancellationToken ct = default);
    Task<PresenceReceipt> SubmitPresenceAsync(PresenceBatch batch, CancellationToken ct = default);
    Task<ImportBatchReceipt> SyncBatchAsync(JsonElement payload, string operationKey, CancellationToken ct);
}
public sealed class ClientApiException(string code, int status, string? detail = null) : Exception($"资料库请求失败：{code} (HTTP {status})。" + (detail ?? (code switch
{
    "import_too_large" or "request_too_large" or "import_item_limit" => "请按Web公布的字节／项数限制拆分未提交的新资料；原批次和回执保留，待确认批次需恢复兼容限额后按原ID重试。",
    "invalid_json" => "请检查JSON格式或重新导出，原件保留。",
    "import_kind_mismatch" => "metadata使用Web的JSON导入入口，活动JSON使用会话页。",
    "response_too_large" => "响应超过客户端独立读取上限，请缩小查询范围或页大小。",
    _ => ""
})))
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public sealed class WebUnavailableException : CatalogUnavailableException;
