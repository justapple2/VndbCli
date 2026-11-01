using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

// Durable small markers prevent a crashed archive/restore from becoming an "uninstall".
internal static class ObservationResourceIntents
{
    internal const string Resource = "local-resource-observation";
    private sealed record Intent(string InstallationId, string RunId, bool Active);
    private static string FileName(string directory, string id) => Path.Combine(directory, "local-resource-intents", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    public static async Task SetAsync(string directory, string id, string runId, bool active, CancellationToken ct) =>
        await JsonSnapshotStore.AtomicWriteAsync(FileName(directory, id), new Intent(id, runId, active), true, ct);
    public static async Task<HashSet<string>> ActiveIdsAsync(string directory, CancellationToken ct)
    {
        var folder = Path.Combine(directory, "local-resource-intents"); LocalFileLease.RequireOrdinaryAncestors(folder);
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return active;
        var paths = Directory.EnumerateFiles(folder, "*.json").Where(p => Path.GetFileName(p) != "initialized.json").Take(10001).ToArray();
        if (paths.Length > 10000) throw new InvalidDataException("本机整理意图标记超过预算，请人工归档已完成记录。");
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("整理意图标记不是小型普通文件。");
            var value = JsonSerializer.Deserialize<Intent>(await File.ReadAllBytesAsync(path, ct), VndbJson.Options) ?? throw new InvalidDataException("整理意图标记无效。");
            if (!string.Equals(path, FileName(directory, value.InstallationId), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("整理意图身份不符，暂停自动观测。");
            if (value.Active) active.Add(value.InstallationId);
        }
        return active;
    }
    private sealed record PlanHead(string InstallationId);
    private sealed record ReceiptHead(string State);
    private sealed record RunHead(PlanHead Plan, ReceiptHead Receipt);
    public static async Task SeedAsync(string directory, CancellationToken ct)
    {
        var marker = new JsonStateFile<Dictionary<string, int>>(directory, "local-resource-intents/initialized.json");
        if (await marker.ReadAsync(ct) is { } prior)
        { if (prior.GetValueOrDefault("version") != 1) throw new InvalidDataException("整理意图索引版本无效，暂停观测。"); return; }
        var folder = Path.Combine(directory, "organization-state"); LocalFileLease.RequireOrdinaryAncestors(folder);
        if (Directory.Exists(folder))
        {
            var paths = Directory.GetFiles(folder, "*.json"); if (paths.Length > 1000) throw new InvalidDataException("旧整理意图过多，请先核对后启用监控。");
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested(); LocalFileLease.RequireOrdinaryAncestors(path);
                if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("旧整理意图过大，暂停观测等待核对。");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var head = await JsonSerializer.DeserializeAsync<RunHead>(stream, VndbJson.Options, ct) ?? throw new InvalidDataException("旧整理意图无效。");
                if (head.Receipt.State is "planned" or "restore_planned") await SetAsync(directory, head.Plan.InstallationId, Path.GetFileNameWithoutExtension(path), true, ct);
            }
        }
        await marker.SaveAsync(new() { ["version"] = 1 }, ct);
    }
}
