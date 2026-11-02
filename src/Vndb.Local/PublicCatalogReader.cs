using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>Exact scoped page snapshots only; never a local database or a promise of offline global search.</summary>
public sealed class PublicCatalogReader(string directory, string? origin,
    Func<PublicCatalogQuery, CancellationToken, Task<PublicCatalogPage>>? remote,
    Func<string, CancellationToken, Task<IReadOnlyList<CatalogProducerChoice>>>? producers = null)
{
    private sealed record Saved(string? Origin, PublicCatalogQuery Query, PublicCatalogPage Page);
    private sealed record SavedProducers(string? Origin, string Query, IReadOnlyList<CatalogProducerChoice> Items);
    public async Task<IReadOnlyList<CatalogProducerChoice>> ProducersAsync(string input, bool cacheOnly, CancellationToken ct)
    {
        var query = input.Trim();
        if (query.Length > 200 || query.Any(char.IsControl)) throw new ArgumentException("会社搜索无效。");
        var state = new JsonStateFile<SavedProducers>(directory, "catalog-producers.json", 128 * 1024);
        static void Validate(IReadOnlyList<CatalogProducerChoice> rows)
        {
            if (rows is null || rows.Any(r => r is null) || rows.Count > 60 || rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count ||
                rows.Any(r => r is null || VndbId.Normalize(r.Id, 'p') != r.Id || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 500 || r.Original?.Length > 500 || r.NovelCount < 0))
                throw new InvalidDataException("会社选择结果无效，原快照保留。");
        }
        if (!cacheOnly && producers is not null)
        {
            try
            {
                var rows = await producers(query, ct); Validate(rows);
                await QueryScopeBinding.CheckAsync(directory, origin, true, ct);
                await state.SaveAsync(new(LocalQueryScope.Normalize(origin), query, rows), ct); return rows;
            }
            catch (CatalogUnavailableException) { }
        }
        await QueryScopeBinding.CheckAsync(directory, origin, false, ct);
        var saved = await state.ReadAsync(ct) ?? throw new CatalogCacheMissException();
        if (saved.Origin != LocalQueryScope.Normalize(origin)) throw new InvalidDataException("会社选择快照作用域不符。");
        if (saved.Query != query) throw new CatalogCacheMissException();
        Validate(saved.Items); return saved.Items;
    }
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
