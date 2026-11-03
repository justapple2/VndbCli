using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vndb.Core;
using Vndb.Contracts;

namespace Vndb.Local;

public sealed record LocalResourceData(string ItemId, string Kind, string RootPath, string RelativePath, string? InstallationId = null,
    string? PackageId = null, string? Language = null, string? Version = null, string? ReleaseId = null, string? SourceUrl = null,
    string Verification = "unreviewed", bool ManuallyApplied = false, string? Notes = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record LocalResource(string Id, string DeviceId, LocalResourceData Details, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, string? Sha256 = null, long? HashLength = null, long? HashWriteTicks = null, DateTimeOffset? RemovedAt = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record LocalResourceDocument(IReadOnlyList<LocalResource> Items, string Format = "vn-local-resources", int FormatVersion = 1)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record LocalResourceFacts(string Status, long? Length, string Message, bool HashCurrent);

/// <summary>Local descriptive archive; never executes, extracts, installs, moves or publishes a resource.</summary>
public sealed class LocalResourceArchive(LocalLibraryStore library, string deviceId)
{
    private readonly JsonStateFile<LocalResourceDocument> _file = new(library.DirectoryPath, "local-resources.json");
    public static IReadOnlyList<string> Kinds { get; } = ["package", "patch", "save", "bonus", "document"];
    public static IReadOnlyList<string> Verifications { get; } = ["unreviewed", "applicable", "incompatible"];
    private static void Validate(LocalResourceDocument doc)
    {
        if (doc.Format != "vn-local-resources" || doc.FormatVersion != 1 || doc.Items is null || doc.Items.Count > 10000 ||
            doc.Items.Any(r => r?.Details is null || !Guid.TryParseExact(r.Id, "D", out _) || r.Revision < 1 || string.IsNullOrWhiteSpace(r.DeviceId)) ||
            doc.Items.Select(r => r.Id).Distinct().Count() != doc.Items.Count) throw new InvalidDataException("本机资源档案格式无效；原件保留。");
        foreach (var row in doc.Items) Validate(row.Details);
    }
    private static void Validate(LocalResourceData data)
    {
        if (!PersonalOrganizationRules.ValidId(data.ItemId) || !Kinds.Contains(data.Kind) || !Verifications.Contains(data.Verification) ||
            data.Language?.Length > 40 || data.Version?.Length > 200 || data.Notes?.Length > 4000 || data.SourceUrl?.Length > 2000 ||
            new[] { data.Language, data.Version }.Any(v => v?.Any(char.IsControl) == true)) throw new ArgumentException("资源类型、核对状态或说明长度无效。");
        _ = OwnedPackages.FilePath(data.RootPath, data.RelativePath, false);
        if (data.ReleaseId is { Length: > 0 } release && VndbId.Normalize(release, 'r') != release) throw new ArgumentException("适用发行ID须为r编号，未知时留空。");
        if (!string.IsNullOrEmpty(data.SourceUrl) && (!Uri.TryCreate(data.SourceUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0)) throw new ArgumentException("来源须为HTTP(S)链接，不能包含账号密码。");
    }
    public async Task<IReadOnlyList<LocalResource>> ReadAsync(string? itemId = null, CancellationToken ct = default)
    {
        var doc = await _file.ReadAsync(ct) ?? new([]); Validate(doc);
        return doc.Items.Where(r => r.DeviceId == deviceId && r.RemovedAt is null && (itemId is null || r.Details.ItemId == itemId)).ToArray();
    }
    public async Task<LocalResource> SaveAsync(LocalResourceData data, string? id = null, long? expectedRevision = null, CancellationToken ct = default, LibraryItemView? selectedItem = null)
    {
        Validate(data); _ = OwnedPackages.FilePath(data.RootPath, data.RelativePath); var local = await library.ReadSummaryAsync(ct);
        if (!local.Items.Any(i => i.Item.Id == data.ItemId && i.Item.DeletedAt is null) && !(selectedItem?.Item.Id == data.ItemId && selectedItem.Item.DeletedAt is null)) throw new ArgumentException("请选择准确的本机书架条目。");
        if (data.InstallationId is { } installation && !local.Installations.Any(i => i.Installation.Id == installation && i.Installation.Details.DeviceId == deviceId && i.Installation.RemovedAt is null && i.Components.Any(c => c.Details.ItemId == data.ItemId)))
            throw new ArgumentException("安装归属不符；资源不按VN ID借用其他版本。");
        if (data.PackageId is { } package && !(local.Packages ?? []).Any(p => p.Id == package && p.DeviceId == deviceId && p.ItemId == data.ItemId)) throw new ArgumentException("安装包归属不符。");
        LocalResource? saved = null;
        await _file.UpdateAsync(doc =>
        {
            doc ??= new([]); Validate(doc); var old = id is null ? null : doc.Items.SingleOrDefault(i => i.Id == id);
            if (id is not null && (old is null || old.DeviceId != deviceId || old.Revision != expectedRevision || old.RemovedAt is not null || old.Details.ItemId != data.ItemId)) throw new InvalidOperationException("资源身份或修订已变化，请重新读取。");
            data = data with { Extensions = data.Extensions ?? old?.Details.Extensions };
            var now = DateTimeOffset.UtcNow;
            saved = old is null ? new(Guid.NewGuid().ToString("D"), deviceId, data, 1, now, now) : old with { Details = data, Revision = old.Revision + 1, UpdatedAt = now };
            if (old is not null && (old.Details.RootPath != data.RootPath || old.Details.RelativePath != data.RelativePath)) saved = saved with { Sha256 = null, HashLength = null, HashWriteTicks = null };
            var result = doc with { Items = doc.Items.Where(i => i.Id != saved.Id).Append(saved).ToArray() }; Validate(result); return result;
        }, ct);
        return saved!;
    }
    public static LocalResourceFacts Inspect(LocalResource resource)
    {
        try
        {
            var path = OwnedPackages.FilePath(resource.Details.RootPath, resource.Details.RelativePath);
            if (!Directory.Exists(resource.Details.RootPath)) return new("unknown", null, "检索根不可用，不能判定资源已删除。", false);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) return new("present", null, "目录存在；未验证内容完整性。", false);
            var info = new FileInfo(path);
            return new("present", info.Length, "文件存在；适用性和应用状态由人工确认。", resource.Sha256 is not null && info.Length == resource.HashLength && info.LastWriteTimeUtc.Ticks == resource.HashWriteTicks);
        }
        catch (FileNotFoundException) { return new("missing", null, "资源文件缺失；档案与其他登记保留。", false); }
        catch (DirectoryNotFoundException) { return new("missing", null, "资源子目录缺失；档案保留。", false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return new("unknown", null, "资源不可读取或位置待核对。", false); }
    }
    public async Task<LocalResource> HashAsync(string id, long expectedRevision, CancellationToken ct = default)
    {
        var row = (await ReadAsync(ct: ct)).Single(r => r.Id == id);
        if (row.Revision != expectedRevision) throw new InvalidOperationException("资源修订已变化。");
        var path = OwnedPackages.FilePath(row.Details.RootPath, row.Details.RelativePath); var before = new FileInfo(path);
        var length = before.Length; var modified = before.LastWriteTimeUtc.Ticks;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)); before.Refresh();
        if (before.Length != length || before.LastWriteTimeUtc.Ticks != modified) throw new IOException("校验期间文件已变化，未更新摘要。");
        LocalResource? result = null;
        await _file.UpdateAsync(doc =>
        {
            if (doc is null) throw new InvalidDataException("资源档案已变化。"); Validate(doc);
            var current = doc.Items.Single(r => r.Id == id);
            if (LocalArchiveFormat.Hash(current) != LocalArchiveFormat.Hash(row)) throw new InvalidOperationException("校验期间资源档案已变化。");
            result = row with { Sha256 = hash, HashLength = length, HashWriteTicks = modified, Revision = row.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            return doc with { Items = doc.Items.Select(r => r.Id == id ? result : r).ToArray() };
        }, ct);
        return result!;
    }
}
