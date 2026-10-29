using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record ShelfBookPlays(LibraryItemView Item, IReadOnlyList<PersonalPlayView> Plays, bool NeedsReview);

/// <summary>Stable IDs first. Titles can explain ambiguity, never establish an association.</summary>
public static class ShelfPlayResolver
{
    public static IReadOnlyList<ShelfBookPlays> Resolve(ShelfSnapshot snapshot)
    {
        var items = snapshot.Items.Where(i => i.Item.DeletedAt is null).ToArray();
        var active = items.ToDictionary(i => i.Item.Id, StringComparer.Ordinal);
        var universe = items.Concat(snapshot.RemovedItems ?? []).DistinctBy(i => i.Item.Id).ToArray();
        var novels = universe.Where(i => i.Item.Details.NovelId is not null).ToLookup(i => i.Item.Details.NovelId!, StringComparer.Ordinal);
        var names = items.Where(i => i.Item.Details.NovelId is null).ToLookup(i => i.Item.Details.Title, StringComparer.Ordinal);
        var result = items.ToDictionary(i => i.Item.Id, _ => new List<PersonalPlayView>(), StringComparer.Ordinal);
        var review = new HashSet<string>(StringComparer.Ordinal);
        var confirmed = (snapshot.ConfirmedPlayItems ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var play in snapshot.Plays.Where(p => p.DeletedAt is null))
        {
            string? id = play.ItemId;
            if (id is null)
            {
                var deterministic = "item-" + play.Id;
                if (play.Details.NovelId is null && active.ContainsKey(deterministic) && active[deterministic].Item.Details.NovelId is null) id = deterministic;
                else if (play.Details.NovelId is { } vn && snapshot.RemovedItems is not null && novels[vn].Count() == 1) id = novels[vn].Single().Item.Id;
                else
                {
                    var candidates = play.Details.NovelId is { } known ? novels[known] : names[play.Details.Title];
                    foreach (var candidate in candidates.Where(c => !confirmed.Contains(c.Item.Id))) review.Add(candidate.Item.Id);
                    continue;
                }
                if (confirmed.Contains(id)) continue; // A newer complete item read supersedes unresolved legacy guesses.
            }
            if (!active.TryGetValue(id, out var item)) continue; // Removed/other items never fall back to another edition.
            if (play.Details.NovelId != item.Item.Details.NovelId) { review.Add(id); continue; }
            result[id].Add(play.ItemId == id ? play : play with { ItemId = id });
            if (!PersonalRules.States.Contains(play.Details.Status)) review.Add(id);
        }
        return items.Select(i => new ShelfBookPlays(i, result[i.Item.Id], review.Contains(i.Item.Id) || i.PlayCount > result[i.Item.Id].Count)).ToArray();
    }
    public static IReadOnlyList<PersonalPlayView> ForItem(IReadOnlyList<PersonalPlayView> plays, string itemId)
    {
        if (plays.Any(p => p.ItemId is not null && p.ItemId != itemId)) throw new InvalidDataException("游玩记录属于其他条目，请刷新关联；未采用返回结果。");
        return plays.Where(p => p.DeletedAt is null).Select(p => p.ItemId is null ? p with { ItemId = itemId } : p).ToArray();
    }
    public static void RequireSelection(string? itemId, PersonalPlayView? play)
    {
        if (play is not null && (play.DeletedAt is not null || itemId is null || play.ItemId != itemId))
            throw new ArgumentException("游玩记录与所选条目关联尚未确认，请重新读取该条目的记录后选择。");
    }
}
