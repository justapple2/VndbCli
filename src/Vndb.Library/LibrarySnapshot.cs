using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;

namespace Vndb.Library;

// Maintenance artifacts are private, local files. They are never HTTP DTOs.
public sealed record SnapshotFile(long Bytes, string Sha256);
public sealed record SnapshotCatalog(int Novels, int Producers, int Releases, int NovelReleases,
    int CompletedDatasets, int ActiveDatasets, DateTimeOffset? CompletedSnapshotAt);
public sealed record SnapshotManifest(string Format, int Schema, string ImageId, string ServiceInstanceId,
    string? Owner, string? KeyFingerprint, string MountRequirement, IReadOnlyDictionary<string, SnapshotFile> Files,
    SnapshotCatalog? Catalog = null, bool DatasetInputsIncluded = false);

public static class LibrarySnapshot
{
    private const string ManifestName = "snapshot-manifest.json";
    private const string IdentityName = "service-instance-id";

    public static SnapshotManifest Create(string dataDirectory, string destination, string imageId,
        string? owner = null, string? keyFile = null, CancellationToken cancellationToken = default, bool includeDatasetInputs = false)
    {
        if (string.IsNullOrWhiteSpace(imageId) || imageId.Length > 256 || imageId.Any(char.IsControl))
            throw new ArgumentException("需要记录可回退的原镜像ID。");
        if (owner is not null && !System.Text.RegularExpressions.Regex.IsMatch(owner, @"^\d{1,10}:\d{1,10}$"))
            throw new ArgumentException("属主须为数字UID:GID；在原数据库属主身份下运行。");
        var source = Ordinary(dataDirectory);
        var identityPath = Ordinary(Path.Combine(source, IdentityName));
        var identity = File.ReadAllBytes(identityPath);
        var instance = Encoding.UTF8.GetString(identity).Trim();
        if (!Guid.TryParseExact(instance, "D", out _)) throw new InvalidDataException("缺少有效服务实例身份，不能生成替代身份。");
        var stage = Stage(destination, source, allowChild: true);
        var database = Path.Combine(stage, "library.db");
        cancellationToken.ThrowIfCancellationRequested();
        using (var input = Open(Ordinary(Path.Combine(source, "library.db"))))
        using (var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            output.Open();
            input.BackupDatabase(output);
            using var journal = output.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode=DELETE";
            journal.ExecuteScalar(); // Only the new snapshot, never the live WAL/SHM.
        }
        var databaseSchema = ValidateDatabase(database);
        if (includeDatasetInputs)
            foreach (var name in DatasetInputs(database, source, cancellationToken))
            { cancellationToken.ThrowIfCancellationRequested(); Copy(Path.Combine(source, name), Path.Combine(stage, name)); }
        foreach (var asset in Assets(database))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = Ordinary(LibraryRules.CoverPath(source, asset));
            var proof = Proof(original);
            if (proof.Bytes != asset.SizeBytes || !proof.Sha256.Equals(asset.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("封面内容与数据库散列不一致，未完成备份。");
            Copy(original, LibraryRules.CoverPath(stage, asset));
        }
        File.WriteAllBytes(Path.Combine(stage, IdentityName), identity);
        if (!File.ReadAllBytes(identityPath).AsSpan().SequenceEqual(identity))
            throw new InvalidDataException("备份期间服务实例身份发生变化。");
        var content = Members(stage).ToDictionary(p => Path.GetRelativePath(stage, p).Replace('\\', '/'), Proof);
        var manifest = new SnapshotManifest("vn-library-snapshot-v2", databaseSchema, imageId, instance, owner,
            KeyFingerprint(keyFile), "Mount restored data read/write as /data under the original database UID:GID; supply the original key separately as a read-only secret.", content,
            Catalog(database), includeDatasetInputs);
        using (var file = new FileStream(Path.Combine(stage, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, manifest, VndbJson.Options);
            file.Flush(true);
        }
        Verify(stage, keyFile);
        Commit(stage, destination);
        return manifest;
    }

    public static SnapshotManifest Restore(string snapshotDirectory, string destination, string? keyFile = null,
        CancellationToken cancellationToken = default)
    {
        var source = Ordinary(snapshotDirectory);
        var manifest = Verify(source, keyFile);
        var stage = Stage(destination, source);
        foreach (var name in manifest.Files.Keys.Append(ManifestName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Copy(Path.Combine(source, name), Path.Combine(stage, name));
        }
        Verify(stage, keyFile);
        Commit(stage, destination);
        return manifest;
    }

    public static SnapshotManifest Verify(string snapshotDirectory, string? keyFile = null)
    {
        var root = Ordinary(snapshotDirectory);
        var manifestPath = Ordinary(Path.Combine(root, ManifestName));
        if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024) throw new InvalidDataException("恢复清单过大。");
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllBytes(manifestPath), VndbJson.Options)
            ?? throw new InvalidDataException("恢复清单为空。");
        if (manifest.Format is not ("vn-library-snapshot-v1" or "vn-library-snapshot-v2") ||
            manifest.Format == "vn-library-snapshot-v1" && (manifest.Catalog is not null || manifest.DatasetInputsIncluded) ||
            manifest.Format == "vn-library-snapshot-v2" && manifest.Catalog is null ||
            (manifest.Schema < 10 || manifest.Schema > SqliteLibraryStore.SchemaVersion) || manifest.Files is null ||
            !Guid.TryParseExact(manifest.ServiceInstanceId, "D", out _) || string.IsNullOrWhiteSpace(manifest.ImageId))
            throw new InvalidDataException("不支持的恢复清单。");
        if (manifest.KeyFingerprint is not null && manifest.KeyFingerprint != KeyFingerprint(keyFile))
            throw new InvalidDataException("恢复须独立提供原管理密钥，指纹不匹配或未提供。");
        var members = Members(root).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!members.SetEquals(manifest.Files.Keys.Append(ManifestName)) ||
            !manifest.Files.ContainsKey("library.db") || !manifest.Files.ContainsKey(IdentityName))
            throw new InvalidDataException("恢复文件集合不完整或包含额外文件。");
        foreach (var (name, proof) in manifest.Files)
        {
            if (name != "library.db" && name != IdentityName && !ValidCoverName(name) && !(manifest.DatasetInputsIncluded && ValidDatasetName(name)))
                throw new InvalidDataException("恢复清单含有非许可文件。");
            if (Proof(Ordinary(Path.Combine(root, name))) != proof) throw new InvalidDataException("恢复文件散列不一致。");
        }
        if (File.ReadAllText(Path.Combine(root, IdentityName)).Trim() != manifest.ServiceInstanceId)
            throw new InvalidDataException("恢复实例身份不一致。");
        var database = Path.Combine(root, "library.db");
        if (ValidateDatabase(database) != manifest.Schema) throw new InvalidDataException("数据库版本与清单不一致。");
        if (manifest.Catalog is { } catalog && catalog != Catalog(database)) throw new InvalidDataException("公共目录数量或任务覆盖与恢复清单不一致。");
        var expected = new HashSet<string>(StringComparer.Ordinal) { "library.db", IdentityName };
        if (manifest.DatasetInputsIncluded) expected.UnionWith(DatasetInputs(database, root, default));
        foreach (var asset in Assets(database))
        {
            var name = asset.RelativePath.Replace('\\', '/');
            if (!ValidCoverName(name) || !manifest.Files.TryGetValue(name, out var proof) ||
                proof.Bytes != asset.SizeBytes || !proof.Sha256.Equals(asset.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("数据库封面与恢复清单不一致。");
            expected.Add(name);
        }
        if (!expected.SetEquals(manifest.Files.Keys)) throw new InvalidDataException("恢复清单包含未登记的封面。");
        return manifest;
    }

    private static SnapshotCatalog Catalog(string database)
    {
        using var db = Open(database);
        int Count(string table)
        {
            using var exists = db.CreateCommand(); exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name"; exists.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return 0;
            using var count = db.CreateCommand(); count.CommandText = "SELECT count(*) FROM " + table; return Convert.ToInt32(count.ExecuteScalar());
        }
        var novels = Count("novels"); var producers = Count("producers"); var releases = Count("releases"); var relations = Count("novel_releases");
        var jobs = DatasetWorks(db).Select(w => w.Job).ToArray();
        return new(novels, producers, releases, relations, jobs.Count(j => j.State == "completed"),
            jobs.Count(j => j.State is not ("completed" or "cancelled")), jobs.Where(j => j.State == "completed").Max(j => j.SnapshotAt));
    }
    private static List<DatasetWork> DatasetWorks(SqliteConnection db)
    {
        using var exists = db.CreateCommand(); exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='dataset_jobs'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return [];
        using var select = db.CreateCommand(); select.CommandText = "SELECT record_json FROM dataset_jobs";
        using var reader = select.ExecuteReader(); var works = new List<DatasetWork>();
        while (reader.Read()) works.Add(JsonSerializer.Deserialize<DatasetWork>(reader.GetString(0), VndbJson.Options) ?? throw new InvalidDataException("导入任务记录无效。"));
        return works;
    }
    private static IReadOnlyList<string> DatasetInputs(string database, string root, CancellationToken ct)
    {
        using var db = Open(database); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var work in DatasetWorks(db).Where(w => w.Job.State is not ("completed" or "cancelled")))
        {
            ct.ThrowIfCancellationRequested();
            if (work.SourceFile is { } file)
            {
                var name = "datasets/" + file;
                if (!System.Text.RegularExpressions.Regex.IsMatch(file, @"^[a-f0-9]{32}\.zst$") || !ValidDatasetName(name) ||
                    Proof(Ordinary(Path.Combine(root, name))).Sha256 != work.Job.Sha256) throw new InvalidDataException("导入原包缺失或不匹配，不能声明可续跑快照。");
                names.Add(name);
            }
            if (work.Job.Phase is not ("producer" or "vn")) continue; // Interrupted preparation restarts from the recorded raw package.
            if (work.SourceFile is null || work.Stage is not { } stageName || !System.Text.RegularExpressions.Regex.IsMatch(stageName, @"^stage-[a-f0-9]{32}$"))
                throw new InvalidDataException("导入缺少明确恢复输入。");
            var prefix = "datasets/" + stageName + "/"; var stage = Ordinary(Path.Combine(root, "datasets", stageName));
            if (Proof(Path.Combine(stage, "manifest.json")).Sha256 != work.ManifestSha) throw new InvalidDataException("导入暂存清单与断点不匹配。");
            var manifest = DumpPackage.Verify(stage, ct);
            if (!manifest.Normalized || manifest.Sha256 != work.Job.Sha256) throw new InvalidDataException("暂存尚未验证，不能用于续跑恢复。");
            foreach (var member in manifest.Files.Keys.Append("manifest.json")) names.Add(prefix + member);
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    private static bool ValidDatasetName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^datasets/[a-f0-9]{32}\.zst$") ||
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^datasets/stage-[a-f0-9]{32}/(?:manifest\.json|normalized-proofs\.json|stage\.db|TIMESTAMP|README\.txt|LICENSE-ODBL\.txt|LICENSE-DBCL\.txt|schema_plain\.sql|db/[a-z_]+(?:\.header)?)$");

    private static bool ValidCoverName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, @"^covers/[a-fA-F0-9]{64}\.(jpg|png|webp)$");
    private static string? KeyFingerprint(string? path) => path is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Ordinary(path)).Trim())));
    private static SnapshotFile Proof(string path)
    {
        using var input = File.OpenRead(Ordinary(path));
        return new(input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
    }
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static int ValidateDatabase(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var schema = Convert.ToInt32(command.ExecuteScalar());
        if ((schema < 10 || schema > SqliteLibraryStore.SchemaVersion)) throw new InvalidDataException("恢复仅支持schema10–15，不能自动升级或降级。");
        command.CommandText = "PRAGMA integrity_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("数据库完整性检查失败。");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("数据库外键检查失败。");
        return schema;
    }
    private static List<CoverAsset> Assets(string database)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash,relative_path,content_type,size_bytes,source_url,downloaded_at FROM assets";
        using var reader = command.ExecuteReader();
        var assets = new List<CoverAsset>();
        while (reader.Read()) assets.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))));
        return assets;
    }
    private static string Ordinary(string path)
    {
        var full = Path.GetFullPath(path);
        for (string? node = full; node is not null; node = Path.GetDirectoryName(node))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(node); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("备份／恢复拒绝重解析目录或文件。");
        }
        return full;
    }
    private static IEnumerable<string> Members(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(Ordinary(directory)))
        {
            Ordinary(entry);
            if (Directory.Exists(entry)) { foreach (var file in Members(entry)) yield return file; }
            else yield return entry;
        }
    }
    private static string Stage(string destination, string source, bool allowChild = false)
    {
        var target = Ordinary(destination);
        if (File.Exists(target) || Directory.Exists(target) || Path.GetFullPath(source).StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            (!allowChild && target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || target.Equals(source, StringComparison.OrdinalIgnoreCase))
            throw new IOException("目标必须是全新、独立的目录。");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Reservation is retained with the private evidence, including on interruption.
        using (new FileStream(target + ".snapshot-intent", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var stage = target + ".staging-" + Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stage;
    }
    private static void Copy(string source, string destination)
    {
        Ordinary(source); Ordinary(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = File.OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }
    private static void Commit(string stage, string destination)
    {
        var target = Ordinary(destination);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("目标已出现，保留暂存证据。");
        Directory.Move(stage, target);
    }
}
