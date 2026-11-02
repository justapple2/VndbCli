using System.Security.Cryptography;
using System.Text;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed partial class LocalShelfReader
{
    private void RequireSnapshot(ShelfSnapshot snapshot)
    {
        if (snapshot.Origin != origin) throw new InvalidDataException("快照服务地址不符。");
        if (snapshot.Items is null || snapshot.Plays is null || snapshot.Installations is null ||
            snapshot.Items.Concat(snapshot.RemovedItems ?? []).Any(i => i?.Item?.Details is null || string.IsNullOrWhiteSpace(i.Item.Id) || i.Metadata is { Developers: null }) ||
            snapshot.Plays.Any(p => p?.Details is null) || snapshot.SummaryOnly && snapshot.Items.Any(i => i.State?.Progress?.Statuses is null))
            throw new InvalidDataException("书架快照字段不完整，原件保留。");
    }
    private static LibraryItemView LocalSummary(LibraryItemView value) => ShelfSummaries.Item(value) with
    { State = value.State ?? new(PersonalProgress.FromStatuses([], value.PlayCount > 0), false) };
    private sealed record SummaryRows(List<LibraryItemView> Active, LibraryItemView[] Removed);
    private static async Task<SummaryRows?> ReadSummariesAsync(Func<int, bool, CancellationToken, Task<ShelfSummaryPage?>> read, CancellationToken ct)
    {
        var active = new List<LibraryItemView>(); var removed = new List<LibraryItemView>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deleted in new[] { false, true })
        {
            int? total = null; int? size = null;
            for (var page = 1; ; page++)
            {
                var result = await read(page, deleted, ct);
                if (result is null && !deleted && page == 1) return null;
                if (result is null) throw new InvalidDataException("书架能力在读取期间变化，请重试。");
                ShelfSummaries.RequirePage(result, page, deleted);
                total ??= result.Total; size ??= result.PageSize;
                if (total != result.Total || size != result.PageSize || result.Items.Any(i => !ids.Add(i.Item.Id)))
                    throw new InvalidDataException("书架分页期间条目变化，原快照保留。");
                (deleted ? removed : active).AddRange(result.Items);
                if (!result.More) break;
                if (page >= 100) throw new InvalidDataException("书架摘要超过10000项。");
            }
        }
        return new(active, removed.ToArray());
    }
    private sealed record ItemDetailSnapshot(string? Origin, string ItemId, LibraryItemView Value, int FormatVersion = 1);
    public async Task<LibraryItemView> ItemAsync(string id, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        var state = new JsonStateFile<ItemDetailSnapshot>(directory, "item-details/" + hash + ".json");
        var cached = await state.ReadAsync(ct);
        if (cached is not null && (cached.FormatVersion != 1 || cached.Origin != origin || cached.ItemId != id || cached.Value?.Item?.Details is null || cached.Value.Item.Id != id || cached.Value.Item.DeletedAt is not null))
            throw new InvalidDataException("条目详情快照来源不符，原件保留。");
        if (remote?.Item is not null)
        {
            try
            {
                var value = await remote.Item(id, ct);
                if (value?.Item?.Details is null || value.Item.Id != id || value.Item.DeletedAt is not null) throw new InvalidDataException("条目详情身份或移除状态不符。");
                using var budget = await LocalFileLease.AcquireAsync(directory, "item-details-budget", ct);
                RequireDetailBudget(hash, value);
                await state.SaveAsync(new(origin, id, value), ct); return value;
            }
            catch (CatalogUnavailableException) { }
        }
        var shelf = await SnapshotState().ReadAsync(ct);
        if (shelf is not null) RequireSnapshot(shelf);
        var current = shelf?.Items.SingleOrDefault(i => i.Item.Id == id);
        if (shelf?.RemovedItems?.Any(i => i.Item.Id == id) == true) throw new CatalogUnavailableException();
        if (cached is not null && (current is null || cached.Value.Item.Revision >= current.Item.Revision && cached.Value.Item.Details.NovelId == current.Item.Details.NovelId)) return cached.Value;
        if (shelf is { SummaryOnly: false } && current is not null) return current;
        var localItem = (await local.ReadAsync(ct)).Items.SingleOrDefault(i => i.Item.Id == id);
        if (localItem is not null && LocalInstallationRegistration.IsPending(id)) return localItem;
        throw new CatalogUnavailableException();
    }
    private void RequireDetailBudget(string hash, LibraryItemView value)
    {
        var folder = Path.Combine(directory, "item-details"); LocalFileLease.RequireOrdinaryAncestors(folder);
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ItemDetailSnapshot(origin, value.Item.Id, value), VndbJson.Options).LongLength;
        if (size > 8 * 1024 * 1024) throw new InvalidDataException("单条详情超过8MiB，未替换缓存。");
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [];
        long total = size; var count = 1;
        foreach (var file in files.Where(f => Path.GetFileName(f) != hash + ".json"))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("详情缓存含链接，未写入。");
            total += new FileInfo(file).Length; count++;
        }
        if (count > 1000 || total > 256L * 1024 * 1024) throw new InvalidDataException("当前服务的详情缓存已达1000项或256MiB；请归档旧缓存后重试，原件保留。");
    }
}
