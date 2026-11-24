using Microsoft.AspNetCore.Http.Features;
using Vndb.Contracts;
using Vndb.Library;

namespace Vndb.Web;

public static class DatasetEndpoints
{
    public static void MapDatasets(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/datasets").WithTags("Official public datasets");
        group.MapGet("", (SqliteLibraryStore store) => store.DatasetJobs());
        group.MapGet("/sync", (DatasetImports imports) => imports.SyncStatus());
        group.MapGet("/maintenance", (DatasetImports imports) => imports.MaintenanceStatus());
        group.MapPost("/maintenance/preview", (DatasetImports imports, HttpContext ctx, CancellationToken ct) =>
            imports.PreviewCleanupAsync(ctx.Request.Headers["Idempotency-Key"].ToString(), ct));
        group.MapPost("/maintenance/cleanup", (DatasetCleanupRequest request, DatasetImports imports, HttpContext ctx, CancellationToken ct) =>
            imports.CleanupAsync(request, ctx.Request.Headers["Idempotency-Key"].ToString(), ct));
        group.MapPost("/sync/check", (DatasetImports imports, HttpContext ctx, CancellationToken ct) =>
            imports.CheckLatestAsync(ctx.Request.Headers["Idempotency-Key"].ToString(), ct)).RequireRateLimiting("upstream");
        group.MapPost("/sync/update", (DatasetImports imports, HttpContext ctx, CancellationToken ct) =>
            imports.UpdateLatestAsync(ctx.Request.Headers["Idempotency-Key"].ToString(), ct)).RequireRateLimiting("upstream");
        group.MapPost("/sync/settings", (DatasetSyncWrite request, DatasetImports imports, HttpContext ctx) =>
            imports.ConfigureSync(request, ctx.Request.Headers["Idempotency-Key"].ToString()));
        group.MapPost("", (DatasetRequest request, DatasetImports imports, HttpContext ctx) => imports.Create(request, ctx.Request.Headers["Idempotency-Key"].ToString()));
        group.MapPost("/upload", async (DatasetImports imports, HttpContext ctx, CancellationToken ct) =>
        {
            if (ctx.Request.ContentType != "application/octet-stream") throw new ArgumentException("请选择.tar.zst，使用application/octet-stream上传。");
            var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = DumpPackage.MaximumCompressedBytes;
            if (ctx.Request.ContentLength > DumpPackage.MaximumCompressedBytes) throw new ImportValidationException("dataset_too_large", 413, "导出包超过512MiB。");
            return await imports.UploadAsync(ctx.Request.Body, ctx.Request.Headers["Idempotency-Key"].ToString(), ct);
        });
        group.MapPost("/{id}/{action}", (string id, string action, DatasetRevision request, DatasetImports imports, HttpContext ctx) =>
            imports.Control(id, action, request, ctx.Request.Headers["Idempotency-Key"].ToString()));
    }
}
