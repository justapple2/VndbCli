using System.Globalization;
using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

/// <summary>Isolated-stage projection. Missing dump fields stay unknown; no API calls or asset downloads.</summary>
public sealed class DumpProjection(SqliteConnection stage, DumpManifest manifest)
{
    private List<Dictionary<string, string?>> Rows(string sql, string? id = null)
    {
        using var command = stage.CreateCommand(); command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); var result = new List<Dictionary<string, string?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var n = 0; n < reader.FieldCount; n++) row.Add(reader.GetName(n), reader.IsDBNull(n) ? null : reader.GetString(n));
            result.Add(row);
        }
        return result;
    }
    public IReadOnlyList<string> Ids(string after = "", int limit = 25)
        => EntityIds("vn", after, limit);
    public IReadOnlyList<string> EntityIds(string kind, string after = "", int limit = 25)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("批次大小1–100。");
        if (kind is not ("vn" or "producer")) throw new ArgumentException("未知公共实体。");
        return Rows($"SELECT id FROM {(kind == "vn" ? "vn" : "producers")} WHERE id > $id ORDER BY id LIMIT {limit}", after).Select(r => r["id"]!).ToArray();
    }
    public Producer Producer(string id)
    {
        if (VndbId.Normalize(id, 'p') != id) throw new InvalidDataException("会社ID无效。");
        var producer = Producer(Rows("SELECT * FROM producers WHERE id=$id", id).Single());
        if (string.IsNullOrWhiteSpace(producer.Name) || producer.Name.Length > 500) throw new InvalidDataException("会社名称无效。");
        return producer;
    }
    private static string[] Lines(string? value) => (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool Boolean(string? value) => value switch { "t" => true, "f" => false, _ => throw new InvalidDataException("COPY布尔值无效。") };
    private static Producer Producer(Dictionary<string, string?> p) => new()
    {
        Id = p["id"]!, Name = p["latin"] ?? p["name"]!, Original = p["latin"] is null ? null : p["name"],
        Type = p["type"], Lang = p["lang"], Aliases = Lines(p["alias"]), Description = p["description"]
    };
    public NovelRelease Release(string id)
    {
        var r = Rows("SELECT * FROM releases WHERE id=$id", id).Single();
        var titles = Rows("SELECT * FROM releases_titles WHERE id=$id ORDER BY lang", id);
        var main = titles.Single(t => t["lang"] == r["olang"]);
        return new()
        {
            Id = id, Title = main["latin"] ?? main["title"]!, AltTitle = main["latin"] is null ? null : main["title"],
            Released = ReleaseDate(r["released"]), Official = Boolean(r["official"]), Patch = Boolean(r["patch"]),
            Languages = titles.Select(t => new ReleaseLanguage(t["lang"]!, t["title"], t["latin"], Boolean(t["mtl"]), t["lang"] == r["olang"])).ToArray(),
            Platforms = Rows("SELECT platform FROM releases_platforms WHERE id=$id ORDER BY platform", id).Select(p => p["platform"]!).ToArray(),
            Vns = Rows("SELECT vid,rtype FROM releases_vn WHERE id=$id ORDER BY vid", id).Select(v => new ReleaseNovel(v["vid"]!, v["rtype"]!)).ToArray(),
            Producers = Rows("SELECT p.*,rp.developer,rp.publisher FROM releases_producers rp JOIN producers p ON p.id=rp.pid WHERE rp.id=$id ORDER BY p.id", id)
                .Select(p => new ReleaseProducer(p["id"]!, p["latin"] ?? p["name"]!, p["latin"] is null ? null : p["name"], Boolean(p["developer"]), Boolean(p["publisher"]))).ToArray()
        };
    }
    public LibraryEntry Novel(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (VndbId.Normalize(id, 'v') != id) throw new InvalidDataException("作品ID无效。");
        var v = Rows("SELECT * FROM vn WHERE id=$id", id).Single();
        var titles = Rows("SELECT * FROM vn_titles WHERE id=$id ORDER BY lang", id).Select(t => new NovelTitle
        { Lang = t["lang"]!, Title = t["title"]!, Latin = t["latin"], Official = Boolean(t["official"]), Main = t["lang"] == v["olang"] }).ToArray();
        var main = titles.Single(t => t.Main);
        var producers = Rows("SELECT DISTINCT p.* FROM producers p JOIN releases_producers rp ON rp.pid=p.id JOIN releases_vn rv ON rv.id=rp.id WHERE rv.vid=$id AND rp.developer='t' ORDER BY p.id", id).Select(Producer).ToArray();
        var releases = new List<NovelRelease>();
        foreach (var r in Rows("SELECT id FROM releases_vn WHERE vid=$id ORDER BY id", id)) { ct.ThrowIfCancellationRequested(); releases.Add(Release(r["id"]!)); }
        var catalog = new ReleaseCatalog(releases, manifest.SnapshotAt); ReleaseRules.Validate(catalog, id);
        var dates = releases.Select(r => r.Released).Where(d => d is not null && d != "TBA").Order(StringComparer.Ordinal).ToArray();
        var novel = new VisualNovel
        {
            Id = id, Title = main.Latin ?? main.Title, AltTitle = main.Latin is null ? null : main.Title, OriginalLanguage = v["olang"], Titles = titles,
            Aliases = Lines(v["alias"]), Description = v["description"], Developers = producers, ReleaseCatalog = catalog,
            Released = dates.FirstOrDefault() ?? (releases.Any(r => r.Released == "TBA") ? "TBA" : null),
            Languages = releases.SelectMany(r => r.Languages).Where(l => !l.Mtl).Select(l => l.Lang).Distinct().Order(StringComparer.Ordinal).ToArray(),
            Platforms = releases.SelectMany(r => r.Platforms).Distinct().Order(StringComparer.Ordinal).ToArray(),
            Rating = int.TryParse(v["c_rating"], out var rating) && rating > 0 ? rating / 10d : null,
            VoteCount = int.TryParse(v["c_votecount"], out var votes) ? votes : null,
            LengthMinutes = int.TryParse(v["c_length"], out var length) && length > 0 ? length : null,
            Relations = Rows("SELECT vid,relation,official FROM vn_relations WHERE id=$id ORDER BY vid", id)
                .Select(r => new NovelRelation(r["vid"]!, r["relation"]!, Boolean(r["official"]))).ToArray()
        };
        // Image/rating derivations not faithfully represented by selected raw tables are not invented.
        var needsCover = v["c_image"] is not null || v["image"] is not null;
        return new(novel, producers, null, needsCover ? "failed" : "none", needsCover ? "导出包没有封面缓存，可按需联网补全。" : null,
            manifest.SnapshotAt, manifest.SnapshotAt, "vndb_dump", "partial_text");
    }
    public static string? ReleaseDate(string? raw)
    {
        if (raw is null or "0") return null;
        if (raw == "99999999") return "TBA";
        if (raw.Length != 8 || !raw.All(char.IsAsciiDigit)) throw new InvalidDataException("COPY日期无效。");
        if (raw[4..] == "9999") return raw[..4];
        if (raw[6..] == "99") return raw[..4] + "-" + raw.Substring(4, 2);
        return DateOnly.ParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
