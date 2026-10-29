using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Vndb.Core;

/// <summary>A reusable read-only Kana client. The caller owns the HttpClient.</summary>
public sealed partial class VndbClient : IDisposable
{
    public static Uri ApiEndpoint { get; } = new("https://api.vndb.org/kana/");
    private const string NovelFields = "title,alttitle,olang,released,developers{id,name,original}";
    private const string NovelDetailFields = NovelFields +
        ",titles{lang,title,latin,official,main},aliases,languages,platforms,description,rating,votecount,length_minutes,image{id,url,thumbnail,dims,sexual,violence}";
    private const string ProducerFields = "name,original,aliases,lang,type";
    private readonly HttpClient _httpClient;
    private readonly VndbClientOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRequest;

    public VndbClient(HttpClient httpClient, VndbClientOptions? options = null, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _options = options ?? new();
        _time = timeProvider ?? TimeProvider.System;
        if (_options.RequestInterval < TimeSpan.Zero || _options.RequestTimeout <= TimeSpan.Zero ||
            _options.MaxRetries is < 0 or > 5 || _options.ThrottleBackoff < TimeSpan.Zero ||
            _options.TransientBackoff < TimeSpan.Zero)
        {
            throw new ArgumentException("无效的客户端超时、重试或节流设置。", nameof(options));
        }
    }

    public Task<QueryPage<VisualNovelSummary>> SearchNovelsAsync(string query, int limit = 10, int page = 1,
        string? developer = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        object[] filter = ["search", "=", query.Trim()];
        if (developer is not null)
        {
            filter = ["and", filter, DeveloperFilter(developer)];
        }
        return QueryAsync<VisualNovelSummary>("vn", filter, NovelFields, "searchrank", limit, page, cancellationToken);
    }

    public async Task<VisualNovel?> GetNovelAsync(string id, CancellationToken cancellationToken = default)
    {
        var normalized = VndbId.Normalize(id, 'v');
        var result = await QueryAsync<VisualNovel>("vn", ["id", "=", normalized],
            NovelDetailFields, "id", 1, 1, cancellationToken);
        ValidateRequested(result.Results.Select(n => n.Id), [normalized]);
        return result.Results.FirstOrDefault();
    }

    public Task<QueryPage<VisualNovel>> SearchNovelDetailsAsync(IReadOnlyList<string> queries, int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (queries.Count is < 1 or > 10 || queries.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 200))
            throw new ArgumentException("完整资料查询需要 1–10 个、每个 1–200 字符的标题。");
        object[] filters = queries.Count == 1 ? ["search", "=", queries[0].Trim()] :
            new object[] { "or" }.Concat(queries.Select(q => (object)new object[] { "search", "=", q.Trim() })).ToArray();
        return QueryAsync<VisualNovel>("vn", filters, NovelDetailFields, "id", 100, page, cancellationToken);
    }

    public async Task<QueryPage<VisualNovel>> GetNovelsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count is < 1 or > 100) throw new ArgumentException("作品批量查询需要 1–100 个 ID。");
        var normalized = ids.Select(id => VndbId.Normalize(id, 'v')).Distinct().ToArray();
        object[] filters = normalized.Length == 1 ? ["id", "=", normalized[0]] :
            new object[] { "or" }.Concat(normalized.Select(id => (object)new object[] { "id", "=", id })).ToArray();
        var result = await QueryAsync<VisualNovel>("vn", filters, NovelDetailFields, "id", 100, 1, cancellationToken);
        ValidateRequested(result.Results.Select(n => n.Id), normalized);
        return result;
    }

    public async Task<QueryPage<Producer>> GetProducersAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count is < 1 or > 100) throw new ArgumentException("会社批量查询需要 1–100 个 ID。");
        var normalized = ids.Select(id => VndbId.Normalize(id, 'p')).Distinct().ToArray();
        object[] filters = normalized.Length == 1 ? ["id", "=", normalized[0]] :
            new object[] { "or" }.Concat(normalized.Select(id => (object)new object[] { "id", "=", id })).ToArray();
        var result = await QueryAsync<Producer>("producer", filters, ProducerFields + ",description", "id", 100, 1, cancellationToken);
        ValidateRequested(result.Results.Select(p => p.Id), normalized);
        return result;
    }

    public Task<QueryPage<Producer>> SearchProducersAsync(string query, int limit = 10, int page = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return QueryAsync<Producer>("producer", ["search", "=", query.Trim()], ProducerFields,
            "searchrank", limit, page, cancellationToken);
    }

    public async Task<Producer?> GetProducerAsync(string id, CancellationToken cancellationToken = default)
    {
        var normalized = VndbId.Normalize(id, 'p');
        var result = await QueryAsync<Producer>("producer", ["id", "=", normalized],
            ProducerFields + ",description", "id", 1, 1, cancellationToken);
        ValidateRequested(result.Results.Select(p => p.Id), [normalized]);
        return result.Results.FirstOrDefault();
    }

    public Task<QueryPage<VisualNovelSummary>> GetDeveloperNovelsAsync(string developer, int limit = 10, int page = 1,
        CancellationToken cancellationToken = default) =>
        QueryAsync<VisualNovelSummary>("vn", DeveloperFilter(developer), NovelFields, "id", limit, page, cancellationToken);

    public Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<DatabaseStats>(HttpMethod.Get, "stats", null, null, cancellationToken);

    public Task<AuthInfo> GetAuthInfoAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
        {
            throw new ArgumentException("VNDB_API_TOKEN 不得包含空白或控制字符。");
        }
        return SendAsync<AuthInfo>(HttpMethod.Get, "authinfo", null, token, cancellationToken);
    }

    private static object[] DeveloperFilter(string id) => ["developer", "=", new object[] { "id", "=", VndbId.Normalize(id, 'p') }];

    private async Task<QueryPage<T>> QueryAsync<T>(string endpoint, object[] filters, string fields,
        string sort, int limit, int page, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("--limit 必须在 1 到 100 之间。");
        if (page < 1) throw new ArgumentException("--page 必须大于或等于 1。");
        var body = new { filters, fields, sort, results = limit, page };
        var result = await SendAsync<QueryPage<T>>(HttpMethod.Post, endpoint, body, null, cancellationToken);
        if (result.Results is null) throw new JsonException("VNDB 结果集合无效。");
        foreach (var item in result.Results)
        {
            switch (item)
            {
                case VisualNovelSummary n when ValidId(n.Id, 'v') && !string.IsNullOrWhiteSpace(n.Title) && n.Developers is not null &&
                    n.Developers.All(p => p is not null && ValidId(p.Id, 'p') && !string.IsNullOrWhiteSpace(p.Name)): break;
                case Producer p when ValidId(p.Id, 'p') && !string.IsNullOrWhiteSpace(p.Name): break;
                case NovelRelease r when ValidId(r.Id, 'r') && !string.IsNullOrWhiteSpace(r.Title): break;
                default: throw new JsonException("VNDB 条目字段无效。");
            }
        }
        return result with { Page = page, PageSize = limit };
    }

    private static bool ValidId(string? value, char prefix) => value is { Length: > 1 } && value[0] == prefix && value[1..].All(char.IsAsciiDigit) && value[1] != '0';
    private static void ValidateRequested(IEnumerable<string> returned, IReadOnlyList<string> requested)
    {
        if (returned.Any(id => !requested.Contains(id, StringComparer.Ordinal))) throw new JsonException("VNDB 返回了查询范围以外的 ID。");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string endpoint, object? body, string? token,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await DelayAsync(_nextRequest - _time.GetUtcNow(), cancellationToken);
                _nextRequest = _time.GetUtcNow() + _options.RequestInterval;
                using var request = new HttpRequestMessage(method, new Uri(ApiEndpoint, endpoint));
                request.Headers.UserAgent.ParseAdd("VndbLocal/0.10.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
                if (body is not null) request.Content = JsonContent.Create(body, options: VndbJson.Options);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                try
                {
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    if (IsRetryable(response.StatusCode) && attempt < _options.MaxRetries)
                    {
                        // Retry-After is a lower bound, including when the server requests a long wait.
                        var fallback = response.StatusCode == HttpStatusCode.TooManyRequests
                            ? _options.ThrottleBackoff : _options.TransientBackoff * (1 << attempt);
                        var retryAfter = response.Headers.RetryAfter;
                        var requested = retryAfter?.Delta ?? (retryAfter?.Date - _time.GetUtcNow()) ?? TimeSpan.Zero;
                        var nextRetry = _time.GetUtcNow() + (requested > fallback ? requested : fallback);
                        if (nextRetry > _nextRequest) _nextRequest = nextRetry;
                        response.Dispose();
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // Error bodies may be HTML and might echo input. Do not print them or any headers.
                        throw new VndbApiException(response.StatusCode, ErrorMessage(response.StatusCode));
                    }
                    return await response.Content.ReadFromJsonAsync<T>(VndbJson.Options, timeout.Token)
                        ?? throw new JsonException("VNDB 返回了空 JSON 响应。");
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("VNDB 请求超时；可以使用 --timeout 增加单次请求的等待秒数。", exception);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, _time, cancellationToken) : Task.CompletedTask;

    private static bool IsRetryable(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static string ErrorMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => "VNDB 拒绝了查询 (HTTP 400)，请检查参数或减少 --limit。",
        HttpStatusCode.Unauthorized => "VNDB 令牌无效 (HTTP 401)，请检查 VNDB_API_TOKEN。",
        HttpStatusCode.TooManyRequests => "VNDB 请求达到限额 (HTTP 429)，重试次数已用尽，请稍后再试。",
        _ => $"VNDB API 请求失败 (HTTP {(int)status})。"
    };

    public void Dispose() => _gate.Dispose();
}
