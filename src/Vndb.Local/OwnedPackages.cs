using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vndb.Contracts;

namespace Vndb.Local;

// Local-only ownership. An archive is never an installation, component or launch target.
public sealed record LocalOwnedPackage(string Id, string DeviceId, string ItemId, string ScanRoot, string RelativePath,
    long Length, DateTime LastWriteUtc, string Status, DateTimeOffset ObservedAt, long Revision, DateTimeOffset AddedAt)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record PackageCandidate(string ScanRoot, string RelativePath, string QueryTitle, string Kind, long Length)
{
    public string Label => RelativePath + " · " + Kind;
}
public sealed record PackageScan(string ScanRoot, IReadOnlyList<PackageCandidate> Candidates, IReadOnlyList<ScanIssue> Issues);
public sealed record PackageFacts(long Length, DateTime LastWriteUtc, DateTime CreatedUtc);
public sealed record PackageRegistrationPreview(string DeviceId, string LibrarySha256, LocalOwnedPackage Package,
    LibraryItemView Item, PackageFacts Facts, string? Origin, bool Reused);

/// <summary>Bounded filename discovery and explicit registration. Never reads archive contents.</summary>
public sealed partial class OwnedPackages(LocalLibraryStore library, ClientDeviceIdentity device)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".iso", ".img", ".cab", ".msi", ".tar", ".tgz", ".gz", ".bz2", ".xz" };
    [GeneratedRegex(@"\.part(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RarPart();
    [GeneratedRegex(@"\.(7z|zip)\.(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplitPart();
    [GeneratedRegex(@"^(\[[^\]]{1,80}\]|【[^】]{1,80}】)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixTag();
    private static StringComparison Paths => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string Root(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)) throw new ArgumentException("请明确选择检索根的绝对目录。");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        LocalFileLease.RequireOrdinaryAncestors(root);
        return root;
    }
    public static string FilePath(string root, string relative)
    {
        root = Root(root);
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || relative.Any(char.IsControl) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' '))) throw new ArgumentException("安装包必须使用检索根内无上级跳转的相对路径。");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, Paths)) throw new ArgumentException("安装包必须位于检索根内。");
        LocalFileLease.RequireOrdinaryAncestors(path);
        return path;
    }
    private static (string Title, string Kind)? Identify(string name)
    {
        var rar = RarPart().Match(name); var split = SplitPart().Match(name);
        string title; string kind;
        if (rar.Success)
        {
            if (!int.TryParse(rar.Groups[1].Value, out var part) || part != 1) return null;
            title = name[..rar.Index]; kind = "分卷首包（不验证完整性）";
        }
        else if (split.Success)
        {
            if (split.Groups[2].Value != "001") return null;
            title = name[..split.Index]; kind = "分卷首包（不验证完整性）";
        }
        else
        {
            if (!Extensions.Contains(Path.GetExtension(name))) return null;
            title = Path.GetFileNameWithoutExtension(name);
            if (Path.GetExtension(title).Equals(".tar", StringComparison.OrdinalIgnoreCase)) title = Path.GetFileNameWithoutExtension(title);
            kind = Path.GetExtension(name).ToLowerInvariant() switch { ".iso" or ".img" => "光盘镜像", ".msi" => "安装程序", _ => "压缩包" };
        }
        // Suggestions only: keep Japanese punctuation and version text for manual correction.
        var stripped = PrefixTag().Replace(title, "").Trim();
        return (stripped.Length == 0 ? title : stripped, kind);
    }
    public static async Task<PackageScan> ScanAsync(LocalLibraryStore library, string root, int depth, bool outsideInstallations, CancellationToken ct = default)
    {
        var excluded = outsideInstallations ? (await library.ReadAsync(ct)).Installations
            .Where(i => i.Installation.RemovedAt is null && i.Installation.Status == "present" && Directory.Exists(i.Installation.Details.RootPath))
            .Select(i => i.Installation.Details.RootPath).ToArray() : [];
        return await Task.Run(() => Scan(root, depth, ct, excluded), ct);
    }
    public static PackageScan Scan(string root, int depth = 3, CancellationToken ct = default, IReadOnlyList<string>? excludedDirectories = null)
    {
        root = Root(root);
        if (!Directory.Exists(root) || depth is < 1 or > 6) throw new ArgumentException("检索根须存在，深度为1–6。");
        var excluded = new HashSet<string>((excludedDirectories ?? []).Select(Root), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var candidates = new List<PackageCandidate>(); var issues = new List<ScanIssue>(); int directories = 0, files = 0;
        void Walk(string directory, int level)
        {
            ct.ThrowIfCancellationRequested();
            if (excluded.Contains(directory)) return;
            if (++directories > 5000) throw new IOException("扫描超过5000目录，请缩小范围。");
            try
            {
                LocalFileLease.RequireOrdinaryAncestors(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory).Take(100001).Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++files > 100000) throw new IOException("扫描超过100000项，请缩小范围。");
                    var attrs = File.GetAttributes(path);
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if ((attrs & FileAttributes.ReparsePoint) != 0) { issues.Add(new(relative, "reparse-skipped")); continue; }
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        if (level < depth) Walk(path, level + 1); else issues.Add(new(relative, "depth-limit"));
                    }
                    else if (Identify(Path.GetFileName(path)) is { } info)
                    {
                        _ = FilePath(root, relative);
                        if (candidates.Count == 5000) throw new IOException("安装包候选超过5000项，请缩小范围。");
                        candidates.Add(new(root, relative, info.Title, info.Kind, new FileInfo(path).Length));
                    }
                }
            }
            catch (UnauthorizedAccessException) { issues.Add(new(Path.GetRelativePath(root, directory), "access-denied")); }
            catch (DirectoryNotFoundException) { issues.Add(new(Path.GetRelativePath(root, directory), "directory-disappeared")); }
        }
        Walk(root, 0); return new(root, candidates, issues);
    }
    private static PackageFacts Facts(string path)
    {
        LocalFileLease.RequireOrdinaryAncestors(path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new IOException("安装包须为普通文件。");
        var file = new FileInfo(path); return new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
    }
    public static PackageCandidate Candidate(string root, string relative)
    {
        root = Root(root); var path = FilePath(root, relative);
        var info = Identify(Path.GetFileName(path)) ?? throw new ArgumentException("请选择支持的安装包／镜像或分卷首包。");
        return new(root, relative, info.Title, info.Kind, Facts(path).Length);
    }
    internal static void Validate(IReadOnlyList<LocalOwnedPackage> packages)
    {
        if (packages.Count > 5000 || packages.Any(p => p is null) || packages.Select(p => p.Id).Distinct().Count() != packages.Count) throw new InvalidDataException("本机安装包记录无效。");
        foreach (var p in packages)
        {
            if (!Guid.TryParseExact(p.Id, "D", out _) || string.IsNullOrWhiteSpace(p.DeviceId) || string.IsNullOrWhiteSpace(p.ItemId) ||
                p.Status is not ("present" or "missing" or "unknown") || p.Revision < 1 || p.Length < 0) throw new InvalidDataException("本机安装包身份或状态无效。");
            // Validate syntax, not filesystem accessibility: disconnected storage is an unknown observation.
            if (p.RelativePath is null || p.ScanRoot is null || !Path.IsPathFullyQualified(p.ScanRoot) || p.RelativePath.Contains('\\') || p.RelativePath.Contains(':') ||
                p.RelativePath.Split('/').Any(s => s is "" or "." or "..")) throw new InvalidDataException("本机安装包路径无效。");
        }
    }
    public async Task<PackageRegistrationPreview> PreviewAsync(string root, string relative, RegistrationSelection choice, string? origin = null, CancellationToken ct = default)
    {
        root = Root(root); var path = FilePath(root, relative);
        if (Identify(Path.GetFileName(path)) is null) throw new ArgumentException("仅登记支持的普通安装包／镜像或分卷首包。");
        if (choice.Item is null && choice.Metadata is null) throw new ArgumentException("请明确选择VNDB候选或个人书架条目；文件名不会自动确认为作品。");
        var facts = Facts(path); var local = await library.ReadAsync(ct); var now = DateTimeOffset.UtcNow;
        var old = (local.Packages ?? []).SingleOrDefault(p => p.DeviceId == device.Id && p.ScanRoot.Equals(root, Paths) && p.RelativePath.Equals(relative, Paths));
        var item = LocalInstallationRegistration.Resolve(choice, ".", "game", local, null, [], now);
        if (old is not null && old.ItemId != item.Item.Id) throw new ArgumentException("此包已关联其他条目；请先核对，不会覆盖原关联。");
        var reused = old is { Status: "present" } && old.Length == facts.Length && old.LastWriteUtc == facts.LastWriteUtc;
        var package = reused ? old! : new(old?.Id ?? Guid.NewGuid().ToString("D"), device.Id, item.Item.Id, root, relative,
            facts.Length, facts.LastWriteUtc, "present", now, (old?.Revision ?? 0) + 1, old?.AddedAt ?? now) { Extensions = old?.Extensions };
        return new(device.Id, LocalArchiveFormat.Hash(local), package, item, facts, origin, reused);
    }
    public async Task<LocalOwnedPackage> CommitAsync(PackageRegistrationPreview preview, string approvedSha256, CancellationToken ct = default)
    {
        if (preview.DeviceId != device.Id || preview.Package.DeviceId != device.Id || preview.Package.ItemId != preview.Item.Item.Id ||
            LocalArchiveFormat.Hash(preview) != approvedSha256) throw new ArgumentException("请重新预览并批准本次安装包登记。");
        using var operation = await LocalFileLease.AcquireAsync(library.DirectoryPath, "installation-registration", ct);
        if (Facts(FilePath(preview.Package.ScanRoot, preview.Package.RelativePath)) != preview.Facts) throw new IOException("预览后安装包已变化，未登记。");
        var local = await library.ReadAsync(ct);
        if (LocalArchiveFormat.Hash(local) != preview.LibrarySha256) throw new InvalidDataException("本机资料已变化，请重新预览。");
        if (LocalInstallationRegistration.IsPending(preview.Item.Item.Id)) await new LocalRegistrationSync(library).PrepareAsync(preview.Item, preview.Origin, device, ct);
        await library.UpdateAsync(current =>
        {
            if (LocalArchiveFormat.Hash(current) != preview.LibrarySha256) throw new InvalidDataException("登记期间本机资料已变化。");
            return current with { Packages = (current.Packages ?? []).Where(p => p.Id != preview.Package.Id).Append(preview.Package).ToArray(),
                Items = current.Items.Append(preview.Item).DistinctBy(i => i.Item.Id).ToArray() };
        }, ct);
        return preview.Package;
    }
    public Task<LocalLibrary> VerifyAsync(CancellationToken ct = default) => library.UpdateAsync(local => local with {
        Packages = local.Packages?.Select(p =>
        {
            if (p.DeviceId != device.Id) return p;
            ct.ThrowIfCancellationRequested(); var status = "unknown";
            try
            {
                // An unavailable root never revokes ownership; a readable root + missing file does.
                var root = Root(p.ScanRoot); _ = File.GetAttributes(root);
                var path = FilePath(root, p.RelativePath);
                try { var facts = Facts(path); status = facts.Length == p.Length && facts.LastWriteUtc == p.LastWriteUtc ? "present" : "unknown"; }
                catch (FileNotFoundException) { status = "missing"; }
                catch (DirectoryNotFoundException) { status = "missing"; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return p.Status == status ? p : p with { Status = status, Revision = p.Revision + 1, ObservedAt = DateTimeOffset.UtcNow };
        }).ToArray() }, ct);
    internal static bool References(LocalLibrary local, string deviceId, string itemId) =>
        local.Installations.Any(v => v.Installation.RemovedAt is null && v.Installation.Details.DeviceId == deviceId && v.Components.Any(c => c.Details.ItemId == itemId)) ||
        (local.Packages ?? []).Any(p => p.DeviceId == deviceId && p.ItemId == itemId);
}
