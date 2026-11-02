using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Client;

/// <summary>Compatibility adapter for callers/cache JSON. Business policy lives in Core.</summary>
public sealed class QueryRouter
{
    private readonly ClientCatalogQuery _query;
    public QueryRouter(VndbClient core, ICatalogSnapshots snapshots, WebLibraryClient? web = null, TimeProvider? time = null) =>
        _query = new(new VndbCatalogSource(core, time), snapshots, web, time);
    public async Task<MetadataResponse> ReadLocalAsync(string id, CancellationToken ct = default)
    {
        try { return (await _query.ReadLocalAsync(new("vn_get", Id: VndbId.Normalize(id, 'v')), ct)).ToMetadata(); }
        catch (CatalogCacheMissException) { throw new OfflineCacheMissException(); }
    }
    public async Task<MetadataResponse> QueryAsync(ClientQuery input, string source = "auto", CancellationToken ct = default)
    {
        try { return (await _query.QueryAsync(input.Request.ToCatalog(input.Kind, input.Id), source, ct)).ToMetadata(); }
        catch (CatalogCacheMissException) { throw new OfflineCacheMissException(); }
    }
}
