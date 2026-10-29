using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class JsonLibraryStore : ILibraryStore
{
    public string DataDirectory { get; }
    public JsonLibraryStore(string dataDirectory)
    {
        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(Path.Combine(DataDirectory, "novels"));
        Directory.CreateDirectory(Path.Combine(DataDirectory, "audit"));
    }
    public LibraryEntry? Get(string id)
    {
        var path = Path.Combine(DataDirectory, "novels", VndbId.Normalize(id, 'v') + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<LibraryEntry>(File.ReadAllText(path), VndbJson.Options) : null;
    }
    public void Save(LibraryEntry entry)
    {
        var id = VndbId.Normalize(entry.Novel.Id, 'v');
        entry = entry with { Novel = entry.Novel with { ReleaseCatalog = ReleaseRules.Prefer(entry.Novel.ReleaseCatalog, Get(id)?.Novel.ReleaseCatalog) } };
        var temporary = Path.Combine(DataDirectory, "novels", "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, VndbJson.Options));
            File.Move(temporary, Path.Combine(DataDirectory, "novels", id + ".json"), overwrite: true);
            File.WriteAllText(Path.Combine(DataDirectory, "audit", Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(new { novel_id = id, entry.UpdatedAt, entry.CoverStatus, entry.CoverError }, VndbJson.Options));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private IEnumerable<LibraryEntry> All() => Directory.EnumerateFiles(Path.Combine(DataDirectory, "novels"), "v*.json")
        .Select(path => JsonSerializer.Deserialize<LibraryEntry>(File.ReadAllText(path), VndbJson.Options) ?? throw new InvalidDataException("资料库记录损坏。"));
    public LibraryPage List(string? query = null, string? producer = null, int page = 1, int limit = 24)
    {
        LibraryRules.ValidatePage(page, limit);
        producer = string.IsNullOrWhiteSpace(producer) ? null : VndbId.Normalize(producer, 'p');
        var q = query?.Trim().ToLowerInvariant() ?? "";
        var entries = All().Where(e => LibraryRules.SearchText(e.Novel).Contains(q, StringComparison.Ordinal) &&
            (producer is null || e.Producers.Any(p => p.Id == producer))).OrderByDescending(e => e.UpdatedAt).ThenBy(e => e.Novel.Id, StringComparer.Ordinal).ToArray();
        return new(entries.Skip((page - 1) * limit).Take(limit).ToArray(), page, limit, entries.Length, (long)page * limit < entries.Length);
    }
    public IReadOnlyList<ProducerCount> Producers() => All().SelectMany(e => e.Producers).GroupBy(p => p.Id)
        .Select(g => new ProducerCount(g.Key, g.First().Name, g.First().Original, g.Count())).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    public LibraryStatistics Statistics()
    {
        var all = All().ToArray();
        return new(all.Length, all.SelectMany(e => e.Producers).Select(p => p.Id).Distinct().Count(), all.Count(e => e.Cover is not null), all.Count(e => e.CoverStatus == "failed"));
    }
    public ArchiveLocation RegisterArchive(ArchiveRegistration registration)
    {
        var id = ArchiveRules.Validate(registration, Get(registration.NovelId));
        var folder = Path.Combine(DataDirectory, "archives");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, id + ".json");
        var existing = File.Exists(path) ? JsonSerializer.Deserialize<ArchiveLocation>(File.ReadAllText(path), VndbJson.Options) : null;
        if (existing is not null && existing.Registration.NovelId != registration.NovelId)
            throw new ArgumentException("同一机器原路径已经绑定其他作品，请先核对身份。");
        if (existing is not null && existing.Registration.PlanSha256 == registration.PlanSha256 &&
            existing.Registration.State != "planned" && registration.State == "planned")
            throw new ArgumentException("此批次已经归档或复原，不能降回等待归档状态。");
        if (existing?.Registration == registration) return existing;
        var now = DateTimeOffset.UtcNow;
        var location = new ArchiveLocation(id, registration, existing?.CreatedAt ?? now, now);
        var temporary = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(location, VndbJson.Options));
            File.Move(temporary, path, overwrite: true);
            File.WriteAllText(Path.Combine(DataDirectory, "audit", "archive-" + Guid.NewGuid().ToString("N") + ".json"),
                JsonSerializer.Serialize(location, VndbJson.Options));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return location;
    }
    public IReadOnlyList<ArchiveLocation> Archives(string? novelId = null, string? runId = null)
    {
        if (novelId is not null) novelId = VndbId.Normalize(novelId, 'v');
        var folder = Path.Combine(DataDirectory, "archives");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json").Select(path => JsonSerializer.Deserialize<ArchiveLocation>(File.ReadAllText(path), VndbJson.Options)
            ?? throw new InvalidDataException("归档记录损坏。"))
            .Where(value => (novelId is null || value.Registration.NovelId == novelId) && (runId is null || value.Registration.RunId == runId))
            .OrderBy(value => value.Registration.Machine, StringComparer.Ordinal).ThenBy(value => value.Registration.CurrentPath, StringComparer.Ordinal).ToArray();
    }
}
