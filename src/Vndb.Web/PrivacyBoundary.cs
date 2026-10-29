using System.Text.Json;
using Vndb.Contracts;

namespace Vndb.Web;

public static class PrivacyBoundary
{
    public static void UseLocalDataBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            if (new[] { "/api/v1/installations", "/api/v1/installation-bundles", "/api/v1/legacy-locations", "/api/v1/archive-runs", "/api/v1/quality/installation-audits", "/api/archives", "/api/catalog/local" }
                .Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                await Results.Json(new ApiProblem("local_only", "目录、启动配置和复原证据仅由本机JSON管理。", 410), statusCode: 410).ExecuteAsync(context); return;
            }
            if (path is "/installations.html" or "/organization.html") { context.Response.Redirect("/quality.html"); return; }
            if (context.Request.Query.Any(p => p.Value.Any(v => WirePrivacy.ContainsLocalPath(v ?? ""))))
            { await Results.BadRequest(new ApiProblem("local_only_data", "查询不能包含本机路径。", 400)).ExecuteAsync(context); return; }
            if (context.Request.Method is "POST" or "PUT" or "PATCH" && context.Request.HasJsonContentType())
            {
                var limits = context.RequestServices.GetRequiredService<ImportLimits>();
                if (context.Request.ContentLength > limits.MaximumRequestBytes) throw new BadHttpRequestException("请求信封超过限制。", 413);
                using var buffer = new MemoryStream(); var bytes = new byte[16384]; int count;
                while ((count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) != 0)
                { if (buffer.Length + count > limits.MaximumRequestBytes) throw new BadHttpRequestException("请求信封超过限制。", 413); await buffer.WriteAsync(bytes.AsMemory(0, count), context.RequestAborted); }
                if (buffer.Length == 0) throw new ImportValidationException("invalid_json", 400, "JSON内容为空，请选择有效导出文件。");
                if (buffer.Length > 0)
                {
                    buffer.Position = 0;
                    JsonDocument parsed;
                    try { parsed = await JsonDocument.ParseAsync(buffer, cancellationToken: context.RequestAborted); }
                    catch (JsonException) { throw new ImportValidationException("invalid_json", 400, "JSON格式无效；请修复或重新导出文件，原件保留。"); }
                    using var json = parsed;
                    if (WirePrivacy.ContainsPrivateData(json.RootElement))
                    { await Results.Json(new ApiProblem("local_only_data", "请求含本机目录或程序信息，禁止记录到Web。", 400), statusCode: 400).ExecuteAsync(context); return; }
                    if (path is "/api/v1/sync/batches" or "/api/v1/activity/batches") limits.RequireBytes(buffer.Length);
                    if (path == "/api/v1/activity/batches" && json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == "vn-library-batch")
                        throw new ImportValidationException("import_kind_mismatch", 400, "这是metadata JSON，请使用Web的“JSON导入”入口；活动JSON才使用“会话”页。");
                    buffer.Position = 0; context.Request.Body = buffer; await next(context); return;
                }
            }
            await next(context);
        });
    }
}
