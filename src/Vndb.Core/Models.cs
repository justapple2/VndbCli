using System.Text.Json.Serialization;

namespace Vndb.Core;

public sealed record QueryPage<T>
{
    public required IReadOnlyList<T> Results { get; init; }
    public required bool More { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public record VisualNovelSummary
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    [JsonPropertyName("alttitle")]
    public string? AltTitle { get; init; }
    [JsonPropertyName("olang")]
    public string? OriginalLanguage { get; init; }
    // Keep partial dates and TBA intact; these are not necessarily DateOnly values.
    public string? Released { get; init; }
    public IReadOnlyList<ProducerSummary> Developers { get; init; } = [];
}

public sealed record VisualNovel : VisualNovelSummary
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<NovelRelation>? Relations { get; init; }
    // Absent in legacy snapshots: do not change their normalized hashes or imply an empty catalogue.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReleaseCatalog? ReleaseCatalog { get; init; }
    public NovelImage? Image { get; init; }
    public IReadOnlyList<NovelTitle> Titles { get; init; } = [];
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public IReadOnlyList<string> Languages { get; init; } = [];
    public IReadOnlyList<string> Platforms { get; init; } = [];
    public string? Description { get; init; }
    public double? Rating { get; init; }
    [JsonPropertyName("votecount")]
    public int? VoteCount { get; init; }
    public int? LengthMinutes { get; init; }
}
public sealed record NovelRelation(string Id, string Relation, bool Official);

public sealed record NovelImage
{
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string? Thumbnail { get; init; }
    public IReadOnlyList<int> Dims { get; init; } = [];
    public double? Sexual { get; init; }
    public double? Violence { get; init; }
}

public sealed record NovelTitle
{
    public required string Lang { get; init; }
    public required string Title { get; init; }
    public string? Latin { get; init; }
    public bool Official { get; init; }
    public bool Main { get; init; }
}

public record ProducerSummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Original { get; init; }
}

public sealed record Producer : ProducerSummary
{
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? Lang { get; init; }
    public string? Type { get; init; }
    public string? Description { get; init; }
}

public sealed record AuthInfo
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
}

public sealed record DatabaseStats
{
    public long Chars { get; init; }
    public long Producers { get; init; }
    public long Releases { get; init; }
    public long Staff { get; init; }
    public long Tags { get; init; }
    public long Traits { get; init; }
    public long Vn { get; init; }
}
