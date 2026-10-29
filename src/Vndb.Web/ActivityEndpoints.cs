using Vndb.Contracts;
using Vndb.Library;

namespace Vndb.Web;

public static class ActivityEndpoints
{
    public static void MapActivity(this WebApplication app, string instanceId)
    {
        var group = app.MapGroup("/api/v1").WithTags("Play sessions and offline personal activity");
        group.MapPost("/activity/batches", (WebActivityBatch incoming, ActivityImports imports) =>
        {
            var batch = incoming.ToBatch();
            if (batch.Installations.Count > 0 || batch.PlaySessions.Any(s => s.InstallationId is not null || s.ComponentId is not null)) throw new ArgumentException("安装关联仅保存在本机；请升级客户端后提交不含安装结构的活动JSON。");
            return imports.Submit(batch, instanceId);
        });
        group.MapGet("/activity/batches/{id}", (string id, SqliteLibraryStore store) => store.ActivityReceipt(id) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/play-sessions", (string? play_id, SqliteLibraryStore store) => store.Sessions(play_id));
        group.MapPost("/play-sessions", (SessionSample sample, SqliteLibraryStore store) =>
        {
            if (sample.InstallationId is not null || sample.ComponentId is not null) throw new ArgumentException("安装关联仅保存在本机。");
            return store.UpsertSession(sample);
        });
        group.MapPost("/play-sessions/{id}/decision", (string id, SessionDecision decision, SqliteLibraryStore store, HttpContext ctx) =>
            store.DecideSession(id, decision, ctx.Request.Headers["Idempotency-Key"].ToString()));
        group.MapGet("/plays/{id}/time", (string id, SqliteLibraryStore store) => store.PlayTime(id));
        group.MapPost("/time-adjustments", (TimeAdjustment adjustment, SqliteLibraryStore store, HttpContext ctx) =>
            store.AdjustTime(adjustment, ctx.Request.Headers["Idempotency-Key"].ToString()));
        group.MapGet("/play-edit-conflicts", (SqliteLibraryStore store) => store.PlayEditConflicts());
        group.MapPost("/play-edit-conflicts/{id}/decision", (string id, PlayEditDecision decision, SqliteLibraryStore store, HttpContext ctx) =>
            store.ResolvePlayEdit(id, decision, ctx.Request.Headers["Idempotency-Key"].ToString()));
    }
}
