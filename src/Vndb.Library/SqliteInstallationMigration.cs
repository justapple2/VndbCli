using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static string LegacyDevice(SqliteConnection db, SqliteTransaction tx, string machine, string path)
    {
        using var get = Command(db, tx, "SELECT device_id FROM device_legacy_names WHERE machine_key=$name", ("$name", MachineKey(machine)));
        if (get.ExecuteScalar() is string id) return id;
        var now = DateTimeOffset.UtcNow;
        id = "legacy-" + PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(MachineKey(machine)))[..32];
        WriteDevice(db, tx, new(id, new(machine, InstallationRules.Windows(path) ? "windows" : "unknown"), 1, now, now, Legacy: true));
        Execute(db, tx, "INSERT INTO device_legacy_names VALUES($name,$device)", ("$name", MachineKey(machine)), ("$device", id)); return id;
    }
    private static string ComponentNovel(SqliteConnection db, SqliteTransaction tx, InstallationComponent component) => Item(db, tx, component.Details.ItemId).Details.NovelId ?? "";
    private static string InstallItem(SqliteConnection db, SqliteTransaction tx, string? novel, string title, string origin)
    {
        if (novel is not null) return EnsureNovelItem(db, tx, novel, title);
        var id = "item-install-" + PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(origin));
        if (Entity<LibraryItem>(db, tx, "library_items", id) is null)
        {
            var now = DateTimeOffset.UtcNow; WriteItem(db, tx, new(id, new(title), 1, now, now), "installation-item-seed");
        }
        return id;
    }
    private static (Installation Installation, InstallationComponent Component)? AtPath(SqliteConnection db, SqliteTransaction tx, string device, string path)
    {
        using var get = Command(db, tx, "SELECT record_json FROM installations WHERE device_id=$device AND path_key=$path AND removed=0", ("$device", device), ("$path", InstallationRules.PathKey(path)));
        if (get.ExecuteScalar() is not string json) return null;
        var installation = Read<Installation>(json);
        using var components = Command(db, tx, "SELECT record_json FROM installation_components WHERE installation_id=$id", ("$id", installation.Id));
        var rows = new List<InstallationComponent>(); using (var reader = components.ExecuteReader()) while (reader.Read()) rows.Add(Read<InstallationComponent>(reader.GetString(0)));
        return rows.Count == 1 && rows[0].Details.RelativePath == "." ? (installation, rows[0]) : null;
    }
    private static bool OccupiedRoot(SqliteConnection db, SqliteTransaction tx, string device, string path)
    {
        using var get = Command(db, tx, "SELECT 1 FROM installations WHERE device_id=$device AND path_key=$path AND removed=0", ("$device", device), ("$path", InstallationRules.PathKey(path)));
        return get.ExecuteScalar() is not null;
    }
    private static (Installation Installation, InstallationComponent Component) SeedInstallation(SqliteConnection db, SqliteTransaction tx,
        string device, string path, string title, string? novel, string origin, string? group, DateTimeOffset? observed)
    {
        var now = DateTimeOffset.UtcNow;
        var stable = PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(origin))[..32];
        var installation = new Installation("installation-" + stable, new(device, path, title, group), observed is null ? "unverified" : "recorded", observed, 1, now, now);
        WriteInstallation(db, tx, installation);
        var component = new InstallationComponent("component-" + stable, installation.Id,
            new(InstallItem(db, tx, novel, title, origin), ".", Label: title), 1, now, now);
        WriteComponent(db, tx, component); return (installation, component);
    }
    private static void SeedArchive(SqliteConnection db, SqliteTransaction tx, ArchiveLocation archive)
    {
        if (LegacyLink(db, tx, "archive", archive.Id) is not null) return;
        var r = archive.Registration; var path = ActualPath(r); var device = LegacyDevice(db, tx, r.Machine, path);
        var found = AtPath(db, tx, device, path);
        if (found is { } current && ComponentNovel(db, tx, current.Component) != r.NovelId || found is null && OccupiedRoot(db, tx, device, path))
        {
            WriteLegacyLink(db, tx, new("archive", archive.Id, null, null, "needs_review", "该设备路径已有不同作品，未合并。")); return;
        }
        var title = NovelEntry(db, tx, r.NovelId)?.Novel.Title ?? r.NovelId;
        var row = found ?? SeedInstallation(db, tx, device, path, title, r.NovelId, "archive/" + archive.Id, r.SelectedProducerId, r.State == "planned" ? null : r.VerifiedAt);
        WriteLegacyLink(db, tx, new("archive", archive.Id, row.Installation.Id, row.Component.Id, "mapped", "唯一同设备实际路径；保留旧归档ID、原路径与证据。"));
    }
    private static void SeedLocal(SqliteConnection db, SqliteTransaction tx, LocalGame local)
    {
        if (LegacyLink(db, tx, "local", local.Id) is not null) return;
        var d = local.Details; var device = LegacyDevice(db, tx, d.Machine, d.Path); var key = InstallationRules.PathKey(d.Path);
        var candidates = new List<LegacyLocationLink>();
        using (var command = Command(db, tx, "SELECT location_json FROM archive_locations WHERE upper(machine)=$machine", ("$machine", MachineKey(d.Machine))))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var archive = Read<ArchiveLocation>(reader.GetString(0)); var r = archive.Registration;
                if (InstallationRules.PathKey(ActualPath(r)) == key || InstallationRules.PathKey(r.OriginalPath) == key)
                    if (LegacyLink(db, tx, "archive", archive.Id) is { } link) candidates.Add(link);
            }
        var unique = candidates.Where(l => l.ComponentId is not null).DistinctBy(l => l.ComponentId).ToArray();
        if (candidates.Count > 0)
        {
            if (unique.Length == 1 && candidates.All(l => l.State == "mapped"))
            {
                var component = Entity<InstallationComponent>(db, tx, "installation_components", unique[0].ComponentId!)!;
                if (local.NovelId is null || ComponentNovel(db, tx, component) == local.NovelId)
                {
                    WriteLegacyLink(db, tx, new("local", local.Id, component.InstallationId, component.Id, "mapped", "唯一同设备当前／原路径与作品身份兼容；旧观测仅作来源。")); return;
                }
            }
            WriteLegacyLink(db, tx, new("local", local.Id, null, null, "needs_review", "旧路径候选有歧义或作品不一致，保留原记录待确认。")); return;
        }
        var found = AtPath(db, tx, device, d.Path);
        if (found is { } existing && ComponentNovel(db, tx, existing.Component) != (local.NovelId ?? "") || found is null && OccupiedRoot(db, tx, device, d.Path))
        { WriteLegacyLink(db, tx, new("local", local.Id, null, null, "needs_review", "同设备路径关系不一致，未合并。")); return; }
        var row = found ?? SeedInstallation(db, tx, device, d.Path, d.Title, local.NovelId, "local/" + local.Id, d.CompanyHint, d.ObservedAt);
        WriteLegacyLink(db, tx, new("local", local.Id, row.Installation.Id, row.Component.Id, "mapped", "独立旧目录观测；不按标题或数量合并。"));
    }
    private static string ComponentPath(SqliteConnection db, SqliteTransaction? tx, InstallationComponent component)
    {
        var installation = Entity<Installation>(db, tx, "installations", component.InstallationId)!;
        return InstallationRules.Join(installation.Details.RootPath, component.Details.RelativePath);
    }
    private ArchiveLocation ProjectArchive(ArchiveLocation value)
    {
        using var db = Open(); var link = LegacyLink(db, null, "archive", value.Id);
        if (link?.ComponentId is not { } id || value.Registration.State == "planned") return value;
        var component = Entity<InstallationComponent>(db, null, "installation_components", id)!;
        return value with { Registration = value.Registration with { CurrentPath = ComponentPath(db, null, component) } };
    }
    private LocalGame ProjectLocal(LocalGame value)
    {
        using var db = Open(); var link = LegacyLink(db, null, "local", value.Id);
        if (link?.ComponentId is not { } id) return value;
        var component = Entity<InstallationComponent>(db, null, "installation_components", id)!;
        var item = Entity<LibraryItem>(db, null, "library_items", component.Details.ItemId)!;
        return value with { Details = value.Details with { Path = ComponentPath(db, null, component) }, NovelId = item.Details.NovelId, Revision = component.Revision, UpdatedAt = component.UpdatedAt };
    }
}
