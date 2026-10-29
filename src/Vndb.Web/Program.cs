using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.Net.Http.Headers;
using Vndb.Core;
using Vndb.Library;
using Vndb.Contracts;
using Vndb.Web;

if (args.Contains("--healthcheck", StringComparer.Ordinal))
{
    try { using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(4) }; (await healthClient.GetAsync("http://127.0.0.1:8080/healthz")).EnsureSuccessStatusCode(); }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { Environment.ExitCode = 1; }
    return;
}
var builder = WebApplication.CreateBuilder(args);
// Hosting request diagnostics include raw query strings. Keep rejected local paths out of persisted logs.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var dataDirectory = builder.Configuration["Library:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var writeKey = builder.Configuration["Library:WriteKey"];
if (builder.Configuration["Library:WriteKeyFile"] is { Length: > 0 } keyFile) writeKey = File.ReadAllText(keyFile).Trim();
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(writeKey))
    throw new InvalidOperationException("请配置 Library:WriteKeyFile 或 Library:WriteKey，局域网写操作需要独立管理密钥。");
var defaults = new ImportLimits();
var importLimits = new ImportLimits(builder.Configuration.GetValue("MetadataImports:MaximumBytes", defaults.MaximumBytes), builder.Configuration.GetValue("MetadataImports:MaximumItems", defaults.MaximumItems)).Validate();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = importLimits.MaximumRequestBytes);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.Encoder = VndbJson.Options.Encoder;
});
builder.Services.AddHttpClient("vndb", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
builder.Services.AddSingleton(_ => new SqliteLibraryStore(dataDirectory));
builder.Services.AddSingleton<ILibraryStore>(provider => provider.GetRequiredService<SqliteLibraryStore>());
builder.Services.AddSingleton<ICatalogStore>(provider => provider.GetRequiredService<SqliteLibraryStore>());
builder.Services.AddSingleton<IAutomationStore>(provider => provider.GetRequiredService<SqliteLibraryStore>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => new VndbClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("vndb")));
builder.Services.AddSingleton(provider => new CoverDownloader(provider.GetRequiredService<IHttpClientFactory>().CreateClient("vndb"), dataDirectory));
builder.Services.AddSingleton<LibraryImporter>();
builder.Services.AddSingleton<CatalogEnricher>();
builder.Services.AddSingleton<CatalogJobs>();
builder.Services.AddSingleton<CatalogQueries>();
builder.Services.AddSingleton(importLimits);
builder.Services.AddSingleton<MetadataImports>();
builder.Services.AddSingleton<ActivityImports>();
builder.Services.AddSingleton<CatalogMaintenance>();
builder.Services.AddSingleton<QualityReview>();
builder.Services.AddSingleton(provider => new DatasetImports(provider.GetRequiredService<SqliteLibraryStore>(),
    provider.GetRequiredService<IHttpClientFactory>().CreateClient("vndb")));
builder.Services.AddOpenApi(options => options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true);
if (builder.Configuration.GetValue("CatalogJobs:Enabled", true)) builder.Services.AddHostedService<CatalogJobWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new ApiProblem("rate_limited", "资料库查询过快，请稍后重试。", 429,
            context.HttpContext.TraceIdentifier), ct);
    };
    options.AddFixedWindowLimiter("upstream", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});
var app = builder.Build();
app.MapDatasets();
app.Services.GetRequiredService<SqliteLibraryStore>().EnsureWebPrivacy();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    try
    {
        if ((context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE" && context.Request.Path.StartsWithSegments("/api")) || context.Request.Path == "/api/remote/search")
        {
            var supplied = context.Request.Headers["X-Library-Key"].ToString();
            var valid = string.IsNullOrEmpty(writeKey) || CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(writeKey)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
            if (context.Request.Headers["X-Library-Request"] != "1" || !valid)
            {
                if (context.Request.Path.StartsWithSegments("/api/v1"))
                    await Results.Json(new ApiProblem("unauthorized", "请输入正确的资料库管理密钥。", 401, context.TraceIdentifier), statusCode: 401).ExecuteAsync(context);
                else await Results.Problem(statusCode: 401, title: "请输入正确的资料库管理密钥。").ExecuteAsync(context);
                return;
            }
        }
        await next(context);
    }
    catch (Exception exception) when (!context.Response.HasStarted && exception is BadHttpRequestException or ArgumentException or PlayConflictException or VndbApiException or HttpRequestException or TimeoutException or JsonException or SqliteException or IOException or OperationCanceledException)
    {
        var (status, message) = exception switch
        {
            ImportValidationException input => (input.Status, input.Message),
            BadHttpRequestException bad when bad.StatusCode == 413 => (413, "请求信封超过限制；请减少新导入文件的条目，原批次及回执保留。"),
            BadHttpRequestException => (400, "资料库请求 JSON 或参数格式无效。"),
            ArgumentException => (400, exception.Message),
            PlayConflictException => (409, exception.Message),
            VndbApiException api => ((int)api.StatusCode == 429 ? 503 : 502, api.Message),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => (499, "请求已取消。"),
            HttpRequestException or TimeoutException or OperationCanceledException => (502, "VNDB 暂时无法连接，请稍后重试。"),
            JsonException when context.Request.Path.StartsWithSegments("/api/plays") || context.Request.Path.StartsWithSegments("/api/catalog") || context.Request.Path.StartsWithSegments("/api/search") => (400, "资料库请求 JSON 格式无效。"),
            JsonException => (502, "VNDB 数据格式发生变化。"),
            _ => (500, "资料库读写失败，请检查项目日志和数据卷权限。")
        };
        app.Logger.LogWarning("Request failed: {Method}, type={Type}, status={Status}", context.Request.Method, exception.GetType().Name, status);
        if (context.Request.Path.StartsWithSegments("/api/v1"))
        {
            var code = exception switch
            {
                ImportValidationException input => input.Code,
                BadHttpRequestException bad when bad.StatusCode == 413 => "request_too_large",
                BadHttpRequestException or ArgumentException => "invalid_request", PlayConflictException => "conflict",
                VndbApiException api when (int)api.StatusCode == 429 => "upstream_throttled",
                VndbApiException or HttpRequestException or TimeoutException => "upstream_unavailable",
                OperationCanceledException => "cancelled", JsonException => "upstream_protocol",
                _ => "library_unavailable"
            };
            await Results.Json(new ApiProblem(code, message, status, context.TraceIdentifier), statusCode: status).ExecuteAsync(context);
        }
        else await Results.Problem(statusCode: status, title: message).ExecuteAsync(context);
    }
});
app.UseLocalDataBoundary();
app.UseRateLimiter();
app.MapOpenApi();
var identityFile = Path.Combine(dataDirectory, "service-instance-id");
if (!File.Exists(identityFile)) File.WriteAllText(identityFile, Guid.NewGuid().ToString("D"));
app.MapV1(!string.IsNullOrEmpty(writeKey), File.ReadAllText(identityFile).Trim());
app.MapManagement(File.ReadAllText(identityFile).Trim());
app.MapGet("/api/v1/devices", (SqliteLibraryStore store) => store.Devices());
app.MapPost("/api/v1/devices", (DeviceWrite write, SqliteLibraryStore store, HttpContext context) => store.SaveDevice(write, context.Request.Headers["Idempotency-Key"].ToString()));
app.MapPost("/api/v1/devices/{id}/seen", (string id, RevisionRequest revision, SqliteLibraryStore store, HttpContext context) => store.DeviceSeen(id, revision, context.Request.Headers["Idempotency-Key"].ToString()));
app.MapGet("/api/v1/presence", (SqliteLibraryStore store) => store.PublicPresence());
app.MapGet("/api/v1/presence/devices/{id}", (string id, SqliteLibraryStore store) => store.PresenceHead(id));
app.MapPost("/api/v1/presence", (PresenceBatch batch, SqliteLibraryStore store, HttpContext context) => store.SavePresence(batch, File.ReadAllText(identityFile).Trim(), context.Request.Headers["Idempotency-Key"].ToString()));
app.MapActivity(File.ReadAllText(identityFile).Trim());
app.MapQuality();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" && !context.Request.Query.ContainsKey("view") && !context.Request.Query.ContainsKey("vn"))
    { context.Response.Redirect("/manage.html"); return; }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });
app.MapGet("/healthz", (ILibraryStore store) => Results.Ok(new { status = "ok", novels = store.Statistics().Novels }));
app.MapGet("/api/stats", (ILibraryStore store) => store.Statistics());
app.MapGet("/api/config", () => new { write_key_required = !string.IsNullOrEmpty(writeKey), version = typeof(Program).Assembly.GetName().Version!.ToString(3), schema_version = SqliteLibraryStore.SchemaVersion });
app.MapPlays();
app.MapCatalog();
app.MapAutomation();
app.MapGet("/api/producers", (ILibraryStore store) => store.Producers());
app.MapGet("/api/library", (ILibraryStore store, string? q, string? producer, int? page, int? limit) => store.List(q, producer, page ?? 1, limit ?? 24));
app.MapGet("/api/library/{id}", (string id, ILibraryStore store) =>
    store.Get(id) is { } entry ? Results.Ok(entry) : Results.NotFound());
app.MapGet("/api/library/{id}/cover", (string id, ILibraryStore store) =>
{
    var entry = store.Get(id);
    if (entry?.Cover is not { } cover) return Results.NotFound();
    var path = LibraryRules.CoverPath(store.DataDirectory, cover);
    return File.Exists(path) ? Results.File(path, cover.ContentType, enableRangeProcessing: true,
        entityTag: new EntityTagHeaderValue('"' + cover.Hash + '"')) : Results.NotFound();
});
app.MapGet("/api/library/{id}/export", (string id, ILibraryStore store) =>
{
    var entry = store.Get(id);
    return entry is null ? Results.NotFound() : Results.File(JsonSerializer.SerializeToUtf8Bytes(entry, VndbJson.Options), "application/json", entry.Novel.Id + ".json");
});
app.MapGet("/api/remote/search", async (string q, int? page, int? limit, string? developer, CatalogQueries queries, HttpContext context, CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Length > 200) throw new ArgumentException("请输入 1–200 字符的标题或别名。");
    if (limit is < 1 or > 20) throw new ArgumentException("远程搜索每页数量须为 1–20。");
    // Compatibility shape; the old read-only network path now uses the same persistence service.
    context.Response.Headers["Deprecation"] = "true";
    var response = await queries.QueryAsync("vn_search", new(q, developer, page ?? 1, limit ?? 12), null, token);
    return new QueryPage<VisualNovelSummary> { Results = response.Items.Select(i => (VisualNovelSummary)i.Novel!).ToArray(),
        More = response.More, Page = response.Page, PageSize = response.PageSize };
}).RequireRateLimiting("upstream");
app.MapPost("/api/imports/{id}", async (string id, bool? refresh, LibraryImporter importer, CancellationToken token) =>
    await importer.ImportAsync(id, refresh ?? false, token) is { } entry ? Results.Ok(entry) : Results.NotFound())
    .RequireRateLimiting("upstream");
app.Run();

public partial class Program;
