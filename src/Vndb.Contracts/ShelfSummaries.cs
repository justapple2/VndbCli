using Vndb.Core;

namespace Vndb.Contracts;

// Read-only list projection. A summary is never an item-edit or metadata-import payload.
public sealed record ShelfSummaryPage(IReadOnlyList<LibraryItemView> Items, int Page, int PageSize, int Total, bool More,
    string Format = "vn-shelf-summary", int FormatVersion = 1);
public sealed record ItemHead(string Id, long Revision, bool Deleted);
public sealed record ItemHeadPage(IReadOnlyList<ItemHead> Items, int Page, int PageSize, int Total, bool More);

public static class ShelfSummaries
{
    public static MetadataSnapshot? Metadata(MetadataSnapshot? value) => value is null ? null : value with
    {
        Novel = value.Novel is null ? null : value.Novel with { Description = null, ReleaseCatalog = null, Relations = null, Aliases = [], Developers = [] },
        Developers = value.Developers.Select(p => p with { Description = null, Aliases = [] }).ToArray(),
        Producer = value.Producer is null ? null : value.Producer with { Description = null, Aliases = [] }
    };
    public static LibraryItemView Item(LibraryItemView value) => value with
    {
        Item = value.Item with { Details = value.Item.Details with { Notes = null, ExtraNotes = null } }, Metadata = Metadata(value.Metadata)
    };
    public static void RequirePage(ShelfSummaryPage value, int page, bool deleted)
    {
        if (value.Format != "vn-shelf-summary" || value.FormatVersion != 1 || value.Items is null || value.Page != page || value.PageSize is < 1 or > 100 ||
            value.Total < 0 || value.More != ((long)page * value.PageSize < value.Total) || value.Items.Count != Math.Min(value.PageSize, Math.Max(0, value.Total - (long)(page - 1) * value.PageSize)))
            throw new InvalidDataException("书架摘要分页无效，原快照保留。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in value.Items)
        {
            if (row?.Item?.Details is null || string.IsNullOrWhiteSpace(row.Item.Id) || row.Item.Revision < 1 || !ids.Add(row.Item.Id) ||
                (row.Item.DeletedAt is not null) != deleted || row.State?.Progress is null || row.PlayCount < 0 || row.State.Progress.Statuses is null ||
                row.Item.Details.Notes is not null || row.Item.Details.ExtraNotes is not null || row.Metadata?.Novel is { } n &&
                (n.Id != row.Item.Details.NovelId || n.Description is not null || n.ReleaseCatalog is not null || n.Relations is not null))
                throw new InvalidDataException("书架摘要条目无效，未替换已有资料。");
            var progress = row.State.Progress;
            if (progress.HasRecord != (row.PlayCount > 0) || progress.HasPlayed != progress.Statuses.Any(PersonalRules.IsPlayed) ||
                !progress.Statuses.SequenceEqual(PersonalRules.States.Where(progress.Statuses.Contains)) ||
                progress.Statuses.Count > row.PlayCount || progress.HasRecord && progress.Statuses.Count == 0 && !progress.NeedsReview ||
                row.State.LatestStatus is { } latest && !progress.Statuses.Contains(latest) && !(progress.NeedsReview && !PersonalRules.States.Contains(latest)) ||
                row.PlayCount == 0 && row.State.LatestStatus is not null || row.PlayCount > 0 && row.State.LatestStatus is null)
                throw new InvalidDataException("书架摘要个人状态不一致。");
        }
    }
}
