using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record RegistrationSelection(string Title, LibraryItemView? Item = null, MetadataSnapshot? Metadata = null, string? Company = null)
{
    public static async Task<JsonElement> ReadJsonAsync(string file, CancellationToken ct)
    {
        LocalFileLease.RequireOrdinaryAncestors(file);
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("登记输入超过8MiB，请分批选择。");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct); return json.RootElement.Clone();
    }
    public static RegistrationSelection FromJson(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("作品JSON需要明确的单条对象。");
        if (json.TryGetProperty("items", out var items))
        { if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != 1) throw new ArgumentException("请选择一部作品；合集请逐项添加组件。"); json = items[0]; }
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("作品JSON条目无效。");
        if (json.TryGetProperty("item", out _))
        {
            var item = json.Deserialize<LibraryItemView>(VndbJson.Options) ?? throw new ArgumentException("条目JSON无效。");
            if (item.Item?.Details is null) throw new ArgumentException("条目JSON缺少作品资料。");
            return new(item.Item.Details.Title, item, item.Metadata, item.Item.Details.CompanyHint);
        }
        var metadata = json.Deserialize<MetadataSnapshot>(VndbJson.Options) ?? throw new ArgumentException("作品JSON无效。");
        return new(metadata.Title, Metadata: metadata);
    }
}
public sealed record RegistrationComponent(RegistrationSelection Work, string RelativePath = ".", string Kind = "game", string? Label = null);
public sealed record InstallationRegistration(string RootPath, string Title, IReadOnlyList<RegistrationComponent> Components, string? Company = null, string? Origin = null);
public sealed record RegistrationDirectory(string Path, LocalPhysicalIdentity Identity);
public sealed record RegistrationPreview(InstallationRegistration Request, string DeviceId, string LibrarySha256,
    LocalInstallationView Installation, IReadOnlyList<LibraryItemView> Items, IReadOnlyList<RegistrationDirectory> Directories, bool Reused);
public sealed record RegistrationResult(LocalInstallationView Installation, IReadOnlyList<LibraryItemView> Items, bool Reused);

/// <summary>Explicit local association only. Never scans a tree, launches an EXE or moves a directory.</summary>
public sealed class LocalInstallationRegistration(LocalLibraryStore library, ClientDeviceIdentity device)
{
    public const string PendingPrefix = "local-item-";
    public static bool IsPending(string id) => id.StartsWith(PendingPrefix, StringComparison.Ordinal);
    private static void Text(string? value, bool required = true)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value?.Length > 500 || value?.Any(char.IsControl) == true)
            throw new ArgumentException("登记名称为空、过长或包含控制字符。");
    }
    public async Task<RegistrationPreview> PreviewAsync(InstallationRegistration request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Text(request.Title); Text(request.Company, false);
        if (request.Components is null || request.Components.Count is < 1 or > 100 || request.Components.Any(c => c?.Work is null))
            throw new ArgumentException("请明确添加1–100个游戏／特典组件。");
        var root = LocalFiles.Full(request.RootPath); LocalFiles.CheckAncestors(root);
        if (!Directory.Exists(root) || Path.GetPathRoot(root) == root || LocalFiles.Same(root, library.DirectoryPath) || LocalFiles.Within(root, library.DirectoryPath) || LocalFiles.Within(library.DirectoryPath, root))
            throw new ArgumentException("请选择存在的完整游戏目录，不能选择盘根或本机数据目录。");
        request = request with { RootPath = root, Origin = request.Origin is null ? null : ClientSettings.ServerUri(request.Origin).AbsoluteUri };
        var local = await library.ReadAsync(ct);
        var matches = local.Installations.Where(i => LocalFiles.Same(i.Installation.Details.RootPath, root)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("此目录有多个历史安装，请先明确核对。");
        var old = matches.SingleOrDefault();
        if (old is not null && (old.Installation.RemovedAt is not null || old.Installation.Details.DeviceId != device.Id))
            throw new InvalidDataException("此目录已由已移除或其他设备的安装占用，请先核对原记录。");
        if (local.Installations.Any(i => i.Installation.RemovedAt is null && i.Installation.Id != old?.Installation.Id &&
            (LocalFiles.Within(root, i.Installation.Details.RootPath) || LocalFiles.Within(i.Installation.Details.RootPath, root))))
            throw new InvalidDataException("目录与已登记的完整安装重叠；合集和特典应登记为同一容器的组件。");
        if (old is not null && (old.Installation.Details.Title != request.Title || old.Installation.Details.CompanyGroup != request.Company))
            throw new InvalidDataException("已有容器名称或分组不同；请沿用原值添加组件，不用登记覆盖原记录。");
        var now = DateTimeOffset.UtcNow; var installId = old?.Installation.Id ?? Guid.NewGuid().ToString("D");
        var components = old?.Components.ToList() ?? []; var selected = new List<LibraryItemView>();
        var directories = new List<RegistrationDirectory> { new(root, NativeFiles.Identity(root)) };
        var normalized = new List<RegistrationComponent>();
        foreach (var input in request.Components)
        {
            ct.ThrowIfCancellationRequested(); Text(input.Work.Title); Text(input.Work.Company, false); Text(input.Label, false);
            var relative = Relative(input.RelativePath);
            if (input.Kind is not ("game" or "bonus" or "edition" or "other")) throw new ArgumentException("组件类型无效。");
            var path = relative == "." ? root : LocalFiles.Full(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            LocalFiles.CheckAncestors(path);
            if (!Directory.Exists(path) || !LocalFiles.Same(root, path) && !LocalFiles.Within(root, path)) throw new ArgumentException("组件目录必须存在于完整容器内。");
            var identity = NativeFiles.Identity(path);
            if (identity.Volume != directories[0].Identity.Volume) throw new ArgumentException("组件不能跨卷或经过挂载入口。");
            directories.Add(new(path, identity)); normalized.Add(input with { RelativePath = relative });
            var work = Resolve(input.Work, relative, input.Kind, local, old, selected, now);
            selected.Add(work);
            var previousComponent = components.SingleOrDefault(c => c.Details.ItemId == work.Item.Id && c.Details.RelativePath.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase) && c.Details.Kind == input.Kind);
            if (previousComponent is not null)
            {
                if (input.Label is not null && input.Label != previousComponent.Details.Label) throw new ArgumentException("组件已有不同说明，登记不会覆盖原组件；请先核对原记录。");
                continue;
            }
            components.Add(new(Guid.NewGuid().ToString("D"), installId, new(work.Item.Id, relative, input.Kind, input.Label), 1, now, now));
        }
        var changed = old is null || components.Count != old.Components.Count || old.Installation.Status != "present" || old.Installation.DirectoryIdentity != directories[0].Identity;
        var installation = old?.Installation ?? new(installId, new(device.Id, root, request.Title, request.Company), "present", now, 1, now, now);
        if (changed && old is not null) installation = installation with { Status = "present", Revision = installation.Revision + 1, ObservedAt = now, UpdatedAt = now };
        installation = installation with { DirectoryIdentity = directories[0].Identity };
        return new(request with { Components = normalized }, device.Id, LocalArchiveFormat.Hash(local), new(installation, components), selected.DistinctBy(i => i.Item.Id).ToArray(), directories, !changed);
    }
    private static string Relative(string path)
    {
        if (path == ".") return path;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 220 || path.Any(char.IsControl) || path.Contains(':')) throw new ArgumentException("组件需要容器内相对目录。");
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))) throw new ArgumentException("组件不能包含绝对、上级或歧义路径。");
        return string.Join('/', parts);
    }
    internal static LibraryItemView Resolve(RegistrationSelection choice, string relative, string kind, LocalLibrary local,
        LocalInstallationView? old, List<LibraryItemView> selected, DateTimeOffset now)
    {
        var metadata = choice.Metadata ?? choice.Item?.Metadata;
        if (metadata is not null && (metadata.Kind != "vn" || metadata.Novel is null || VndbId.Normalize(metadata.VndbId, 'v') != metadata.Novel.Id))
            throw new ArgumentException("请选择视觉小说资料，不能把会社或身份不符的资料登记成游戏。");
        if (choice.Item is { } item)
        {
            if (item.Item?.Details is null || string.IsNullOrWhiteSpace(item.Item.Id) || item.Item.DeletedAt is not null || metadata is not null && item.Item.Details.NovelId != metadata.VndbId)
                throw new ArgumentException("条目已移除或作品关联不符。");
            var prior = local.Items.SingleOrDefault(i => i.Item.Id == item.Item.Id);
            if (prior is not null && (prior.Item.DeletedAt is not null || prior.Item.Details.NovelId != item.Item.Details.NovelId)) throw new InvalidDataException("本机条目关联已改变。");
            return prior ?? item;
        }
        var existing = local.Items.Concat(selected).DistinctBy(i => i.Item.Id).Where(i => i.Item.DeletedAt is null &&
            (metadata is not null ? i.Item.Details.NovelId == metadata.VndbId : old?.Components.Any(c => c.Details.ItemId == i.Item.Id && c.Details.RelativePath == relative && c.Details.Kind == kind) == true && i.Item.Details.Title == choice.Title && i.Item.Details.CompanyHint == choice.Company)).ToArray();
        if (existing.Length > 1) throw new ArgumentException("存在多个相同作品条目，请明确选择要关联的书架条目。");
        if (existing.Length == 1) return existing[0];
        var details = new LibraryItemData(choice.Title, metadata?.VndbId, choice.Company);
        if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(new { details, metadata }, VndbJson.Options))) throw new ArgumentException("公开资料不能包含本机目录或程序信息。");
        return new(new(PendingPrefix + Guid.NewGuid().ToString("D"), details, 1, now, now), metadata, 0);
    }
    public async Task<RegistrationResult> CommitAsync(RegistrationPreview preview, string approvedSha256, CancellationToken ct = default)
    {
        if (preview.DeviceId != device.Id || LocalArchiveFormat.Hash(preview) != approvedSha256) throw new ArgumentException("登记预览或批准摘要不符，请重新预览。");
        using var operation = await LocalFileLease.AcquireAsync(library.DirectoryPath, "installation-registration", ct);
        foreach (var directory in preview.Directories)
        { ct.ThrowIfCancellationRequested(); LocalFiles.CheckAncestors(directory.Path); if (NativeFiles.Identity(directory.Path) != directory.Identity) throw new IOException("预览后目录身份发生变化，未登记。"); }
        var current = await library.ReadAsync(ct);
        if (LocalArchiveFormat.Hash(current) != preview.LibrarySha256) throw new InvalidDataException("本机资料在预览后变化，请重新预览，未覆盖原记录。");
        foreach (var item in preview.Items.Where(i => IsPending(i.Item.Id)))
            await new LocalRegistrationSync(library).PrepareAsync(item, preview.Request.Origin, device, ct);
        await library.UpdateAsync(local =>
        {
            if (LocalArchiveFormat.Hash(local) != preview.LibrarySha256) throw new InvalidDataException("登记期间本机资料发生变化，请重新预览。");
            return local with {
                Installations = preview.Reused ? local.Installations : local.Installations.Where(i => i.Installation.Id != preview.Installation.Installation.Id).Append(preview.Installation).ToArray(),
                Items = local.Items.Concat(preview.Items).DistinctBy(i => i.Item.Id).ToArray() };
        }, ct);
        return new(preview.Installation, preview.Items, preview.Reused);
    }
}
