using Vndb.Library;

public static class CatalogEndpoints
{
    public static void MapCatalog(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog");
        group.MapGet("", (CatalogEnricher catalog, string? q, string? status, string? scope, int? page, int? limit) =>
            catalog.Page(q, string.IsNullOrWhiteSpace(status) ? null : status, string.IsNullOrWhiteSpace(scope) ? null : scope, page ?? 1, limit ?? 20));
        group.MapGet("/stats", (CatalogEnricher catalog) => catalog.Statistics());
        group.MapPost("/enrich", async (CatalogBatchRequest request, CatalogEnricher catalog, CancellationToken token) =>
            await catalog.CompleteAsync(request, token)).RequireRateLimiting("upstream");
        group.MapPost("/search", async (CatalogSearch request, CatalogEnricher catalog, CancellationToken token) =>
            await catalog.SearchAsync(request, token)).RequireRateLimiting("upstream");
        group.MapPost("/resolve", async (CatalogResolve request, CatalogEnricher catalog, CancellationToken token) =>
            await catalog.ResolveAsync(request, token)).RequireRateLimiting("upstream");
    }
}
