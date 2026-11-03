namespace Vndb.Core;

public sealed record PersonalLabel(string Id, string Name, string Kind);
public sealed record PersonalMembership(string ItemId, bool Favorite, IReadOnlyList<string> LabelIds);
public sealed record PersonalOrganizationState(long Revision, IReadOnlyList<PersonalLabel> Labels, IReadOnlyList<PersonalMembership> Items);
public sealed record PersonalOrganizationChange(long ExpectedRevision, PersonalLabel? Label = null, string? RemoveLabel = null,
    IReadOnlyList<PersonalMembership>? Items = null);
public sealed class PersonalOrganizationConflictException() : InvalidOperationException("个人分类已变化，请保留原稿并重新读取后确认。");

/// <summary>Virtual personal organization only. No directory, launch or play-state semantics.</summary>
public static class PersonalOrganizationRules
{
    public static PersonalOrganizationState Empty { get; } = new(0, [], []);
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 100 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static void Validate(PersonalOrganizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 0 || state.Labels is null || state.Items is null || state.Labels.Count > 200 || state.Items.Count > 20000 ||
            state.Labels.Any(l => l is null || !ValidId(l.Id) || l.Kind is not ("collection" or "tag") || string.IsNullOrWhiteSpace(l.Name) || l.Name != l.Name.Trim() || l.Name.Length > 80 || l.Name.Any(char.IsControl)) ||
            state.Labels.Select(l => l.Id).Distinct().Count() != state.Labels.Count ||
            state.Labels.Select(l => l.Kind + ":" + l.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Labels.Count)
            throw new ArgumentException("分类最多200项，名称须为1–80字且同类型不重名。");
        var labels = state.Labels.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (state.Items.Any(i => i is null || !ValidId(i.ItemId) || i.LabelIds is null || i.LabelIds.Count > 32 || i.LabelIds.Distinct().Count() != i.LabelIds.Count || i.LabelIds.Any(id => !labels.Contains(id))) ||
            state.Items.Select(i => i.ItemId).Distinct().Count() != state.Items.Count)
            throw new ArgumentException("个人分类条目或成员关系无效；每项最多32个分类。");
    }
    public static PersonalOrganizationState Apply(PersonalOrganizationState state, PersonalOrganizationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Validate(state);
        if (change.ExpectedRevision != state.Revision) throw new PersonalOrganizationConflictException();
        if ((change.Label is null ? 0 : 1) + (change.RemoveLabel is null ? 0 : 1) + (change.Items is null ? 0 : 1) != 1 || change.Items is { Count: < 1 or > 100 })
            throw new ArgumentException("每次仅修改一个分类或1–100项成员；请先预览再应用。");
        var labels = state.Labels.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var items = state.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
        if (change.Label is { } label)
        {
            if (!ValidId(label.Id) || labels.TryGetValue(label.Id, out var old) && old.Kind != label.Kind) throw new ArgumentException("分类身份或类型无效。");
            labels[label.Id] = label;
        }
        if (change.RemoveLabel is { } removed)
        {
            if (!labels.Remove(removed)) throw new ArgumentException("分类不存在。");
            foreach (var row in items.Values.ToArray()) items[row.ItemId] = row with { LabelIds = row.LabelIds.Where(id => id != removed).ToArray() };
        }
        if (change.Items is { } edits)
        {
            if (edits.Any(i => i is null || !ValidId(i.ItemId)) || edits.Select(i => i.ItemId).Distinct().Count() != edits.Count) throw new ArgumentException("成员身份重复或无效。");
            foreach (var row in edits) items[row.ItemId] = row;
        }
        var result = new PersonalOrganizationState(checked(state.Revision + 1), labels.Values.OrderBy(l => l.Id, StringComparer.Ordinal).ToArray(),
            items.Values.Where(i => i.Favorite || i.LabelIds is null || i.LabelIds.Count > 0).OrderBy(i => i.ItemId, StringComparer.Ordinal).ToArray());
        Validate(result); return result;
    }
}
