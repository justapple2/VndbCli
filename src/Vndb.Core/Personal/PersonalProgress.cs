namespace Vndb.Core;

/// <summary>Derived from exact associated play records. Absence is never a claim of not having played.</summary>
public sealed record PersonalProgress(bool HasRecord, bool HasPlayed, bool NeedsReview, IReadOnlyList<string> Statuses)
{
    public string State => NeedsReview ? "review" : !HasRecord ? "unrecorded" : HasPlayed ? "played" : "unplayed";
    public bool Matches(string? state = null, string? status = null) =>
        (string.IsNullOrEmpty(state) || State == state) && (string.IsNullOrEmpty(status) || Statuses.Contains(status));
    public static PersonalProgress FromStatuses(IEnumerable<string> statuses, bool needsReview = false)
    {
        var values = statuses.Distinct(StringComparer.Ordinal).ToArray();
        return new(values.Length > 0, values.Any(PersonalRules.IsPlayed),
            needsReview || values.Any(s => !PersonalRules.States.Contains(s)),
            PersonalRules.States.Where(values.Contains).ToArray());
    }
}
