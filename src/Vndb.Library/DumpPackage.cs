using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Vndb.Library;

public sealed record DumpManifest(string Format, string Sha256, DateTimeOffset SnapshotAt, long DownloadBytes,
    long ExpandedBytes, long SelectedBytes, IReadOnlyDictionary<string, long> Rows, IReadOnlyDictionary<string, string> Files, bool Normalized = false);

/// <summary>Selected public COPY tables only. Never executes the supplied SQL or loads user tables.</summary>
public static class DumpPackage
{
    public static IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["producers"] = "id\ttype\tlang\tname\tlatin\talias\tdescription",
        ["vn"] = "id\timage\tc_image\tolang\tc_votecount\tc_rating\tc_average\tc_length\tc_lengthnum\tlength\tdevstatus\talias\tdescription",
        ["vn_titles"] = "id\tlang\tofficial\ttitle\tlatin",
        ["releases"] = "id\tgtin\tolang\treleased\tvoiced\treso_x\treso_y\tminage\tani_story\tani_ero\tani_story_sp\tani_story_cg\tani_cutscene\tani_ero_sp\tani_ero_cg\tani_bg\tani_face\thas_ero\tpatch\tfreeware\tuncensored\tofficial\tcatalog\tnotes\tengine",
        ["releases_titles"] = "id\tlang\tmtl\ttitle\tlatin",
        ["releases_platforms"] = "id\tplatform",
        ["releases_producers"] = "id\tpid\tdeveloper\tpublisher",
        ["releases_vn"] = "id\tvid\trtype",
        ["vn_relations"] = "id\tvid\trelation\tofficial"
    };
    public const long MaximumCompressedBytes = 512L * 1024 * 1024;
    public const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    public static void Ordinary(string path)
    {
        for (var node = Path.GetFullPath(path); node is not null; node = Path.GetDirectoryName(node))
            if (Path.Exists(node) && (File.GetAttributes(node) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("资料包暂存拒绝链接和重解析路径。");
    }
    public static string Hash(string path)
    { Ordinary(path); using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }

    public static DumpManifest Prepare(string archive, string destination, CancellationToken ct = default)
    {
        Ordinary(archive); Ordinary(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("请使用新的隔离暂存目录；不覆盖原件。");
        var size = new FileInfo(archive).Length;
        if (size <= 0 || size > MaximumCompressedBytes) throw new InvalidDataException("压缩资料包大小无效。");
        EnsureSpace(destination, 1024L * 1024 * 1024);
        var hash = Hash(archive); Directory.CreateDirectory(destination);
        var selected = Headers.Keys.SelectMany(n => new[] { "db/" + n, "db/" + n + ".header" })
            .Concat(["TIMESTAMP", "README.txt", "LICENSE-ODBL.txt", "LICENSE-DBCL.txt", "schema_plain.sql"]).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); long expanded = 0, kept = 0;
        using (var file = File.OpenRead(archive))
        using (var zstd = new DecompressionStream(file))
        using (var tar = new TarReader(zstd))
        {
            TarEntry? member; var count = 0;
            while ((member = tar.GetNextEntry()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                var name = member.EntryType == TarEntryType.Directory ? member.Name.TrimEnd('/') : member.Name;
                if (++count > 512 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p => p is "." or ".." or "") ||
                    !seen.Add(name) || member.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                    throw new InvalidDataException("资料包成员名称、类型或数量无效。");
                if (member.EntryType == TarEntryType.Directory) { if (name != "db") throw new InvalidDataException("非预期目录。"); continue; }
                expanded = checked(expanded + member.Length);
                if (member.Length < 0 || expanded > MaximumExpandedBytes) throw new InvalidDataException("资料包展开超过限额。");
                if (!selected.Contains(name)) continue;
                if (member.Length > 256L * 1024 * 1024) throw new InvalidDataException("所选数据表超过限额。");
                kept += member.Length;
                var target = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                if (member.Length > 0) (member.DataStream ?? throw new InvalidDataException("资料包内容缺失。")).CopyTo(output);
                ct.ThrowIfCancellationRequested();
            }
        }
        if (selected.Any(n => !File.Exists(Path.Combine(destination, n)))) throw new InvalidDataException("缺少必需公共表或许可文件。");
        foreach (var (table, header) in Headers)
            if (File.ReadAllText(Path.Combine(destination, "db", table + ".header")).TrimEnd('\n', '\r') != header)
                throw new InvalidDataException("资料表结构变化，拒绝导入。");
        var stamp = DateTimeOffset.Parse(File.ReadAllText(Path.Combine(destination, "TIMESTAMP")).Trim(), CultureInfo.InvariantCulture);
        var readme = File.ReadAllText(Path.Combine(destination, "README.txt"));
        if (!readme.Contains("Open Database License", StringComparison.Ordinal) || !readme.Contains("Database Contents License", StringComparison.Ordinal))
            throw new InvalidDataException("导出许可声明无法识别，请先核对。");
        var rows = LoadTables(destination, ct);
        var files = selected.Append("stage.db").ToDictionary(n => n, n => Hash(Path.Combine(destination, n)), StringComparer.Ordinal);
        var manifest = new DumpManifest("vndb-public-stage-v1", hash, stamp, size, expanded, kept, rows, files);
        File.WriteAllBytes(Path.Combine(destination, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Vndb.Core.VndbJson.Options));
        return manifest;
    }
    public static void EnsureSpace(string path, long minimum)
    {
        var full = Path.GetFullPath(path);
        var drive = DriveInfo.GetDrives().Where(d =>
        {
            var name = Path.TrimEndingDirectorySeparator(d.Name);
            return full.Equals(name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                full.StartsWith(Path.EndsInDirectorySeparator(name) ? name : name + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }).OrderByDescending(d => d.Name.Length).FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.AvailableFreeSpace < minimum) throw new IOException("暂存/数据库空间不足，保留已有资料。");
    }
    public static SqliteConnection OpenStage(string directory)
    {
        Ordinary(directory);
        if (new FileInfo(Path.Combine(directory, "manifest.json")).Length > 1024 * 1024) throw new InvalidDataException("暂存清单过大。");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "stage.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    public static DumpManifest Verify(string directory, CancellationToken ct = default)
    {
        Ordinary(directory);
        if (new FileInfo(Path.Combine(directory, "manifest.json")).Length > 1024 * 1024) throw new InvalidDataException("暂存清单过大。");
        var manifest = JsonSerializer.Deserialize<DumpManifest>(File.ReadAllBytes(Path.Combine(directory, "manifest.json")), Vndb.Core.VndbJson.Options)
            ?? throw new InvalidDataException("暂存清单无效。");
        if (manifest.Format != "vndb-public-stage-v1" || manifest.Files is null || manifest.Rows is null ||
            manifest.Sha256 is not { Length: 64 } || manifest.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
            !manifest.Files.Keys.ToHashSet().SetEquals(Headers.Keys.SelectMany(n => new[] { "db/" + n, "db/" + n + ".header" }).Concat(["TIMESTAMP", "README.txt", "LICENSE-ODBL.txt", "LICENSE-DBCL.txt", "schema_plain.sql", "stage.db"]).Concat(manifest.Normalized ? ["normalized-proofs.json"] : Array.Empty<string>())))
            throw new InvalidDataException("暂存文件集合无效。");
        foreach (var (name, hash) in manifest.Files) { ct.ThrowIfCancellationRequested(); if (Hash(Path.Combine(directory, name)) != hash) throw new InvalidDataException("暂存文件散列变化。"); }
        return manifest;
    }
    private static IReadOnlyDictionary<string, long> LoadTables(string directory, CancellationToken ct)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "stage.db"), Pooling = false }.ToString()); db.Open();
        using (var setup = db.CreateCommand()) { setup.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA cache_size=-16384; PRAGMA temp_store=FILE"; setup.ExecuteNonQuery(); }
        var counts = new Dictionary<string, long>();
        foreach (var (name, header) in Headers)
        {
            var columns = header.Split('\t');
            using var create = db.CreateCommand(); create.CommandText = $"CREATE TABLE {name} ({string.Join(',', columns.Select(c => '\"' + c + "\" TEXT"))}); CREATE INDEX ix_{name}_id ON {name}(id)"; create.ExecuteNonQuery();
            using var tx = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO {name} VALUES({string.Join(',', columns.Select((_, n) => "$p" + n))})";
            foreach (var (c, n) in columns.Select((c, n) => (c, n))) insert.Parameters.Add("$p" + n, SqliteType.Text);
            insert.Prepare(); long count = 0;
            using var reader = new StreamReader(Path.Combine(directory, "db", name), new UTF8Encoding(false, true));
            string? line;
            while ((line = ReadLine(reader)) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (line.Length > 1024 * 1024) throw new InvalidDataException("COPY行过大。");
                var cells = line.Split('\t'); if (cells.Length != columns.Length) throw new InvalidDataException("COPY列数量无效。");
                for (var n = 0; n < cells.Length; n++) insert.Parameters[n].Value = (object?)Decode(cells[n]) ?? DBNull.Value;
                insert.ExecuteNonQuery(); count++;
            }
            tx.Commit(); counts[name] = count;
        }
        using var validate = db.CreateCommand(); validate.CommandText = """
            CREATE INDEX ix_rvn_vid ON releases_vn(vid,id);
            CREATE UNIQUE INDEX ux_vn ON vn(id); CREATE UNIQUE INDEX ux_producers ON producers(id); CREATE UNIQUE INDEX ux_releases ON releases(id);
            CREATE UNIQUE INDEX ux_vnt ON vn_titles(id,lang); CREATE UNIQUE INDEX ux_rt ON releases_titles(id,lang);
            SELECT (SELECT count(*) FROM releases_vn rv LEFT JOIN vn v ON v.id=rv.vid LEFT JOIN releases r ON r.id=rv.id WHERE v.id IS NULL OR r.id IS NULL)
              + (SELECT count(*) FROM releases_producers rp LEFT JOIN producers p ON p.id=rp.pid LEFT JOIN releases r ON r.id=rp.id WHERE p.id IS NULL OR r.id IS NULL)
              + (SELECT count(*) FROM vn v WHERE NOT EXISTS(SELECT 1 FROM vn_titles t WHERE t.id=v.id AND t.lang=v.olang));
            """;
        if (Convert.ToInt64(validate.ExecuteScalar()) != 0) throw new InvalidDataException("公共资料关联不完整。");
        return counts;
    }
    private static string? ReadLine(StreamReader reader)
    {
        var line = new StringBuilder(); int value;
        while ((value = reader.Read()) >= 0)
        {
            if (value == '\n') return line.ToString();
            if (line.Length >= 1024 * 1024) throw new InvalidDataException("COPY行过大。");
            line.Append((char)value);
        }
        return line.Length == 0 ? null : line.ToString();
    }
    public static string? Decode(string raw)
    {
        if (raw == "\\N") return null;
        var source = Encoding.UTF8.GetBytes(raw); using var value = new MemoryStream(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] != '\\') { value.WriteByte(source[i]); continue; }
            if (++i == source.Length) throw new InvalidDataException("COPY转义被截断。");
            var c = (char)source[i];
            if (c is >= '0' and <= '7')
            { var number = c - '0'; for (var n = 0; n < 2 && i + 1 < source.Length && source[i + 1] is >= (byte)'0' and <= (byte)'7'; n++) number = number * 8 + source[++i] - '0'; if (number > 255) throw new InvalidDataException("COPY字节越界。"); value.WriteByte((byte)number); }
            else if (c == 'x')
            { var number = 0; var digits = 0; while (digits < 2 && i + 1 < source.Length && char.IsAsciiHexDigit((char)source[i + 1])) { number = number * 16 + int.Parse(((char)source[++i]).ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture); digits++; } if (digits == 0) throw new InvalidDataException("COPY十六进制转义无效。"); value.WriteByte((byte)number); }
            else value.WriteByte((byte)(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', '\\' => '\\', _ => throw new InvalidDataException("未知COPY转义。") }));
        }
        try { return new UTF8Encoding(false, true).GetString(value.ToArray()); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("COPY字节不是有效UTF8。", error); }
    }
}
