using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>Exact scoped page snapshots only; never a local database or a promise of offline global search.</summary>
public sealed class PublicCatalogReader(string directory, string? origin,
    Func<PublicCatalogQuery, CancellationToken, Task<PublicCatalogPage>>? remote)
{
    private sealed record Saved(string? Origin, PublicCatalogQuery Query, PublicCatalogPage Page);
    public async Task<PublicCatalogPage> ReadAsync(PublicCatalogQuery input, bool cacheOnly, CancellationToken ct)
    {
        var query = PublicCatalogRules.Validate(input);
        var name = LocalArchiveFormat.Hash(query) + ".json";
        var path = Path.Combine(directory, "public-catalog", name);
        if (!cacheOnly && remote is not null)
        {
            try
            {
                var result = await remote(query, ct);
                if (!PublicCatalogRules.ValidPage(result, query))
                    throw new InvalidDataException("公共目录回复无效，未替换已有JSON。");
                await QueryScopeBinding.CheckAsync(directory, origin, true, ct);
                using var lease = await LocalFileLease.AcquireAsync(directory, "public-catalog/" + name, ct);
                await JsonSnapshotStore.AtomicWriteAsync(path, new Saved(LocalQueryScope.Normalize(origin), query, result), true, ct);
                return result;
            }
            catch (CatalogUnavailableException) { }
        }
        if (!File.Exists(path)) throw new CatalogCacheMissException();
        await QueryScopeBinding.CheckAsync(directory, origin, true, ct);
        var saved = JsonSerializer.Deserialize<Saved>(await QueryDataMigration.BytesAsync(path, ct), VndbJson.Options)
            ?? throw new InvalidDataException("公共目录快照损坏，原件保留。");
        if (saved.Origin != LocalQueryScope.Normalize(origin) || saved.Query != query || !PublicCatalogRules.ValidPage(saved.Page, query))
            throw new InvalidDataException("公共目录快照作用域不符，原件保留。");
        return saved.Page with { Source = "client_catalog_cache" };
    }
}
