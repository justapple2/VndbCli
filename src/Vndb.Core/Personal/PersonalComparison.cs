using System.Globalization;

namespace Vndb.Core;

public enum PersonalOperationState { Pending, Uncertain, Conflict, Applied, Withdrawn }
public sealed record ComparedValue(string? Value, bool Known = true, bool Exists = true);
public sealed record FieldDifference(string Key, ComparedValue Original, ComparedValue Current, ComparedValue Incoming)
{
    public bool DraftChanged => Original.Known && Original != Incoming;
    public bool RemoteChanged => Original.Known && Current.Known && Original != Current;
    public bool Conflict => DraftChanged && RemoteChanged && Current != Incoming;
}

/// <summary>Small read-only comparisons. No merging, persistence, HTTP, paths or operation identity changes.</summary>
public static class PersonalComparison
{
    private static string? Text(object? value) => value switch
    {
        null => null, DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), decimal number => number.ToString("0.############################", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString()
    };
    private static FieldDifference Field(string key, object? before, object? current, object? incoming,
        bool beforeKnown = true, bool currentKnown = true, bool beforeExists = true, bool currentExists = true, bool incomingExists = true) =>
        new(key, new(Text(before), beforeKnown, beforeExists), new(Text(current), currentKnown, currentExists), new(Text(incoming), true, incomingExists));
    private static bool Visible(FieldDifference field) => !field.Original.Known || field.DraftChanged || field.RemoteChanged;
    public static IReadOnlyList<FieldDifference> Play(PersonalDetails? before, bool beforeKnown, PersonalDetails? current, bool currentKnown, PersonalDetails incoming)
    {
        object?[] Values(PersonalDetails? d) => [d?.Title, d?.NovelId, d?.Status, d?.StartedOn, d?.FinishedOn, d?.DurationMinutes, d?.Score, d?.Review, d?.Notes, d?.ExtraNotes, d?.Language];
        string[] keys = ["title", "novel_id", "status", "started_on", "finished_on", "duration_minutes", "score", "review", "notes", "extra_notes", "language"];
        var old = Values(before); var latest = Values(current); var draft = Values(incoming);
        return keys.Select((key, n) => Field(key, old[n], latest[n], draft[n], beforeKnown, currentKnown, before is not null, current is not null)).Where(Visible).ToArray();
    }
    public static IReadOnlyList<FieldDifference> Organization(PersonalOrganizationState before, PersonalOrganizationState? current, PersonalOrganizationChange change)
    {
        var incoming = PersonalOrganizationRules.Apply(before, change);
        if (current is not null) PersonalOrganizationRules.Validate(current);
        var rows = new List<FieldDifference>();
        var labelId = change.Label?.Id ?? change.RemoveLabel;
        if (labelId is not null)
        {
            var old = before.Labels.SingleOrDefault(l => l.Id == labelId); var latest = current?.Labels.SingleOrDefault(l => l.Id == labelId); var draft = incoming.Labels.SingleOrDefault(l => l.Id == labelId);
            rows.Add(Field("label:" + labelId + ":name", old?.Name, latest?.Name, draft?.Name, currentKnown: current is not null, beforeExists: old is not null, currentExists: latest is not null, incomingExists: draft is not null));
            rows.Add(Field("label:" + labelId + ":kind", old?.Kind, latest?.Kind, draft?.Kind, currentKnown: current is not null, beforeExists: old is not null, currentExists: latest is not null, incomingExists: draft is not null));
        }
        var oldItems = before.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
        var latestItems = current?.Items.ToDictionary(i => i.ItemId, StringComparer.Ordinal);
        foreach (var edit in change.Items ?? [])
        {
            var old = oldItems.GetValueOrDefault(edit.ItemId); var latest = latestItems?.GetValueOrDefault(edit.ItemId);
            string Names(PersonalOrganizationState state, IReadOnlyList<string>? ids) => string.Join(" / ", (ids ?? []).Order(StringComparer.Ordinal).Select(id => (state.Labels.SingleOrDefault(l => l.Id == id)?.Name ?? "已移除分类") + " [" + id + "]"));
            rows.Add(Field("item:" + edit.ItemId + ":favorite", old?.Favorite == true, latest?.Favorite == true, edit.Favorite, currentKnown: current is not null));
            rows.Add(Field("item:" + edit.ItemId + ":labels", Names(before, old?.LabelIds), current is null ? null : Names(current, latest?.LabelIds), Names(incoming, edit.LabelIds), currentKnown: current is not null));
        }
        return rows.Where(Visible).ToArray();
    }
}
