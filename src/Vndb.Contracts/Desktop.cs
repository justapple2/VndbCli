namespace Vndb.Contracts;

// Read-only projection of the existing play endpoint. Workbook provenance stays on Web.
public sealed record PersonalPlayView(string Id, PersonalPlayData Details, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null, PlayTimeSummary? Time = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ItemId = null);
