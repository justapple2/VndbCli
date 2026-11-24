using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

internal sealed record DatasetFileProof(string Name, bool Directory, long Length, long Created, long Modified, string? Sha256);
internal sealed record DatasetCleanupTarget(string Name, string Kind, string? JobId, IReadOnlyList<DatasetFileProof> Files);
internal sealed record DatasetCleanupPlan(string Id, IReadOnlyList<DatasetCleanupTarget> Targets);

public sealed partial class DatasetImports
{
    // The file lease also covers other service instances and is automatically released after a crash.
    private FileStream? TryFileLease()
    {
        DumpPackage.Ordinary(Root); Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "maintenance.lock"); DumpPackage.Ordinary(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    private async Task<FileStream> FileLeaseAsync(CancellationToken ct)
    {
        while (true) { ct.ThrowIfCancellationRequested(); if (TryFileLease() is { } lease) return lease; await Task.Delay(100, ct); }
    }
    private long FreeSpace(string path) => (availableSpace ?? DumpPackage.AvailableSpace)(path);
    private void RequireSpace(string path, long minimum)
    { if (FreeSpace(path) < minimum) throw new IOException("资料卷空间不足，未创建新的输入或暂存。"); }
    private void RemoveUnacceptedFile(string name)
    {
        // A failed/uncertain DB read must preserve the candidate for a later audit.
        if (store.DatasetWorks().Any(w => w.SourceFile == name)) return;
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
    }
    private void RemoveUnacceptedStage(string name)
    {
        if (store.DatasetWorks().Any(w => w.Stage == name)) return;
        var path = PathFor(name);
        if (Directory.Exists(path)) DeleteTree(path, Proof(path, CancellationToken.None, hash: false));
    }
    private static bool InputName(string name) => name.EndsWith(".zst", StringComparison.Ordinal) && Guid.TryParseExact(name[..^4], "N", out _);
    private static bool StageName(string name) => name.StartsWith("stage-", StringComparison.Ordinal) && Guid.TryParseExact(name[6..], "N", out _);
    private IEnumerable<string> Entries()
    { DumpPackage.Ordinary(Root); return Directory.Exists(Root) ? Directory.EnumerateFileSystemEntries(Root) : []; }

    public DatasetMaintenanceStatus MaintenanceStatus()
    {
        long input = 0, stages = 0; int inputs = 0, count = 0;
        foreach (var path in Entries())
        {
            try
            {
                if (InputName(Path.GetFileName(path))) { DumpPackage.Ordinary(path); input += new FileInfo(path).Length; inputs++; }
                else if (StageName(Path.GetFileName(path)) && Directory.Exists(path)) { stages += Measure(path); count++; }
            }
            catch (FileNotFoundException) { } // Worker may finish its own attempt while a passive read is in flight.
            catch (DirectoryNotFoundException) { }
        }
        var sync = SyncStatus();
        var wait = sync.ActiveJob is { } active ? $"现有任务：{active.State}／{active.Phase}；暂停或失败需手动处理。" :
            !sync.WorkerEnabled ? "后台执行未启用。" : sync.Checking ? "官方日期检查中。" : sync.Settings.Enabled ? "等待下次允许的定时检查。" : "定时同步关闭；可手动检查。";
        return new(FreeSpace(Root), input, stages, inputs, count, sync.CompletedSnapshotAt, sync.ActiveJob, wait);
    }
    private static long Measure(string path)
    {
        DumpPackage.Ordinary(path);
        if (File.Exists(path)) return new FileInfo(path).Length;
        long total = 0;
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) total = checked(total + Measure(child));
        return total;
    }

    public async Task<DatasetCleanupPreview> PreviewCleanupAsync(string key, CancellationToken ct)
    {
        RequireSyncKey(key);
        using var lease = await FileLeaseAsync(ct);
        // A stable request key identifies a durable preview. Replaying never changes its approval basis.
        var id = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var file = CleanupFile(id);
        if (File.Exists(file)) return PublicPreview(ReadCleanup(file));
        var works = store.DatasetWorks(); var targets = new List<DatasetCleanupTarget>();
        foreach (var path in Entries().Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); var name = Path.GetFileName(path);
            if (InputName(name) && !works.Any(w => w.SourceFile == name)) targets.Add(new(name, "unreferenced_input", null, Proof(path, ct)));
            else if (StageName(name) && Directory.Exists(path))
            {
                var references = works.Where(w => w.Stage == name).ToArray();
                if (references.Length == 0)
                {
                    if (KnownStageFiles(path)) targets.Add(new(name, "unreferenced_stage", null, Proof(path, ct)));
                }
                else if (references.Length == 1 && references[0].Job.State == "completed")
                {
                    var work = references[0];
                    if (!KnownStageFiles(path)) continue;
                    var manifest = DumpPackage.Verify(path, ct);
                    if (manifest.Normalized && manifest.Sha256 == work.Job.Sha256 && DumpPackage.Hash(Path.Combine(path, "manifest.json")) == work.ManifestSha)
                        targets.Add(new(name, "completed_stage", work.Job.Id, Proof(path, ct)));
                }
            }
        }
        var plan = new DatasetCleanupPlan(id, targets);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(output, plan, VndbJson.Options, ct); output.Flush(true); }
            File.Move(temp, file, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return PublicPreview(plan);
    }
    public async Task<DatasetCleanupResult> CleanupAsync(DatasetCleanupRequest request, string key, CancellationToken ct)
    {
        RequireSyncKey(key);
        using var lease = await FileLeaseAsync(ct);
        if (store.DatasetCleanupReceipt(request, key) is { } cached) return cached;
        var plan = ReadCleanup(CleanupFile(request.PreviewId));
        if (ArchiveFormat.Hash(plan) != request.Sha256) throw new PlayConflictException("清理预览证明已变化，请重新预览。");
        var intent = CleanupFile(plan.Id) + ".intent"; DumpPackage.Ordinary(intent);
        var continuing = File.Exists(intent);
        if (continuing && File.ReadAllText(intent) != request.Sha256) throw new PlayConflictException("清理恢复意图不符。");
        // Hash large files before opening a database write transaction. The lease prevents worker/upload changes.
        foreach (var target in plan.Targets)
        {
            ct.ThrowIfCancellationRequested();
            var path = PathFor(target.Name);
            if (Path.Exists(path) && !MatchesRemaining(Proof(path, ct), target.Files, continuing))
                throw new PlayConflictException("暂存文件内容或身份已变化，请重新预览；尚未清理。");
        }
        return store.CommitDatasetCleanup(request, key, works =>
        {
            if (works.Any(w => w.Job.State is not ("completed" or "cancelled"))) throw new PlayConflictException("仍有活动／暂停／失败任务，请先处理原任务再清理。");
            // Check every target before touching anything. Missing entries are already absent after a lost reply/crash.
            foreach (var target in plan.Targets)
            {
                ct.ThrowIfCancellationRequested(); RequireCleanupReference(target, works);
                var path = PathFor(target.Name);
                if (Path.Exists(path) && !MatchesRemaining(Proof(path, ct, hash: false), target.Files.Select(f => f with { Sha256 = null }).ToArray(), continuing))
                    throw new PlayConflictException("暂存文件内容或身份已变化，请重新预览；尚未清理。");
            }
            if (!continuing)
            {
                using var output = new FileStream(intent, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(System.Text.Encoding.UTF8.GetBytes(request.Sha256)); output.Flush(true);
            }
            int removed = 0; long released = 0;
            foreach (var target in plan.Targets)
            {
                var path = PathFor(target.Name);
                if (!Path.Exists(path)) continue;
                DeleteTree(path, target.Files, verifyHash: false); removed++; released += target.Files.Sum(f => f.Length);
            }
            return new(plan.Id, removed, released);
        });
    }
    private static void RequireCleanupReference(DatasetCleanupTarget target, IReadOnlyList<DatasetWork> works)
    {
        if (target.Kind == "completed_stage")
        {
            if (!StageName(target.Name) || !works.Any(w => w.Job.Id == target.JobId && w.Job.State == "completed" && w.Stage == target.Name) ||
                works.Any(w => w.Stage == target.Name && w.Job.Id != target.JobId)) throw new PlayConflictException("已完成暂存引用已变化。");
        }
        else if (target.Kind == "unreferenced_input" ? !InputName(target.Name) || works.Any(w => w.SourceFile == target.Name) :
                 target.Kind != "unreferenced_stage" || !StageName(target.Name) || works.Any(w => w.Stage == target.Name))
            throw new PlayConflictException("候选已被任务引用或不属于可清理暂存。");
    }
    private string CleanupFile(string id)
    {
        if (id is not { Length: 64 } || id.Any(c => !char.IsAsciiHexDigit(c))) throw new ArgumentException("无效清理预览。");
        var file = Path.Combine(store.DataDirectory, "dataset-cleanups", id + ".json"); DumpPackage.Ordinary(file); return file;
    }
    private static DatasetCleanupPlan ReadCleanup(string file)
    {
        if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new InvalidDataException("清理预览过大。");
        var plan = JsonSerializer.Deserialize<DatasetCleanupPlan>(File.ReadAllBytes(file), VndbJson.Options) ?? throw new InvalidDataException("清理预览损坏。");
        if (plan.Id != Path.GetFileNameWithoutExtension(file) || plan.Targets.Count > 1024 || plan.Targets.Any(t =>
            (!InputName(t.Name) && !StageName(t.Name)) || t.Files.Count > 1024 || t.Files.Any(f => f.Name != "." &&
                (Path.IsPathFullyQualified(f.Name) || f.Name.Split('/', '\\').Any(p => p is "" or "." or "..") || f.Name.Contains(':')))))
            throw new InvalidDataException("清理预览范围无效。");
        return plan;
    }
    private static DatasetCleanupPreview PublicPreview(DatasetCleanupPlan plan)
    {
        var entries = plan.Targets.Select((t, i) => new DatasetCleanupEntry(i.ToString(System.Globalization.CultureInfo.InvariantCulture), t.Kind, t.Files.Sum(f => f.Length), t.JobId)).ToArray();
        return new(plan.Id, ArchiveFormat.Hash(plan), entries, entries.Sum(e => e.Bytes));
    }
    private static IReadOnlyList<DatasetFileProof> Proof(string root, CancellationToken ct, bool hash = true)
    {
        var result = new List<DatasetFileProof>();
        void Walk(string path)
        {
            ct.ThrowIfCancellationRequested(); DumpPackage.Ordinary(path);
            if (result.Count >= 1024) throw new InvalidDataException("暂存文件数量超出清理范围。");
            if (File.Exists(path))
            {
                var f = new FileInfo(path);
                result.Add(new(Path.GetRelativePath(root, path), false, f.Length, f.CreationTimeUtc.Ticks, f.LastWriteTimeUtc.Ticks, hash ? DumpPackage.Hash(path) : null));
            }
            else
            {
                var d = new DirectoryInfo(path);
                result.Add(new(Path.GetRelativePath(root, path), true, 0, d.CreationTimeUtc.Ticks, d.LastWriteTimeUtc.Ticks, null));
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)) Walk(child);
            }
        }
        Walk(root); return result;
    }
    private static bool KnownStageFiles(string root)
    {
        var allowed = DumpPackage.Headers.Keys.SelectMany(n => new[] { "db/" + n, "db/" + n + ".header" })
            .Concat(["TIMESTAMP", "README.txt", "LICENSE-ODBL.txt", "LICENSE-DBCL.txt", "schema_plain.sql", "stage.db", "manifest.json", "normalized-proofs.json"])
            .ToHashSet(StringComparer.Ordinal);
        foreach (var proof in Proof(root, CancellationToken.None, hash: false))
            if (proof.Directory ? proof.Name != "." && proof.Name != "db" : !allowed.Contains(proof.Name.Replace('\\', '/'))) return false;
        return true;
    }
    private static void DeleteTree(string root, IReadOnlyList<DatasetFileProof> proof, bool verifyHash = true)
    {
        // Fixed under the datasets directory; reject all links. Never recursively follow an unchecked path.
        DumpPackage.Ordinary(root);
        foreach (var file in proof.Where(f => !f.Directory))
        {
            var path = file.Name == "." ? root : Path.Combine(root, file.Name); DumpPackage.Ordinary(path);
            if (File.Exists(path))
            {
                var current = new FileInfo(path);
                if (current.Length != file.Length || current.CreationTimeUtc.Ticks != file.Created || current.LastWriteTimeUtc.Ticks != file.Modified ||
                    verifyHash && file.Sha256 is not null && DumpPackage.Hash(path) != file.Sha256) throw new IOException("清理期间文件已变化，停止并保留恢复意图。");
                File.Delete(path);
            }
        }
        foreach (var folder in proof.Where(f => f.Directory).OrderByDescending(f => f.Name.Length))
        {
            var path = folder.Name == "." ? root : Path.Combine(root, folder.Name); DumpPackage.Ordinary(path);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
        }
    }
    private static bool MatchesRemaining(IReadOnlyList<DatasetFileProof> current, IReadOnlyList<DatasetFileProof> expected, bool continuing)
    {
        if (!continuing) return ArchiveFormat.Hash(current) == ArchiveFormat.Hash(expected);
        var prior = expected.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return current.All(f => prior.TryGetValue(f.Name, out var old) && (f.Directory ? old.Directory && old.Created == f.Created : old == f));
    }
}
