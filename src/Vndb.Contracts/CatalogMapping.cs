using Vndb.Core;

namespace Vndb.Contracts;

/// <summary>Public metadata boundary. No installation or other private local model is mapped.</summary>
public static class CatalogMapping
{
    public static CatalogQuery ToCatalog(this MetadataQuery query, string kind, string? id = null) =>
        new(kind, query.Query, id, query.ProducerId, query.Page, query.PageSize, query.Mode);
    public static MetadataQuery ToMetadata(this CatalogQuery query) => new(query.Query, query.ProducerId, query.Page, query.PageSize, query.Mode);
    public static CatalogResult ToCatalog(this MetadataResponse result) => new(
        result.Items.Select(i => new CatalogEntry(i.VndbId, i.Title, i.Kind, i.Novel, i.Developers, i.Producer,
            new(i.Cover.Status, i.Cover.Url, i.Cover.ContentType, i.Cover.Sha256), i.FetchedAt, i.Provenance, i.Completeness)).ToArray(),
        result.Source, result.FetchedAt, result.Page, result.PageSize, result.Total, result.More, result.Partial,
        result.NeedsRemote, result.PendingImport, result.VerificationStatus, result.Warnings);
    public static MetadataResponse ToMetadata(this CatalogResult result) => new(
        result.Items.Select(i => new MetadataSnapshot(i.VndbId, i.Title, i.Kind, i.Novel, i.Developers, i.Producer,
            new(i.Cover.Status, i.Cover.Url, i.Cover.ContentType, i.Cover.Sha256), i.FetchedAt, i.Provenance, i.Completeness)).ToArray(),
        result.Source, result.FetchedAt, result.Page, result.PageSize, result.Total, result.More, result.Partial,
        result.NeedsRemote, result.PendingImport, result.VerificationStatus, result.Warnings);
}
