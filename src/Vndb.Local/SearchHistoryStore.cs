namespace Vndb.Local;

/// <summary>Small local-only explicit submission history, isolated by service and channel.</summary>
public sealed class SearchHistoryStore(LocalQueryScope scope, string channel)
{
    private sealed record History(string? Origin, IReadOnlyList<string> Queries, int FormatVersion = 1);
    private JsonStateFile<History> State()
    {
        if (channel is not ("lookup" or "catalog")) throw new ArgumentException("历史作用域无效。");
        return new(scope.DirectoryPath, "search-history-" + channel + ".json", 32 * 1024);
    }
    private IReadOnlyList<string> Validate(History? value)
    {
        if (value is null) return [];
        if (value.FormatVersion != 1 || value.Origin != scope.Origin || value.Queries is null || value.Queries.Count > 20 ||
            value.Queries.Any(q => !Valid(q)) || value.Queries.Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Queries.Count)
            throw new InvalidDataException("搜索历史格式或服务作用域无效，原件保留。");
        return value.Queries;
    }
    private static bool Valid(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 200 && text == text.Trim() && !text.Any(char.IsControl);
    public async Task<IReadOnlyList<string>> ReadAsync(CancellationToken ct = default)
    { await QueryScopeBinding.CheckAsync(scope.DirectoryPath, scope.Origin, false, ct); return Validate(await State().ReadAsync(ct)); }
    public async Task<IReadOnlyList<string>> RecordAsync(string text, CancellationToken ct = default)
    {
        text = text.Trim(); if (!Valid(text)) throw new ArgumentException("请输入不超过200字符的查询文字。");
        await QueryScopeBinding.CheckAsync(scope.DirectoryPath, scope.Origin, true, ct);
        return Validate(await State().UpdateAsync(value => new(scope.Origin, new[] { text }.Concat(Validate(value).Where(q => !q.Equals(text, StringComparison.OrdinalIgnoreCase))).Take(20).ToArray()), ct));
    }
    public async Task<IReadOnlyList<string>> RemoveAsync(string? text, CancellationToken ct = default)
    {
        await QueryScopeBinding.CheckAsync(scope.DirectoryPath, scope.Origin, true, ct);
        return Validate(await State().UpdateAsync(value => new(scope.Origin, Validate(value).Where(q => text is not null && q != text).ToArray()), ct));
    }
}
