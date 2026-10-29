using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>Query JSON only. Device, launch and installation state never follows this override.</summary>
public sealed record LocalQueryScope(string DataRoot, string? Origin, string DirectoryPath, string QueryRoot)
{
    public static string? Normalize(string? server) => string.IsNullOrWhiteSpace(server) ? null : ClientSettings.ServerUri(server).AbsoluteUri;
    public static string ServerDirectory(string root, string? origin) => Path.Combine(Path.GetFullPath(root), "servers",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(origin) ?? "offline"))));
    public static LocalQueryScope Resolve(string dataRoot, ClientSettings settings, string? server = null, string? cacheDirectory = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var origin = Normalize(server ?? environment("VN_LIBRARY_URL") ?? settings.Server);
        var root = Path.GetFullPath(dataRoot); var queryRoot = Path.GetFullPath(cacheDirectory ?? environment("VN_LIBRARY_CACHE") ?? settings.CacheDirectory ?? root);
        return new(root, origin, ServerDirectory(queryRoot, origin), queryRoot);
    }
    public IReadOnlyList<string> LegacyDirectories => new[] { Path.Combine(DataRoot, "snapshots"), Path.Combine(DataRoot, "cache"), ServerDirectory(DataRoot, Origin), QueryRoot }
        .Where(p => p != DataRoot && p != DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public async Task<QueryScopeStatus> StatusAsync(CancellationToken ct = default)
    {
        await QueryScopeBinding.CheckAsync(DirectoryPath, Origin, create: false, ct);
        var legacy = await new QueryDataMigration(this).PreviewAsync(ct: ct);
        return new(DataRoot, Origin, DirectoryPath, QueryRoot, Count("cache"), Count("pending"), legacy.Files.Count, legacy.Review.Count);
    }
    private int Count(string name)
    {
        var path = Path.Combine(DirectoryPath, name); LocalFileLease.RequireOrdinaryAncestors(path);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.json").Count() : 0;
    }
}
public sealed record QueryScopeStatus(string DataRoot, string? Origin, string DirectoryPath, string QueryRoot,
    int CachedQueries, int RetainedBatches, int LegacyProvenFiles, int LegacyReviewFiles);

internal sealed record QueryScopeBinding(string? Origin, int FormatVersion = 1)
{
    internal static bool IsHistoricalScope(string directory, string? origin)
    {
        var parent = Directory.GetParent(Path.GetFullPath(directory));
        return parent?.Name == "servers" && parent.Parent is { } root &&
            string.Equals(Path.GetFullPath(directory), LocalQueryScope.ServerDirectory(root.FullName, origin), StringComparison.OrdinalIgnoreCase);
    }
    internal static async Task CheckAsync(string directory, string? origin, bool create, CancellationToken ct)
    {
        origin = LocalQueryScope.Normalize(origin); var file = Path.Combine(directory, "query-scope.json");
        LocalFileLease.RequireOrdinaryAncestors(file);
        if (!File.Exists(file) && !create) return;
        using var lease = await LocalFileLease.AcquireAsync(directory, "query-scope", ct);
        if (File.Exists(file))
        {
            var binding = JsonSerializer.Deserialize<QueryScopeBinding>(await QueryDataMigration.BytesAsync(file, ct), VndbJson.Options);
            if (binding is null || binding.FormatVersion != 1 || binding.Origin != origin) throw new InvalidDataException("查询JSON属于其他服务／离线作用域，未读取或提交。");
            return;
        }
        var hasLegacy = false;
        foreach (var name in new[] { "cache", "pending", "receipts", "metadata-operations" })
        {
            var folder = Path.Combine(directory, name); LocalFileLease.RequireOrdinaryAncestors(folder);
            hasLegacy |= Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        if (hasLegacy && !IsHistoricalScope(directory, origin)) throw new InvalidDataException("旧查询JSON来源尚未确认，请使用query-data预览迁移；原件保留。");
        await JsonSnapshotStore.AtomicWriteAsync(file, new QueryScopeBinding(origin), false, ct);
    }
}
