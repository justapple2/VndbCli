using System.Text.Json;

namespace Vndb.Contracts;

public sealed record LibraryItemData(string Title, string? NovelId = null, string? CompanyHint = null,
    string? TitleOverride = null, string? CompanyOverride = null, string? Notes = null, string? ExtraNotes = null);
public sealed record LibraryItem(string Id, LibraryItemData Details, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null);
public sealed record ItemState(Vndb.Core.PersonalProgress Progress, bool IsLocal,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LatestStatus = null);
public sealed record LibraryItemView(LibraryItem Item, MetadataSnapshot? Metadata, int PlayCount,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ItemState? State = null);
public sealed record CollectionStatistics(int PublicNovels, int PersonalItems, int Played, int Unplayed,
    int Unrecorded, int NeedsReview, int LocalItems, string Scope = "collection");
public sealed record ItemPage(IReadOnlyList<LibraryItemView> Items, int Page, int PageSize, int Total, bool More);
public sealed record ItemWrite(LibraryItemData Details, long? ExpectedRevision = null);
public sealed record RevisionRequest(long ExpectedRevision);
public sealed record ItemLink(string? NovelId, long ExpectedRevision);
public sealed record PersonalPlayData(string Title, string? NovelId, string Status, DateOnly? StartedOn,
    DateOnly? FinishedOn, int? DurationMinutes, decimal? Score, string? Review, string? Notes, string? ExtraNotes, string? Language);
public sealed record PersonalPlayWrite(PersonalPlayData Details, string? ItemId = null, long? ExpectedRevision = null);
public sealed record ImportLimits(int MaximumBytes = 8 * 1024 * 1024, int MaximumItems = 500)
{
    // Payload limit is advertised unchanged. HTTP preview wrappers have a separate, fixed allowance.
    public const int EnvelopeAllowanceBytes = 1024 * 1024;
    [System.Text.Json.Serialization.JsonIgnore] public long MaximumRequestBytes => (long)MaximumBytes + EnvelopeAllowanceBytes;
    public ImportLimits Validate()
    {
        if (MaximumBytes is < 1024 or > 32 * 1024 * 1024 || MaximumItems is < 1 or > 5000)
            throw new ArgumentException("无效导入限额；字节须为1024–32MiB，项数须为1–5000。");
        return this;
    }
    public void RequireBytes(long bytes)
    {
        Validate();
        if (bytes > MaximumBytes) throw new ImportValidationException("import_too_large", 413,
            $"导入超过服务端{MaximumBytes}字节限制；请减少新文件条目再导入。已发送或待确认批次保留原ID／原件，需恢复兼容限额后重试。");
    }
    public void RequireItems(int count)
    {
        Validate();
        if (count > MaximumItems) throw new ImportValidationException("import_item_limit", 400,
            $"导入超过服务端{MaximumItems}项限制；请将未提交的新资料分批。已发送或待确认批次保留原ID／原件，需恢复兼容限额后重试。");
    }
}
public sealed class ImportValidationException(string code, int status, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public sealed record ImportPreviewRequest(JsonElement Payload, bool AddToLibrary = false);
public sealed record NormalizedMetadata(string OperationId, string? VndbId, string Title, MetadataSnapshot? Snapshot,
    string InputKind, string? Error = null);
public sealed record ImportItemResult(int Index, string OperationId, string? VndbId, string Title,
    string Status, string? Message = null, string? ItemId = null, string? CandidateId = null, string? EnrichmentId = null);
public sealed record ImportBatchReceipt(string Id, string PayloadSha256, string? ClientBatchId, string State,
    bool Accepted, int Total, int Completed, IReadOnlyList<ImportItemResult> Items, DateTimeOffset CreatedAt,
    string ServiceInstanceId = "", string? RawSha256 = null);
public sealed record ImportCandidate(string Id, string Kind, string? VndbId, string? ItemId, long ExpectedRevision,
    MetadataSnapshot? Incoming, IReadOnlyList<MetadataSnapshot> Matches, string State, string Message);
public sealed record CandidateDecision(long ExpectedRevision, string? NovelId = null);
public sealed record MetadataProvenance(string VndbId, string Source, DateTimeOffset FetchedAt,
    string Completeness, long Revision, string? BatchId, DateTimeOffset? VerifiedAt);
public sealed record MetadataJob(string Id, string? VndbId, string Title, string? ItemId, string State,
    int Attempts, DateTimeOffset NextAt, string? Error, string? CandidateId = null, long? ItemRevision = null);
public sealed record ClientBatchReceipt(string ClientBatchId, string PayloadSha256, string Server,
    ImportBatchReceipt Receipt, DateTimeOffset ReceivedAt, string? LocalSha256 = null);
