using System.Formats.Tar;
using System.Text;
using Vndb.Library;
using Xunit;
using ZstdSharp;

namespace Vndb.Tests;

public sealed class DumpPackageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static string Archive(string root, Action<Dictionary<string, string>>? change = null)
    {
        var files = new Dictionary<string, string>
        {
            ["TIMESTAMP"] = "2026-10-27 08:00:27+00\n", ["README.txt"] = "Open Database License and Database Contents License",
            ["LICENSE-ODBL.txt"] = "synthetic ODbL fixture", ["LICENSE-DBCL.txt"] = "synthetic DbCL fixture", ["schema_plain.sql"] = "NEVER EXECUTE SQL"
        };
        foreach (var (table, header) in DumpPackage.Headers) { files["db/" + table + ".header"] = header + "\n"; files["db/" + table] = ""; }
        void Row(string table, params (string Key, string Value)[] values)
        {
            var fields = values.ToDictionary(v => v.Key, v => v.Value);
            files["db/" + table] += string.Join('\t', DumpPackage.Headers[table].Split('\t').Select(c => fields.GetValueOrDefault(c, "\\N"))) + "\n";
        }
        Row("producers", ("id", "p1"), ("name", "合成会社"), ("latin", "Synthetic"), ("type", "co"), ("lang", "ja"), ("alias", ""));
        Row("vn", ("id", "v17"), ("olang", "ja"), ("description", "合成\\n説明"), ("alias", "Kana"));
        Row("vn_titles", ("id", "v17"), ("lang", "ja"), ("official", "t"), ("title", "合成加奈"), ("latin", "Kana"));
        Row("releases", ("id", "r1"), ("olang", "ja"), ("released", "20269999"), ("official", "t"), ("patch", "f"));
        Row("releases_titles", ("id", "r1"), ("lang", "ja"), ("mtl", "f"), ("title", "合成加奈"));
        Row("releases_platforms", ("id", "r1"), ("platform", "win"));
        Row("releases_producers", ("id", "r1"), ("pid", "p1"), ("developer", "t"), ("publisher", "t"));
        Row("releases_vn", ("id", "r1"), ("vid", "v17"), ("rtype", "complete"));
        files["db/ulist_vns"] = "synthetic-other-user-data-never-import";
        change?.Invoke(files);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tar.zst");
        using var file = File.Create(path); using var zstd = new CompressionStream(file); using var tar = new TarWriter(zstd);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "db/"));
        foreach (var (name, data) in files)
        { using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(data)); tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = bytes }); }
        return path;
    }
    [Fact]
    public void SelectedCopyTablesHaveNoUserRowsAndPreserveJapaneseDatesAndDevelopers()
    {
        using var dir = new TestDirectory(); var archive = Archive(dir.Path); var stage = Path.Combine(dir.Path, "stage");
        var manifest = DumpPackage.Prepare(archive, stage, Ct);
        Assert.Equal(1, manifest.Rows["vn"]); Assert.False(File.Exists(Path.Combine(stage, "db", "ulist_vns")));
        Assert.Equal(manifest.Sha256, DumpPackage.Verify(stage, Ct).Sha256);
        using var db = DumpPackage.OpenStage(stage); var projected = new DumpProjection(db, manifest).Novel("v17", Ct);
        Assert.Equal("合成加奈", projected.Novel.Titles[0].Title); Assert.Equal("2026", projected.Novel.Released);
        Assert.Contains("\n", projected.Novel.Description); Assert.Equal("p1", projected.Novel.Developers[0].Id);
        Assert.Equal("vndb_dump", projected.Provenance); Assert.Null(projected.Novel.Image);
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("/escape")]
    public void UnsafeMembersNeverReachAReadyStage(string member)
    {
        using var dir = new TestDirectory(); var archive = Archive(dir.Path, f => f[member] = "no");
        var stage = Path.Combine(dir.Path, "stage");
        Assert.Throws<InvalidDataException>(() => DumpPackage.Prepare(archive, stage, Ct));
        Assert.False(File.Exists(Path.Combine(stage, "manifest.json")));
    }
    [Fact]
    public void HeaderChangesAndModifiedStagingFailClosed()
    {
        using var dir = new TestDirectory(); var bad = Archive(dir.Path, f => f["db/vn.header"] = "id\tunexpected");
        Assert.Throws<InvalidDataException>(() => DumpPackage.Prepare(bad, Path.Combine(dir.Path, "bad"), Ct));
        var stage = Path.Combine(dir.Path, "valid"); DumpPackage.Prepare(Archive(dir.Path), stage, Ct);
        File.AppendAllText(Path.Combine(stage, "stage.db"), "changed");
        Assert.Throws<InvalidDataException>(() => DumpPackage.Verify(stage, Ct));
    }
    [Fact]
    public void CopyEscapesDistinguishNullAndPreserveUtf8Bytes()
    {
        Assert.Null(DumpPackage.Decode("\\N")); Assert.Equal("\\N", DumpPackage.Decode("\\\\N"));
        Assert.Equal("日\n本\t語", DumpPackage.Decode("日\\n本\\t語")); Assert.Equal("é", DumpPackage.Decode("\\303\\251"));
        Assert.Throws<InvalidDataException>(() => DumpPackage.Decode("bad\\"));
    }
}
