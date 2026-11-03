using Vndb.Core;

namespace Vndb.Contracts;

// Public wire types contain no storage paths, database entities or credentials.
public sealed record CoverInfo(string Status, string? Url = null, string? ContentType = null, string? Sha256 = null);
public sealed record MetadataSnapshot(string VndbId, string Title, string Kind, VisualNovel? Novel,
    IReadOnlyList<Producer> Developers, Producer? Producer, CoverInfo Cover, DateTimeOffset FetchedAt,
    string Provenance, string Completeness = "full_text");
public sealed record MetadataResponse(IReadOnlyList<MetadataSnapshot> Items, string Source,
    DateTimeOffset? FetchedAt, int Page, int PageSize, int? Total, bool More,
    bool Partial = false, bool NeedsRemote = false, bool PendingImport = false,
    string VerificationStatus = "vndb_metadata", IReadOnlyList<string>? Warnings = null);
public sealed record MetadataQuery(string Query = "", string? ProducerId = null, int Page = 1,
    int PageSize = 10, string Mode = "local_first");
public sealed record ServiceInfo(string ServiceInstanceId, IReadOnlyList<int> ApiVersions,
    string BuildVersion, int SchemaVersion, bool WriteKeyRequired, IReadOnlyList<string> Capabilities,
    IReadOnlyList<int> MetadataFormatVersions, bool MetadataImportAvailable, ImportLimits? ImportLimits = null);
public sealed record LocalStatistics(int Novels, int Producers, int Covers, int CoverFailures, string Scope = "library");
public sealed record CoverRepairRequest(IReadOnlyList<string> ItemIds);
public sealed record CoverRepairResult(string ItemId, string? VndbId, string State, string? JobId = null);
public sealed record ApiProblem(string Code, string Title, int Status, string? TraceId = null);
public sealed record MetadataBatch(IReadOnlyList<MetadataSnapshot> Items, DateTimeOffset ExportedAt,
    string BatchId, string Format = "vn-library-batch", int FormatVersion = 1, string Kind = "metadata");

public static class QueryRules
{
    public static MetadataQuery Validate(MetadataQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.ToCatalog("vn_search").Validate(requireText: false).ToMetadata();
    }
    public static MetadataResponse Response(IReadOnlyList<MetadataSnapshot> items, string source, MetadataQuery query,
        int? total, bool more, bool partial = false, bool needsRemote = false) =>
        new(items, source, items.Count == 0 ? null : items.Min(i => i.FetchedAt), query.Page, query.PageSize,
            total, more, partial, needsRemote, VerificationStatus: items.Any(i => i.Provenance == "client_vndb_import") ? "client_snapshot" : "vndb_metadata");
}
