namespace Vndb.Core;

/// <summary>Client policy; no dependency on the Web SDK or a particular local storage.</summary>
public sealed class ClientCatalogQuery(ICatalogSource upstream, ICatalogSnapshots snapshots,
    ICatalogSource? remote = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _unavailableUntil;
    /// <summary>Read existing Web/cache information only; never invokes the upstream source.</summary>
    public async Task<CatalogResult> ReadLocalAsync(CatalogQuery input, CancellationToken ct = default)
    {
        var query = input.Validate() with { Mode = "local_first" }; ct.ThrowIfCancellationRequested();
        CatalogResult? missing = null;
        if (remote is not null && _time.GetUtcNow() >= _unavailableUntil)
        {
            try
            {
                var result = await remote.QueryAsync(query with { Mode = "local_only" }, ct);
                ct.ThrowIfCancellationRequested();
                if (result.Items.Count > 0) { await snapshots.SaveAsync(query, result, ct); return result; }
                missing = result;
            }
            catch (CatalogUnavailableException) { _unavailableUntil = _time.GetUtcNow().AddSeconds(30); }
        }
        return await snapshots.ReadAsync(query, ct) ?? missing ?? throw new CatalogCacheMissException();
    }
    public async Task<CatalogResult> QueryAsync(CatalogQuery input, string source = "auto", CancellationToken ct = default)
    {
        var query = input.Validate();
        if (source is not ("auto" or "web" or "vndb" or "cache")) throw new ArgumentException("--source 须为 auto、web、vndb 或 cache。");
        ct.ThrowIfCancellationRequested();
        if (source == "cache") return await snapshots.ReadAsync(query, ct) ?? throw new CatalogCacheMissException();
        if (source == "web" && remote is null) throw new ArgumentException("--source web 需要 --server 或 VN_LIBRARY_URL。");
        if (remote is not null && (source == "web" || source == "auto" && _time.GetUtcNow() >= _unavailableUntil))
        {
            try
            {
                var result = await remote.QueryAsync(query, ct);
                await snapshots.SaveAsync(query, result, ct);
                return result;
            }
            catch (CatalogUnavailableException) when (source == "auto") { _unavailableUntil = _time.GetUtcNow().AddSeconds(30); }
        }
        if (source == "auto" && remote is not null && await snapshots.ReadAsync(query, ct) is { } cached) return cached;
        try
        {
            var result = await upstream.QueryAsync(query, ct);
            await snapshots.SaveAsync(query, result, ct);
            return result;
        }
        catch (Exception e) when (source == "auto" && (e is HttpRequestException or TimeoutException || e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return await snapshots.ReadAsync(query, ct) ?? throw new CatalogCacheMissException();
        }
    }
}
