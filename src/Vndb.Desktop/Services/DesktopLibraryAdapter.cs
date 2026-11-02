using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;

namespace Vndb.Desktop.Services;

public sealed record ShelfResult(ShelfSnapshot Snapshot, string Source);
public interface IDesktopLibrary
{
    Task<ShelfResult> ShelfAsync(CancellationToken ct);
    Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct);
    Task<IReadOnlyList<CatalogProducerChoice>> CatalogProducersAsync(string query, bool cacheOnly, CancellationToken ct) => throw new NotSupportedException("当前资料适配器不支持会社选择。");
    Task<PublicCatalogPage> BrowseCatalogAsync(PublicCatalogQuery query, bool cacheOnly, CancellationToken ct) =>
        throw new NotSupportedException("当前资料适配器不支持公共目录浏览。");
    Task<MetadataResponse> ReleasesAsync(string id, string source, bool refresh, CancellationToken ct) =>
        throw new NotSupportedException("当前资料适配器不支持发行版本查询。");
    Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct);
    Task<LibraryItemView?> ItemAsync(string itemId, CancellationToken ct) => Task.FromResult<LibraryItemView?>(null);
}

/// <summary>Read-only presentation adapter; transport and local policies remain injected services.</summary>
public sealed class DesktopLibraryAdapter : IDesktopLibrary
{
    private LocalShelfReader? _shelf;
    private QueryRouter? _query;
    private PublicCatalogReader? _catalog;
    public async Task<IReadOnlyList<CatalogProducerChoice>> CatalogProducersAsync(string query, bool cacheOnly, CancellationToken ct)
    {
        try { return await (_catalog ?? throw new InvalidOperationException("请先加载设置。")).ProducersAsync(query, cacheOnly, ct); }
        catch (CatalogCacheMissException) { throw new OfflineCacheMissException(); }
    }
    public LocalLibrary Catalog { get; private set; } = new([], []);
    public void Bind(LocalShelfReader shelf, QueryRouter query, PublicCatalogReader? catalog = null) { _shelf = shelf; _query = query; _catalog = catalog; }
    public async Task<PublicCatalogPage> BrowseCatalogAsync(PublicCatalogQuery query, bool cacheOnly, CancellationToken ct)
    {
        try { return await (_catalog ?? throw new InvalidOperationException("请先加载设置。")).ReadAsync(query, cacheOnly, ct); }
        catch (CatalogCacheMissException) { throw new OfflineCacheMissException(); }
    }
    public void Seed(LocalLibrary catalog) => Catalog = catalog;
    public async Task<ShelfResult> ShelfAsync(CancellationToken ct)
    {
        var shelf = _shelf ?? throw new InvalidOperationException("请先加载设置。");
        ShelfReadResult result;
        try { result = await shelf.ShelfAsync(ct); }
        catch (CatalogCacheMissException) { throw new OfflineCacheMissException(); }
        Catalog = shelf.Catalog;
        var source = result.Source switch
        {
            ShelfSource.Web => "Web 本地资料库",
            ShelfSource.LocalLibrary => "本机JSON目录与作品快照",
            _ => "本机书架快照 · " + result.Snapshot.FetchedAt.ToLocalTime().ToString("g")
        };
        return new(result.Snapshot, source + (shelf.RegistrationReviewNeeded ? " · 登记映射待核对，原书架保留" : ""));
    }
    public Task<MetadataResponse> QueryAsync(string text, string source, CancellationToken ct) =>
        (_query ?? throw new InvalidOperationException("请先加载设置。")).QueryAsync(ClientQuery.FromText(text, 20), source, ct);
    public async Task<LibraryItemView?> ItemAsync(string itemId, CancellationToken ct)
    {
        try { return await (_shelf ?? throw new InvalidOperationException("请先加载设置。")).ItemAsync(itemId, ct); }
        catch (CatalogUnavailableException) { throw new WebUnavailableException(); }
    }
    public Task<MetadataResponse> ReleasesAsync(string id, string source, bool refresh, CancellationToken ct) =>
        (_query ?? throw new InvalidOperationException("请先加载设置。")).QueryAsync(
            new("vn_releases", new(Mode: refresh ? "explicit_refresh" : "local_first"), VndbId.Normalize(id, 'v')), source, ct);
    public async Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct)
    {
        try { return await (_shelf ?? throw new InvalidOperationException("请先加载设置。")).PlaysAsync(itemId, ct); }
        catch (CatalogUnavailableException) { throw new WebUnavailableException(); }
    }
}
