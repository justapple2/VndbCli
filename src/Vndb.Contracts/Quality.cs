namespace Vndb.Contracts;

public sealed record QualityIssue(string Kind, string EntityType, string EntityId, string Title,
    string Message, string Action, string? NovelId = null, string? DeviceId = null, string? InstallationId = null);
public sealed record QualityPage(IReadOnlyList<QualityIssue> Items, IReadOnlyDictionary<string, int> Counts,
    int Total, int Page, int PageSize, bool More);
