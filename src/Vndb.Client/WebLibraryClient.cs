using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Client;

/// <summary>Caller owns the HTTP client and must disable redirects. A key is attached only to same-origin writes.</summary>
public sealed partial class WebLibraryClient(HttpClient http, Uri server, string? key, TimeSpan timeout) : ICatalogSource, ISyncApi
{
    async Task<CatalogResult> ICatalogSource.QueryAsync(CatalogQuery query, CancellationToken ct) =>
        (await QueryAsync(new(query.Kind, query.ToMetadata(), query.Id), ct)).ToCatalog();
    private bool _discovered;
    private const long MaximumResponseBytes = 8 * 1024 * 1024; // Independent of import and local-state limits.
    public async Task<MetadataResponse> QueryAsync(ClientQuery query, CancellationToken ct)
    {
        if (!_discovered)
        {
            var info = await SendAsync<ServiceInfo>(HttpMethod.Get, "api/v1/info", null, ct);
            if (info.ApiVersions is null || !info.ApiVersions.Contains(1)) throw new ClientApiException("protocol_incompatible", 200);
            _discovered = true;
        }
        var q = query.Request;
        var path = query.Kind switch
        {
            "vn_get" => "vns/" + query.Id,
            "vn_releases" => "vns/" + query.Id + "/releases",
            "producer_get" => "producers/" + query.Id,
            "producer_search" => $"producers?q={Uri.EscapeDataString(q.Query)}&page={q.Page}&page_size={q.PageSize}",
            _ => $"vns?q={Uri.EscapeDataString(q.Query)}&producer_id={Uri.EscapeDataString(query.Kind == "producer_vns" ? query.Id! : q.ProducerId ?? "")}&page={q.Page}&page_size={q.PageSize}"
        };
        if (q.Mode != "explicit_refresh")
        {
            MetadataResponse? local = null;
            try { local = await SendAsync<MetadataResponse>(HttpMethod.Get, "api/v1/" + path, null, ct); }
            catch (ClientApiException e) when (e.Status == 404 && e.Code == "not_found") { }
            if (local is not null && (!local.NeedsRemote || q.Mode == "local_only")) return Validate(local);
            if (q.Mode == "local_only") return QueryRules.Response([], "library", q, 0, false, needsRemote: true);
        }
        path = query.Kind switch
        {
            "vn_get" => $"vns/{query.Id}/{(q.Mode == "explicit_refresh" ? "refresh" : "resolve")}",
            "producer_get" => $"producers/{query.Id}/{(q.Mode == "explicit_refresh" ? "refresh" : "resolve")}",
            "vn_releases" => $"vns/{query.Id}/releases/{(q.Mode == "explicit_refresh" ? "refresh" : "resolve")}",
            "producer_vns" => $"producers/{query.Id}/vns/query",
            "producer_search" => "queries/producers", _ => "queries/vns"
        };
        return Validate(await SendAsync<MetadataResponse>(HttpMethod.Post, "api/v1/" + path,
            query.Kind is "vn_get" or "vn_releases" or "producer_get" ? null : q, ct));
    }

    private static MetadataResponse Validate(MetadataResponse result)
    {
        if (result.Items is null || result.Page < 1 || result.PageSize is < 1 or > 100 || result.Items.Count > result.PageSize ||
            result.Source is not ("library" or "query_cache" or "vndb") ||
            result.Items.Any(i => i is null || i.VndbId is null || i.Title is null || i.Developers is null || i.Cover is null ||
                i.Kind is not ("vn" or "producer") || i.Kind == "vn" && i.Novel is null || i.Kind == "producer" && i.Producer is null))
            throw new ClientApiException("protocol_invalid", 200);
        try
        {
            foreach (var item in result.Items)
                if (item.Novel?.ReleaseCatalog is { } catalog) ReleaseRules.Validate(catalog, item.VndbId);
        }
        catch (ArgumentException) { throw new ClientApiException("protocol_invalid", 200); }
        return result;
    }

    public Task<LocalStatistics> StatisticsAsync(CancellationToken ct) => SendAsync<LocalStatistics>(HttpMethod.Get, "api/v1/stats", null, ct);
    public async Task<ImportBatchReceipt> SyncBatchAsync(JsonElement payload, string operationKey, CancellationToken ct)
    {
        var info = await SendAsync<ServiceInfo>(HttpMethod.Get, "api/v1/info", null, ct);
        if (!info.ApiVersions.Contains(1) || !info.MetadataImportAvailable) throw new ClientApiException("metadata_import_unavailable", 200);
        try { ImportValidation.Metadata(payload, DiscoveredLimits(info)); }
        catch (ImportValidationException e) { throw new ClientApiException(e.Code, e.Status, e.Message); }
        return await SendAsync<ImportBatchReceipt>(HttpMethod.Post, "api/v1/sync/batches", payload, ct, operationKey);
    }
    private static ImportLimits DiscoveredLimits(ServiceInfo info)
    {
        try { return (info.ImportLimits ?? new()).Validate(); }
        catch (ArgumentException) { throw new ClientApiException("protocol_invalid", 200); }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, string? operationKey = null)
    {
        if (body is not null && WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(body, VndbJson.Options)))
            throw new ArgumentException("本机目录／程序数据禁止发送Web，请使用本机JSON功能。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(server, path));
            if (method != HttpMethod.Get)
            {
                request.Headers.Add("X-Library-Request", "1");
                if (operationKey is not null) request.Headers.Add("Idempotency-Key", operationKey);
                if (key is not null && key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) throw new ArgumentException("VN_LIBRARY_KEY 不得含空白或控制字符。");
                if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("X-Library-Key", key);
                if (body is not null) request.Content = JsonContent.Create(body, options: VndbJson.Options);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            // Never follow Location, even when it is another library endpoint.
            if ((int)response.StatusCode is >= 300 and < 400) throw new ClientApiException("redirect_refused", (int)response.StatusCode);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new ClientApiException("response_too_large", (int)response.StatusCode);
            try { await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, linked.Token); }
            catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
            { throw new ClientApiException("response_too_large", (int)response.StatusCode); }
            if (!response.IsSuccessStatusCode)
            {
                ApiProblem? problem = null;
                try { problem = await response.Content.ReadFromJsonAsync<ApiProblem>(VndbJson.Options, linked.Token); }
                catch (JsonException) { }
                var code = problem?.Code is "not_found" or "unauthorized" or "conflict" or "invalid_request" or
                    "upstream_throttled" or "upstream_unavailable" or "upstream_protocol" or "cancelled" or "library_unavailable" or "rate_limited" or
                    "import_too_large" or "request_too_large" or "import_item_limit" or "invalid_json" or "import_kind_mismatch" ? problem.Code : "http_error";
                // An upstream failure is deliberately NOT a transport failure of the library.
                if (code == "library_unavailable" || (code == "http_error" && response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                    throw new WebUnavailableException();
                throw new ClientApiException(code, (int)response.StatusCode);
            }
            return await response.Content.ReadFromJsonAsync<T>(VndbJson.Options, linked.Token)
                ?? throw new ClientApiException("protocol_invalid", (int)response.StatusCode);
        }
        catch (HttpRequestException) { throw new WebUnavailableException(); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new WebUnavailableException(); }
        catch (JsonException) { throw new ClientApiException("protocol_invalid", 200); }
    }
}
