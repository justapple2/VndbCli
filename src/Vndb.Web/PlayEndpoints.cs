using Microsoft.AspNetCore.Http.HttpResults;
using Vndb.Library;

public static class PlayEndpoints
{
    public static void MapPlays(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/plays");
        group.MapGet("", List);
        group.MapGet("/stats", (ILibraryStore store) => TypedResults.Ok(store.PlayStatistics()));
        group.MapGet("/{id}", Results<Ok<PlayRecord>, NotFound> (string id, ILibraryStore store) =>
            store.GetPlay(id) is { } record ? TypedResults.Ok(record) : TypedResults.NotFound());
        group.MapPost("", (PlayEdit edit, ILibraryStore store) => TypedResults.Ok(store.SavePlay(edit.Details)));
        group.MapPut("/{id}", (string id, PlayEdit edit, ILibraryStore store) =>
            TypedResults.Ok(store.SavePlay(edit.Details, id, edit.ExpectedRevision)));
        group.MapPost("/imports", (PlayImportBatch batch, bool? background, ILibraryStore store, CatalogJobs jobs) =>
        {
            var imported = store.ImportPlays(batch);
            return background == true ? Results.Ok(new { import = imported, job = AutomationRules.Summary(jobs.Queue(new())) }) : Results.Ok((object)imported);
        });
        group.MapPost("/{id}/queue", (string id, ILibraryStore store, CatalogJobs jobs) =>
        {
            var record = store.GetPlay(id) ?? throw new ArgumentException("游玩记录不存在。");
            if (!record.HasPlayed) throw new ArgumentException("未玩的记录不加入已玩资料补全。");
            return AutomationEndpoints.JobResponse(jobs.Queue(new(Keys: [CatalogRules.Key(record.Details.Title)])));
        });
        group.MapPost("/{id}/enrich", async (string id, ILibraryStore store, CatalogEnricher catalog, CancellationToken token) =>
        {
            var record = store.GetPlay(id) ?? throw new ArgumentException("游玩记录不存在。");
            await catalog.CompleteAsync(new(Key: CatalogRules.Key(record.Details.Title)), token);
            return TypedResults.Ok(store.GetPlay(id));
        }).RequireRateLimiting("upstream");
    }
    private static IResult List(ILibraryStore store, string? q, string? status, bool? played, int? year,
        bool? unmatched, string? novelId, int? page, int? limit)
    {
        var result = store.Plays(q,
            string.IsNullOrWhiteSpace(status) ? null : status, played, year, unmatched ?? false,
            string.IsNullOrWhiteSpace(novelId) ? null : novelId, page ?? 1, limit ?? 24);
        var times = (store as SqliteLibraryStore)?.PlayTimes(result.Results);
        return Results.Ok(new { result.Total, result.Page, result.PageSize, result.More, Results = result.Results.Select(p => new { p.Id, p.Details, p.Source, p.Revision, p.CreatedAt, p.UpdatedAt, p.DeletedAt, p.HasPlayed, Time = times?.GetValueOrDefault(p.Id) }) });
    }
}
