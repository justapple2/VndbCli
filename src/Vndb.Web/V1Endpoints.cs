using Microsoft.Net.Http.Headers;
using Vndb.Contracts;
using Vndb.Library;

namespace Vndb.Web;

public static class V1Endpoints
{
    public static void MapV1(this WebApplication app, bool keyRequired, string instanceId)
    {
        var group = app.MapGroup("/api/v1").WithTags("Library v1");
        group.MapGet("/info", (ImportLimits limits) => new ServiceInfo(instanceId, [1], typeof(Program).Assembly.GetName().Version!.ToString(3),
            SqliteLibraryStore.SchemaVersion, keyRequired, ["dataset_maintenance", "dataset_sync", "cover_repair", "personal_organization", "shelf_summary_v1", "item_heads_v1", "public_catalog", "dataset_import", "collection_state", "release_catalog", "metadata_query", "producer_query", "covers", "local_first", "metadata_import", "personal_items", "recoverable_removal", "devices", "play_sessions", "play_edits", "activity_import", "portable_json_hash", "data_quality", "boolean_presence", "local_locations_only"], [1], true, limits))
            .WithSummary("Discover protocol, capabilities and service identity");
        group.MapGet("/stats", (ILibraryStore store) => { var s = store.Statistics(); return new LocalStatistics(s.Novels, store.FindProducers("").Count, s.Covers, s.CoverFailures); });
        group.MapGet("/catalog", (SqliteLibraryStore store, string? q, string? producer_id, string? language,
            string? release_type, string? platform, bool? include_mtl, string? sort, int? page, int? page_size) =>
            store.BrowseCatalog(new(q ?? "", producer_id, language ?? "", release_type ?? "", platform ?? "", include_mtl ?? false, sort ?? "personal", page ?? 1, page_size ?? 24)))
            .WithSummary("Browse saved public metadata only; global release filters and sorting precede paging");
        group.MapGet("/catalog/producers", (SqliteLibraryStore store, string? q) => store.CatalogProducers(q ?? ""));
        group.MapGet("/vns", (CatalogQueries queries, string? q, string? producer_id, int? page, int? page_size) =>
            queries.Local("vn_search", new(q ?? "", producer_id, page ?? 1, page_size ?? 10)));
        group.MapGet("/vns/{id}", (CatalogQueries queries, string id) => LocalGet(queries, "vn_get", id))
            .Produces<MetadataResponse>().Produces<ApiProblem>(404);
        group.MapPost("/queries/vns", (CatalogQueries queries, MetadataQuery request, CancellationToken ct) =>
            queries.QueryAsync("vn_search", request, null, ct)).RequireRateLimiting("upstream");
        group.MapPost("/vns/{id}/resolve", (CatalogQueries queries, string id, CancellationToken ct) =>
            queries.QueryAsync("vn_get", new(), id, ct)).RequireRateLimiting("upstream");
        group.MapPost("/vns/{id}/refresh", (CatalogQueries queries, string id, CancellationToken ct) =>
            queries.QueryAsync("vn_get", new(Mode: "explicit_refresh"), id, ct)).RequireRateLimiting("upstream");
        group.MapGet("/vns/{id}/releases", (CatalogQueries queries, string id) => LocalGet(queries, "vn_releases", id));
        group.MapPost("/vns/{id}/releases/resolve", (CatalogQueries queries, string id, CancellationToken ct) =>
            queries.QueryAsync("vn_releases", new(), id, ct)).RequireRateLimiting("upstream");
        group.MapPost("/vns/{id}/releases/refresh", (CatalogQueries queries, string id, CancellationToken ct) =>
            queries.QueryAsync("vn_releases", new(Mode: "explicit_refresh"), id, ct)).RequireRateLimiting("upstream");
        group.MapGet("/vns/{id}/cover", (ILibraryStore store, string id) =>
        {
            var cover = store.Get(id)?.Cover;
            if (cover is null) return Results.NotFound();
            var path = LibraryRules.CoverPath(store.DataDirectory, cover);
            return File.Exists(path) ? Results.File(path, cover.ContentType, enableRangeProcessing: true,
                entityTag: new EntityTagHeaderValue('"' + cover.Hash + '"')) : Results.NotFound();
        });
        group.MapGet("/producers", (CatalogQueries queries, string? q, int? page, int? page_size) =>
            queries.Local("producer_search", new(q ?? "", Page: page ?? 1, PageSize: page_size ?? 10)));
        group.MapGet("/producers/{id}", (CatalogQueries queries, string id) => LocalGet(queries, "producer_get", id))
            .Produces<MetadataResponse>().Produces<ApiProblem>(404);
        group.MapPost("/queries/producers", (CatalogQueries queries, MetadataQuery request, CancellationToken ct) =>
            queries.QueryAsync("producer_search", request, null, ct)).RequireRateLimiting("upstream");
        group.MapPost("/producers/{id}/resolve", (CatalogQueries queries, string id, CancellationToken ct) =>
            queries.QueryAsync("producer_get", new(), id, ct)).RequireRateLimiting("upstream");
        group.MapPost("/producers/{id}/refresh", (CatalogQueries queries, string id, CancellationToken ct) =>
            queries.QueryAsync("producer_get", new(Mode: "explicit_refresh"), id, ct)).RequireRateLimiting("upstream");
        group.MapPost("/producers/{id}/vns/query", (CatalogQueries queries, string id, MetadataQuery request, CancellationToken ct) =>
            queries.QueryAsync("producer_vns", request, id, ct)).RequireRateLimiting("upstream");
    }
    private static IResult LocalGet(CatalogQueries queries, string kind, string id)
    {
        var result = queries.Local(kind, new(), id);
        return result.NeedsRemote ? Results.Json(new ApiProblem("not_found", "本地库没有此条目。", 404), statusCode: 404) : Results.Ok(result);
    }
}
