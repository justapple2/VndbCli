using System.Text.Json;
using Vndb.Contracts;
using Vndb.Library;

namespace Vndb.Web;

public static class ManagementEndpoints
{
    private static string Key(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();
    public static void MapManagement(this WebApplication app, string instanceId)
    {
        var group = app.MapGroup("/api/v1").WithTags("Personal library and metadata imports");
        group.MapGet("/items", ListItems);
        group.MapGet("/shelf", (SqliteLibraryStore store, string? q, bool? deleted, bool? unmatched, int? page, int? page_size, string? progress, string? status, bool? is_local, bool? favorite, string? label) =>
            store.ReadShelfSummary(q, deleted ?? false, unmatched ?? false, page ?? 1, page_size ?? 100, progress, status, is_local, favorite, label));
        group.MapGet("/item-heads", (SqliteLibraryStore store, int? page, int? page_size, bool? deleted) => store.ReadItemHeads(page ?? 1, page_size ?? 100, deleted ?? false));
        group.MapGet("/personal-organization", (SqliteLibraryStore store) => store.ReadPersonalOrganization());
        group.MapPost("/personal-organization", (PersonalOrganizationWrite edit, SqliteLibraryStore store, HttpContext ctx) =>
        {
            if (edit.ServiceInstanceId != instanceId) throw new PlayConflictException("服务实例不一致，分类原稿保留。");
            return store.SavePersonalOrganization(edit.Change, Key(ctx));
        });
        group.MapGet("/collection/stats", (SqliteLibraryStore store) => store.CollectionStatistics());
        group.MapGet("/items/{id}", (string id, SqliteLibraryStore store) => store.ReadItem(id) is { } item ? Results.Ok(item) : Results.NotFound());
        group.MapPost("/items", (ItemWrite edit, SqliteLibraryStore store, HttpContext ctx) => store.SaveItem(edit, null, Key(ctx)));
        group.MapPatch("/items/{id}", (string id, ItemWrite edit, SqliteLibraryStore store, HttpContext ctx) => store.SaveItem(edit, id, Key(ctx)));
        group.MapPost("/items/{id}/link", (string id, ItemLink link, SqliteLibraryStore store, HttpContext ctx) => store.LinkItem(id, link, Key(ctx)));
        group.MapDelete("/items/{id}", (string id, long expected_revision, SqliteLibraryStore store, HttpContext ctx) => store.SetItemRemoved(id, new(expected_revision), true, Key(ctx)));
        group.MapPost("/items/{id}/restore", (string id, RevisionRequest revision, SqliteLibraryStore store, HttpContext ctx) => store.SetItemRemoved(id, revision, false, Key(ctx)));
        group.MapGet("/plays", (SqliteLibraryStore store, string? item_id, bool? deleted) => store.ReadPersonalPlays(item_id, deleted ?? false));
        group.MapGet("/plays/{id}", (string id, SqliteLibraryStore store) => store.ReadPersonalPlay(id) is { } play ? Results.Ok(play) : Results.NotFound());
        group.MapPost("/plays", (PersonalPlayWrite edit, SqliteLibraryStore store, HttpContext ctx) => store.PlayView(store.SavePersonalPlay(edit, null, Key(ctx))));
        group.MapPatch("/plays/{id}", (string id, PersonalPlayWrite edit, SqliteLibraryStore store, HttpContext ctx) => store.PlayView(store.SavePersonalPlay(edit, id, Key(ctx))));
        group.MapDelete("/plays/{id}", (string id, long expected_revision, SqliteLibraryStore store, HttpContext ctx) => store.PlayView(store.SetPlayRemoved(id, new(expected_revision), true, Key(ctx))));
        group.MapPost("/plays/{id}/restore", (string id, RevisionRequest revision, SqliteLibraryStore store, HttpContext ctx) => store.PlayView(store.SetPlayRemoved(id, revision, false, Key(ctx))));
        group.MapGet("/imports", (SqliteLibraryStore store) => store.MetadataBatches());
        group.MapPost("/imports/preview", (ImportPreviewRequest request, MetadataImports imports) => imports.Preview(request));
        group.MapGet("/imports/{id}", (string id, SqliteLibraryStore store) => store.GetMetadataBatch(id) with { ServiceInstanceId = instanceId });
        group.MapPost("/imports/{id}/commit", (string id, SqliteLibraryStore store, HttpContext ctx) => store.CommitMetadata(id, Key(ctx)) with { ServiceInstanceId = instanceId });
        group.MapPost("/imports/{id}/pause", (string id, SqliteLibraryStore store, HttpContext ctx) => store.PauseMetadata(id, true, Key(ctx)));
        group.MapPost("/imports/{id}/resume", (string id, SqliteLibraryStore store, HttpContext ctx) => store.PauseMetadata(id, false, Key(ctx)));
        group.MapPost("/sync/batches", (JsonElement payload, MetadataImports imports, HttpContext ctx) => imports.Sync(payload, Key(ctx)) with { ServiceInstanceId = instanceId });
        group.MapGet("/sync/batches/{id}", (string id, SqliteLibraryStore store) => store.GetMetadataBatch(id) with { ServiceInstanceId = instanceId });
        group.MapGet("/metadata/candidates", (SqliteLibraryStore store) => store.MetadataCandidates());
        group.MapPost("/metadata/candidates/{id}/accept", (string id, CandidateDecision decision, SqliteLibraryStore store, HttpContext ctx) => store.DecideCandidate(id, decision, true, Key(ctx)));
        group.MapPost("/metadata/candidates/{id}/reject", (string id, CandidateDecision decision, SqliteLibraryStore store, HttpContext ctx) => store.DecideCandidate(id, decision, false, Key(ctx)));
        group.MapGet("/metadata/jobs", (SqliteLibraryStore store) => store.MetadataJobs());
        group.MapPost("/metadata/covers/repair", (CoverRepairRequest request, SqliteLibraryStore store, HttpContext ctx) => store.RepairPersonalCovers(request, Key(ctx)));
        group.MapPost("/metadata/jobs/{id}/{action}", (string id, string action, SqliteLibraryStore store, HttpContext ctx) => store.ControlMetadataJob(id, action, Key(ctx)));
        group.MapGet("/metadata/{id}/provenance", (string id, SqliteLibraryStore store) => store.Provenance(id) is { } source ? Results.Ok(source) : Results.NotFound());
        group.MapGet("/schemas/metadata-batch-v1.json", (IWebHostEnvironment environment) => Results.File(Path.Combine(environment.WebRootPath, "schemas", "metadata-batch-v1.json"), "application/schema+json"));
    }
    private static ItemPage ListItems(SqliteLibraryStore store, string? q, bool? deleted, bool? unmatched, int? page, int? page_size, string? progress, string? status, bool? is_local, bool? favorite, string? label)
        => store.ReadItems(q, deleted ?? false, unmatched ?? false, page ?? 1, page_size ?? 24, progress, status, is_local, favorite, label);
}
