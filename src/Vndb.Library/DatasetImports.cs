using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

/// <summary>One existing Web worker owns execution. Durable cursor and writes commit together.</summary>
public sealed partial class DatasetImports(SqliteLibraryStore store, HttpClient http, TimeProvider? time = null, bool workerEnabled = true) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _verified = [];
    private IReadOnlyDictionary<string, string> _rowProofs = new Dictionary<string, string>();
    private string Root => Path.Combine(store.DataDirectory, "datasets");
    private string PathFor(string name)
    {
        if (name.Length > 100 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.'))) throw new InvalidDataException("内部暂存标识无效。");
        var path = Path.Combine(Root, name); DumpPackage.Ordinary(path); return path;
    }
    public DatasetJob Create(DatasetRequest request, string key)
    {
        if (!DateOnly.TryParseExact(request.SnapshotDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Year < 2000)
            throw new ArgumentException("请选择官方导出日期，格式yyyy-MM-dd。");
        return store.EnqueueDataset("vndb-db-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".tar.zst", key);
    }
    public async Task<DatasetJob> UploadAsync(Stream input, string key, CancellationToken ct)
    {
        if (key.Length is < 1 or > 160 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("上传需要有效Idempotency-Key。");
        DumpPackage.EnsureSpace(Root, 4L * 1024 * 1024 * 1024); Directory.CreateDirectory(Root);
        var name = Guid.NewGuid().ToString("N") + ".zst";
        var bytes = await CopyAsync(input, PathFor(name), ct);
        return store.EnqueueDataset("upload", key, name, DumpPackage.Hash(PathFor(name)), bytes);
    }
    private static async Task<long> CopyAsync(Stream input, string file, CancellationToken ct)
    {
        using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            total += count;
            if (total > DumpPackage.MaximumCompressedBytes) throw new ImportValidationException("dataset_too_large", 413, "官方包超过512MiB限额。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total == 0) throw new ArgumentException("导出包为空。");
        await output.FlushAsync(ct); output.Flush(true); return total;
    }
    public DatasetJob Control(string id, string action, DatasetRevision revision, string key) => store.ControlDataset(id, action, revision, key);
    public async Task<bool> StepAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;
        try
        {
            var work = store.ActiveDataset();
            if (work is null || work.Job.State is "paused" or "failed") return false;
            var id = work.Job.Id;
            // Poll durable pause/cancel while preparation is CPU-bound, without adding a second worker.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var poll = new Timer(_ =>
            {
                try { if (store.Dataset(id).Job.State is "paused" or "cancelled") stop.Cancel(); }
                catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException) { }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            try
            {
                if (work.Job.Phase == "download")
                {
                    store.UpdateDataset(id, w => w with { Job = w.Job with { State = "downloading" } });
                    DumpPackage.EnsureSpace(Root, 4L * 1024 * 1024 * 1024); Directory.CreateDirectory(Root);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromMinutes(10));
                    using var response = await http.GetAsync(new Uri("https://dl.vndb.org/dump/" + work.Job.Source), HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if ((int)response.StatusCode == 429)
                    {
                        var retry = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
                        retry = retry < DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow.AddMinutes(1) : retry > DateTimeOffset.UtcNow.AddDays(1) ? DateTimeOffset.UtcNow.AddDays(1) : retry;
                        store.UpdateDataset(id, w => w with { Job = w.Job with { State = "failed", Error = "官方源限流；任务已暂停，不会自动重复请求。", EarliestResumeAt = retry } });
                        return true;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > DumpPackage.MaximumCompressedBytes) throw new InvalidDataException("官方包过大。");
                    var name = Guid.NewGuid().ToString("N") + ".zst";
                    await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
                    var bytes = await CopyAsync(input, PathFor(name), deadline.Token);
                    var sha = DumpPackage.Hash(PathFor(name));
                    store.UpdateDataset(id, w => w with { SourceFile = name, Job = w.Job with { State = "queued", Phase = "prepare", Sha256 = sha, DownloadBytes = bytes } });
                }
                else if (work.Job.Phase == "prepare")
                {
                    if (work.SourceFile is null || DumpPackage.Hash(PathFor(work.SourceFile)) != work.Job.Sha256) throw new InvalidDataException("原包散列变化。");
                    DumpPackage.EnsureSpace(Root, 4L * 1024 * 1024 * 1024);
                    var stageName = "stage-" + Guid.NewGuid().ToString("N");
                    store.UpdateDataset(id, w => w with { Stage = stageName, Job = w.Job with { State = "preparing" } });
                    _ = DumpPackage.Prepare(PathFor(work.SourceFile), PathFor(stageName), stop.Token);
                    var manifest = DumpNormalization.Normalize(PathFor(stageName), stop.Token);
                    var proof = DumpPackage.Hash(Path.Combine(PathFor(stageName), "manifest.json"));
                    store.UpdateDataset(id, w => w with { ManifestSha = proof, Cursor = "", Job = w.Job with { State = "queued", Phase = "producer", SnapshotAt = manifest.SnapshotAt,
                        Total = manifest.Rows["vn"] + manifest.Rows["producers"] } });
                }
                else if (work.Job.Phase is "producer" or "vn")
                {
                    var stagePath = PathFor(work.Stage ?? throw new InvalidDataException("缺少已验证暂存。"));
                    if (DumpPackage.Hash(Path.Combine(stagePath, "manifest.json")) != work.ManifestSha) throw new InvalidDataException("暂存清单变化。");
                    DumpManifest manifest;
                    if (!_verified.TryGetValue(id, out var proof) || proof != work.ManifestSha)
                    {
                        manifest = DumpPackage.Verify(stagePath, stop.Token);
                        var proofs = Path.Combine(stagePath, "normalized-proofs.json");
                        if (new FileInfo(proofs).Length > 32 * 1024 * 1024) throw new InvalidDataException("规范条目证明过大。");
                        _rowProofs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(proofs)) ?? throw new InvalidDataException("缺少规范条目证明。");
                        _verified.Clear();
                        _verified[id] = work.ManifestSha!;
                    }
                    else manifest = JsonSerializer.Deserialize<DumpManifest>(File.ReadAllBytes(Path.Combine(stagePath, "manifest.json")), VndbJson.Options)!;
                    if (!manifest.Normalized || manifest.Sha256 != work.Job.Sha256) throw new InvalidDataException("暂存未完成或来源不符。");
                    using var stage = DumpPackage.OpenStage(stagePath);
                    using var command = stage.CreateCommand();
                    command.CommandText = $"SELECT id,entry_json FROM normalized_{(work.Job.Phase == "vn" ? "novels" : "producers")} WHERE id>$cursor ORDER BY id LIMIT 25";
                    command.Parameters.AddWithValue("$cursor", work.Cursor);
                    using var reader = command.ExecuteReader(); var rows = new List<(string Id, string Json)>(); long bytes = 0;
                    while (reader.Read())
                    {
                        var json = reader.GetString(1);
                        if (json.Length > 8 * 1024 * 1024) throw new InvalidDataException("暂存条目过大。");
                        if (rows.Count > 0 && bytes + json.Length > 8 * 1024 * 1024) break;
                        var rowId = reader.GetString(0);
                        if (!_rowProofs.TryGetValue(work.Job.Phase + "/" + rowId, out var rowSha) ||
                            rowSha != Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))) throw new InvalidDataException("规范条目散列变化。");
                        rows.Add((rowId, json)); bytes += json.Length;
                    }
                    stop.Token.ThrowIfCancellationRequested(); DumpPackage.EnsureSpace(store.DataDirectory, 1024L * 1024 * 1024);
                    store.CommitDatasetBatch(id, work.Cursor, work.Job.Phase, rows, manifest);
                }
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return true; }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException or FormatException or OverflowException or InvalidOperationException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException or ImportValidationException or OperationCanceledException or ZstdSharp.ZstdException)
            {
                store.UpdateDataset(id, w => w with { Job = w.Job with { State = "failed", Error = "下载、格式验证或写入未完成；原资料及暂存保留，请核对后恢复。" } });
                return true;
            }
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { _gate.Dispose(); _syncGate.Dispose(); }
}
