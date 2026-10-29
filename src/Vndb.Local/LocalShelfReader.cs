using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

// Local persistence envelope, never an HTTP request or response.
public sealed record ShelfSnapshot(string Origin, IReadOnlyList<LibraryItemView> Items,
    IReadOnlyList<PersonalPlayView> Plays, IReadOnlyList<LocalInstallationView> Installations, DateTimeOffset FetchedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LibraryItemView>? RemovedItems = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ConfirmedPlayItems = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LocalOwnedPackage>? Packages = null);
public enum ShelfSource { Web, LocalLibrary, Snapshot }
public sealed record ShelfReadResult(ShelfSnapshot Snapshot, ShelfSource Source);

/// <summary>Two read operations supplied by the composition root; no concrete SDK dependency.</summary>
public sealed record LibraryReadOperations(Func<int, CancellationToken, Task<ItemPage>> Items,
    Func<string?, CancellationToken, Task<IReadOnlyList<PersonalPlayView>>> Plays,
    Func<int, CancellationToken, Task<ItemPage>>? RemovedItems = null);

/// <summary>Client-only aggregation and offline snapshots. Never opens a business database.</summary>
public sealed class LocalShelfReader(LocalLibraryStore local, string directory, string? origin,
    LibraryReadOperations? remote = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public LocalLibrary Catalog { get; private set; } = new([], []);
    public bool RegistrationReviewNeeded { get; private set; }
    public async Task<ShelfReadResult> ShelfAsync(CancellationToken ct)
    {
        RegistrationReviewNeeded = false;
        var state = new JsonStateFile<ShelfSnapshot>(directory, "shelf.json"); await state.ObserveAsync(ct);
        if (remote is not null)
        {
            try
            {
                var items = await ReadPagesAsync(remote.Items, ct);
                var removedItems = remote.RemovedItems is null ? null : (await ReadPagesAsync(remote.RemovedItems, ct)).Where(i => i.Item.DeletedAt is not null).ToArray();
                var removed = (removedItems ?? []).Select(i => i.Item.Id).ToHashSet(StringComparer.Ordinal);
                var plays = await remote.Plays(null, ct);
                var prior = await state.ReadAsync(ct, observe: false);
                if (prior is not null && prior.Origin != origin) throw new InvalidDataException("快照服务地址不符。");
                Catalog = await local.ReadAsync(ct);
                var ids = items.Select(i => i.Item.Id).ToHashSet(StringComparer.Ordinal);
                items.AddRange(Catalog.Items.Where(i => i.Item.DeletedAt is null && !removed.Contains(i.Item.Id) && ids.Add(i.Item.Id)));
                var snapshot = new ShelfSnapshot(origin!, items, plays, Catalog.Installations, _time.GetUtcNow(), removedItems, Packages: Catalog.Packages);
                await state.SaveAsync(snapshot, ct);
                return new(snapshot, ShelfSource.Web);
            }
            catch (CatalogUnavailableException) { }
        }
        Catalog = await local.ReadAsync(ct);
        var cached = await state.ReadAsync(ct);
        if (cached is not null && cached.Origin != origin) throw new InvalidDataException("快照服务地址不符。");
        if (cached is null)
        {
            if (Catalog.Items.Count == 0) throw new CatalogCacheMissException();
            return new(new(origin ?? "", Catalog.Items.Where(i => i.Item.DeletedAt is null).ToArray(), [], Catalog.Installations, _time.GetUtcNow(), Packages: Catalog.Packages), ShelfSource.LocalLibrary);
        }
        var refreshed = new List<LibraryItemView>();
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in cached.Items)
        {
            if (LocalInstallationRegistration.IsPending(item.Item.Id) && Catalog.Items.All(i => i.Item.Id != item.Item.Id))
            {
                try
                {
                    if (await new LocalRegistrationSync(local).MappedItemAsync(item.Item.Id, origin, ct, item) is { } mapped &&
                        Catalog.Items.SingleOrDefault(i => i.Item.Id == mapped) is { } current)
                    { refreshed.Add(current); mappings.Add(item.Item.Id, mapped); continue; }
                }
                catch (Exception e) when (e is InvalidDataException or IOException or JsonException or ClientApiException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) { }
                RegistrationReviewNeeded = true;
            }
            refreshed.Add(item);
        }
        var known = refreshed.Select(i => i.Item.Id).ToHashSet(StringComparer.Ordinal);
        // Newly registered offline works have no Web tombstone; older Web removals remain authoritative.
        var pendingItems = Catalog.Items.Where(i => i.Item.DeletedAt is null && LocalInstallationRegistration.IsPending(i.Item.Id) && known.Add(i.Item.Id));
        return new(await OverlayPlaysAsync(cached with { Items = refreshed.Concat(pendingItems).DistinctBy(i => i.Item.Id).ToArray(), Installations = Catalog.Installations, Packages = Catalog.Packages,
            Plays = cached.Plays.Select(p => p.ItemId is { } old && mappings.TryGetValue(old, out var id) ? p with { ItemId = id } : p).ToArray() }, ct), ShelfSource.Snapshot);
    }
    private static async Task<List<LibraryItemView>> ReadPagesAsync(Func<int, CancellationToken, Task<ItemPage>> read, CancellationToken ct)
    {
        var items = new List<LibraryItemView>();
        for (var page = 1; ; page++)
        {
            var result = await read(page, ct); items.AddRange(result.Items);
            if (!result.More) return items;
            if (page >= 100) throw new InvalidDataException("书架超过10000项，请分库管理。");
        }
    }
    public async Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string itemId, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(itemId)));
        var file = Path.Combine(directory, "item-plays", hash + ".json");
        var currentFile = Path.Combine(directory, "item-plays", hash + ".v2.json");
        var observed = File.Exists(currentFile) ? QueryDataMigration.Hash(await QueryDataMigration.BytesAsync(currentFile, ct)) : "missing";
        var shelfFile = Path.Combine(directory, "shelf.json");
        var shelfObserved = File.Exists(shelfFile) ? QueryDataMigration.Hash(await QueryDataMigration.BytesAsync(shelfFile, ct)) : "missing";
        if (remote is not null)
        {
            try
            {
                var plays = ShelfPlayResolver.ForItem(await remote.Plays(itemId, ct), itemId);
                using var lease = await LocalFileLease.AcquireAsync(directory, "item-plays/" + itemId, ct);
                var current = File.Exists(currentFile) ? QueryDataMigration.Hash(await QueryDataMigration.BytesAsync(currentFile, ct)) : "missing";
                if (current != observed) throw new InvalidDataException("个人记录快照已被其他客户端更新，请重新读取。");
                var priorItem = File.Exists(currentFile) ? await ReadItemPlaysAsync(currentFile, itemId, ct) : null; // Corrupt/foreign state is never reset by a successful network read.
                var shelfCurrent = File.Exists(shelfFile) ? QueryDataMigration.Hash(await QueryDataMigration.BytesAsync(shelfFile, ct)) : "missing";
                if (shelfCurrent != shelfObserved) throw new InvalidDataException("书架快照已更新，请重新读取该条目的记录。");
                var priorShelf = shelfCurrent == "missing" ? null : JsonSerializer.Deserialize<ShelfSnapshot>(await QueryDataMigration.BytesAsync(shelfFile, ct), VndbJson.Options)
                    ?? throw new InvalidDataException("书架快照无效。");
                if (priorShelf is not null && priorShelf.Origin != origin) throw new InvalidDataException("快照服务地址不符。");
                var floor = priorItem?.FetchedAt ?? DateTimeOffset.MinValue;
                if (priorShelf is not null && priorShelf.FetchedAt > floor) floor = priorShelf.FetchedAt;
                var fetchedAt = _time.GetUtcNow(); if (fetchedAt <= floor) fetchedAt = floor.AddTicks(1);
                await JsonSnapshotStore.AtomicWriteAsync(currentFile, new ItemPlaySnapshot(origin, itemId, plays, fetchedAt), true, ct); return plays;
            }
            catch (CatalogUnavailableException) { }
        }
        try
        {
            var snapshot = await ShelfAsync(ct);
            var resolved = ShelfPlayResolver.Resolve(snapshot.Snapshot).SingleOrDefault(i => i.Item.Item.Id == itemId);
            if (resolved is not null && !resolved.NeedsReview) return resolved.Plays;
            if (snapshot.Source == ShelfSource.Snapshot) throw new CatalogUnavailableException(); // An authoritative newer shelf must not fall back to a superseded item cache.
        }
        catch (CatalogCacheMissException) { }
        if (File.Exists(currentFile)) return (await ReadItemPlaysAsync(currentFile, itemId, ct)).Plays;
        if (File.Exists(file)) return ShelfPlayResolver.ForItem(JsonSerializer.Deserialize<PersonalPlayView[]>(await QueryDataMigration.BytesAsync(file, ct), VndbJson.Options)
            ?? throw new InvalidDataException("个人记录快照无效。"), itemId);
        throw new CatalogUnavailableException();
    }
    private sealed record ItemPlaySnapshot(string? Origin, string ItemId, IReadOnlyList<PersonalPlayView> Plays, DateTimeOffset FetchedAt, int FormatVersion = 1);
    private async Task<ItemPlaySnapshot> ReadItemPlaysAsync(string file, string id, CancellationToken ct)
    {
        var value = JsonSerializer.Deserialize<ItemPlaySnapshot>(await QueryDataMigration.BytesAsync(file, ct), VndbJson.Options)
            ?? throw new InvalidDataException("个人记录快照无效。");
        if (value.FormatVersion != 1 || value.Origin != origin || value.ItemId != id) throw new InvalidDataException("个人记录快照服务或条目不符。");
        return value with { Plays = ShelfPlayResolver.ForItem(value.Plays, id) };
    }
    private async Task<ShelfSnapshot> OverlayPlaysAsync(ShelfSnapshot snapshot, CancellationToken ct)
    {
        var items = snapshot.Items.ToArray(); var plays = snapshot.Plays.ToList();
        var priorPlays = ShelfPlayResolver.Resolve(snapshot).ToDictionary(i => i.Item.Item.Id, i => i.Plays.Select(p => p.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var confirmed = (snapshot.ConfirmedPlayItems ?? []).ToHashSet(StringComparer.Ordinal);
        var updates = new List<(int Index, ItemPlaySnapshot Value)>();
        for (var n = 0; n < items.Length; n++)
        {
            var item = items[n]; var id = item.Item.Id; var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
            var file = Path.Combine(directory, "item-plays", hash + ".v2.json"); if (!File.Exists(file)) continue;
            var current = await ReadItemPlaysAsync(file, id, ct); if (current.FetchedAt <= snapshot.FetchedAt) continue;
            if (current.Plays.Any(p => p.Details.NovelId != item.Item.Details.NovelId)) continue; // Old association cannot override a corrected item.
            updates.Add((n, current));
        }
        foreach (var (n, current) in updates.OrderBy(u => u.Value.FetchedAt).ThenBy(u => u.Value.ItemId, StringComparer.Ordinal))
        {
            var item = items[n]; var id = item.Item.Id;
            var prior = priorPlays[id]; confirmed.Add(id);
            var accepted = current.Plays.Where(p => !plays.Any(other => other.Id == p.Id && other.Revision > p.Revision)).ToArray();
            var newer = current.Plays.Where(p => plays.Any(other => other.Id == p.Id && other.Revision > p.Revision)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var replaced = accepted.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            plays.RemoveAll(p => !newer.Contains(p.Id) && (p.ItemId == id || p.ItemId is null && prior.Contains(p.Id) || replaced.Contains(p.Id))); plays.AddRange(accepted);
            items[n] = item with { PlayCount = current.Plays.Count };
        }
        return snapshot with { Items = items, Plays = plays, ConfirmedPlayItems = confirmed.ToArray() };
    }
}
