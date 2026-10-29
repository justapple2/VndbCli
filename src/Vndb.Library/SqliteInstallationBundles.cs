using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public InstallationView BundleInstallations(InstallationBundle request, string key)
    {
        ValidateInstallation(request.Details);
        if (request.Children is null || request.Children.Count is < 2 or > 100 || request.Children.DistinctBy(c => c.Id).Count() != request.Children.Count)
            throw new ArgumentException("完整容器需要2–100个不同安装及各自修订。");
        return Mutate("installation/bundle", key, request, (db, tx) =>
        {
            _ = ActiveDevice(db, tx, request.Details.DeviceId);
            var now = DateTimeOffset.UtcNow;
            var children = request.Children.Select(r => ActiveInstallation(db, tx, r.Id, r.ExpectedRevision)).ToArray();
            foreach (var child in children) EnsureNoArchiveLease(db, tx, child.Id);
            if (children.Any(c => c.Details.DeviceId != request.Details.DeviceId)) throw new ArgumentException("不能把不同设备并入同一物理容器。");
            var prefixes = children.ToDictionary(c => c.Id, c => InstallationRules.Under(request.Details.RootPath, c.Details.RootPath));
            var components = Rows<InstallationComponent>(db, tx, "installation_components").Where(c => prefixes.ContainsKey(c.InstallationId)).ToArray();
            var existing = request.Container is { } reference ? ActiveInstallation(db, tx, reference.Id, reference.ExpectedRevision) : null;
            if (existing is not null) EnsureNoArchiveLease(db, tx, existing.Id);
            if (existing is not null && (prefixes.ContainsKey(existing.Id) || existing.Details.DeviceId != request.Details.DeviceId || InstallationRules.PathKey(existing.Details.RootPath) != InstallationRules.PathKey(request.Details.RootPath)))
                throw new ArgumentException("复用的外层安装必须属于同一设备、相同根目录，并独立于章节安装。");
            var container = new Installation(existing?.Id ?? "installation-" + Guid.NewGuid().ToString("N"), request.Details, "unverified", null, (existing?.Revision ?? 0) + 1, existing?.CreatedAt ?? now, now);
            WriteInstallation(db, tx, container);
            var moved = Rows<InstallationComponent>(db, tx, "installation_components").Where(c => c.InstallationId == container.Id).ToList();
            foreach (var component in components)
            {
                var relative = prefixes[component.InstallationId] + (component.Details.RelativePath == "." ? "" : "/" + component.Details.RelativePath);
                var result = component with { InstallationId = container.Id, Details = component.Details with { RelativePath = relative }, Revision = component.Revision + 1, UpdatedAt = now };
                WriteComponent(db, tx, result); moved.Add(result);
            }
            var links = new List<LegacyLocationLink>();
            using (var select = Command(db, tx, "SELECT record_json FROM installation_legacy_links"))
            using (var reader = select.ExecuteReader()) while (reader.Read()) links.Add(Read<LegacyLocationLink>(reader.GetString(0)));
            foreach (var link in links.Where(l => l.InstallationId is not null && prefixes.ContainsKey(l.InstallationId)))
                WriteLegacyLink(db, tx, link with { InstallationId = container.Id, Revision = link.Revision + 1, Basis = link.Basis + " 明确并入完整物理容器，组件身份保留。" });
            foreach (var child in children) WriteInstallation(db, tx, child with { RemovedAt = now, MergedIntoId = container.Id, Revision = child.Revision + 1, UpdatedAt = now });
            return new InstallationView(container, moved);
        });
    }
    public IReadOnlyList<NovelPresence> Presence(string? currentDeviceId = null, string? novelId = null)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction(deferred: true);
        var devices = Rows<LibraryDevice>(db, transaction, "devices").ToDictionary(d => d.Id);
        var installations = Rows<Installation>(db, transaction, "installations").Where(i => i.RemovedAt is null).ToDictionary(i => i.Id);
        var items = Rows<LibraryItem>(db, transaction, "library_items").ToDictionary(i => i.Id);
        var result = new Dictionary<string, List<PresenceLocation>>();
        foreach (var component in Rows<InstallationComponent>(db, transaction, "installation_components"))
        {
            var item = items[component.Details.ItemId];
            if (item.Details.NovelId is not { } vn || novelId is not null && novelId != vn || !installations.TryGetValue(component.InstallationId, out var installation)) continue;
            var device = devices[installation.Details.DeviceId];
            var deviceState = device.Legacy ? "unassigned" : device.LastSeenAt is null ? "unknown" : DateTimeOffset.UtcNow - device.LastSeenAt < TimeSpan.FromMinutes(15) ? "recently_seen" : "offline";
            var scope = device.Legacy ? "unassigned" : currentDeviceId == device.Id ? "current_device" : "other_device";
            if (!result.TryGetValue(vn, out var entries)) result[vn] = entries = [];
            entries.Add(new(device.Id, device.Details.Name, installation.Id, component.Id,
                InstallationRules.Join(installation.Details.RootPath, component.Details.RelativePath), installation.Status, installation.ObservedAt, deviceState, scope));
        }
        transaction.Commit();
        return result.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new NovelPresence(r.Key, r.Value)).ToArray();
    }
}
