using System.Text.Json;
using Vndb.Core;
using Vndb.Library;

public static class AutomationEndpoints
{
    public static void MapAutomation(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/catalog/jobs");
        jobs.MapGet("/latest", (IAutomationStore store) => JobResponse(store.GetCatalogJob()));
        jobs.MapPost("", (CatalogJobRequest request, CatalogJobs service) => JobResponse(service.Queue(request)));
        jobs.MapPost("/{id}/pause", (string id, IAutomationStore store, TimeProvider time) => Results.Ok(AutomationRules.Summary(store.PauseCatalogJob(id, true, time.GetUtcNow()))));
        jobs.MapPost("/{id}/resume", (string id, IAutomationStore store, TimeProvider time) => Results.Ok(AutomationRules.Summary(store.PauseCatalogJob(id, false, time.GetUtcNow()))));
        jobs.MapGet("/{id}/export", (string id, IAutomationStore store) => store.GetCatalogJob(id) is { } job ?
            Results.File(JsonSerializer.SerializeToUtf8Bytes(job, VndbJson.Options), "application/json", id + ".json") : Results.NotFound());
        app.MapGet("/api/search", (CatalogQueries search, string? q, string? producer, int? page, int? limit) =>
            search.Local(new(q ?? "", producer, page ?? 1, limit ?? 12)));
        app.MapPost("/api/search", async (LibrarySearchRequest request, CatalogQueries search, CancellationToken token) =>
            await search.SearchAsync(request, token)).RequireRateLimiting("upstream");
    }
    public static IResult JobResponse(CatalogJob? job) => Results.Content(JsonSerializer.Serialize(AutomationRules.Summary(job), VndbJson.Options), "application/json");
}
