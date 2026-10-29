using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static void InitializeM3(SqliteConnection db, SqliteTransaction tx, int oldVersion)
    {
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY,record_json TEXT NOT NULL CHECK(json_valid(record_json)));
            CREATE TABLE IF NOT EXISTS device_legacy_names(machine_key TEXT PRIMARY KEY,device_id TEXT NOT NULL REFERENCES devices(id));
            CREATE TABLE IF NOT EXISTS installations(id TEXT PRIMARY KEY,device_id TEXT NOT NULL REFERENCES devices(id),path_key TEXT NOT NULL,removed INTEGER NOT NULL,record_json TEXT NOT NULL CHECK(json_valid(record_json)));
            CREATE UNIQUE INDEX IF NOT EXISTS ix_installation_path ON installations(device_id,path_key) WHERE removed=0;
            CREATE TABLE IF NOT EXISTS installation_components(id TEXT PRIMARY KEY,installation_id TEXT NOT NULL REFERENCES installations(id),item_id TEXT NOT NULL REFERENCES library_items(id),record_json TEXT NOT NULL CHECK(json_valid(record_json)));
            CREATE INDEX IF NOT EXISTS ix_component_installation ON installation_components(installation_id);
            CREATE INDEX IF NOT EXISTS ix_component_item ON installation_components(item_id);
            CREATE TABLE IF NOT EXISTS installation_legacy_links(source_kind TEXT NOT NULL,legacy_id TEXT NOT NULL,installation_id TEXT REFERENCES installations(id),component_id TEXT REFERENCES installation_components(id),record_json TEXT NOT NULL CHECK(json_valid(record_json)),PRIMARY KEY(source_kind,legacy_id));
            PRAGMA user_version=7;
            """);
        // Legacy seed writes also obey newer destination guards; create their empty tables first.
        if (oldVersion < 7) InitializeM4(db, tx);
        if (oldVersion >= 7) return;
        var archives = new List<ArchiveLocation>(); var locals = new List<LocalGame>();
        using (var command = Command(db, tx, "SELECT location_json FROM archive_locations ORDER BY id"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) archives.Add(Read<ArchiveLocation>(reader.GetString(0)));
        using (var command = Command(db, tx, "SELECT record_json FROM local_games ORDER BY id"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) locals.Add(Read<LocalGame>(reader.GetString(0)));
        var conflicting = archives.GroupBy(a => MachineKey(a.Registration.Machine) + "\0" + InstallationRules.PathKey(ActualPath(a.Registration)))
            .Where(g => g.Select(a => a.Registration.NovelId).Distinct().Count() > 1).SelectMany(g => g.Select(a => a.Id)).ToHashSet();
        foreach (var archive in archives)
            if (conflicting.Contains(archive.Id))
            {
                _ = LegacyDevice(db, tx, archive.Registration.Machine, ActualPath(archive.Registration));
                WriteLegacyLink(db, tx, new("archive", archive.Id, null, null, "needs_review", "同设备同路径包含不同作品，待确认容器／版本关系。"));
            }
            else SeedArchive(db, tx, archive);
        foreach (var local in locals) SeedLocal(db, tx, local);
    }
    private static string MachineKey(string machine) => machine.Trim().ToUpperInvariant();
    private static string ActualPath(ArchiveRegistration value) => value.State == "planned" ? value.OriginalPath : value.CurrentPath;
    private static T? Entity<T>(SqliteConnection db, SqliteTransaction? tx, string table, string id)
    {
        using var command = Command(db, tx, "SELECT record_json FROM " + table + " WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? Read<T>(json) : default;
    }
    private static void WriteDevice(SqliteConnection db, SqliteTransaction tx, LibraryDevice device)
    {
        var before = Entity<LibraryDevice>(db, tx, "devices", device.Id);
        Execute(db, tx, "INSERT INTO devices VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET record_json=excluded.record_json", ("$id", device.Id), ("$json", Json(device)));
        Event(db, tx, device.Id, "device-write", before is null ? null : Json(before), Json(device));
    }
    private static void WriteInstallation(SqliteConnection db, SqliteTransaction tx, Installation installation)
    {
        var before = Entity<Installation>(db, tx, "installations", installation.Id);
        var path = InstallationRules.PathKey(installation.Details.RootPath);
        if (installation.RemovedAt is null) EnsureArchiveDestinationAvailable(db, tx, installation.Id, installation.Details.DeviceId, installation.Details.RootPath);
        using var collision = Command(db, tx, "SELECT id FROM installations WHERE device_id=$device AND path_key=$path AND removed=0 AND id<>$id",
            ("$device", installation.Details.DeviceId), ("$path", path), ("$id", installation.Id));
        if (installation.RemovedAt is null && collision.ExecuteScalar() is not null) throw new PlayConflictException("该设备的目录已登记，请核对现有安装，不能覆盖或按标题合并。");
        Execute(db, tx, "INSERT INTO installations VALUES($id,$device,$path,$removed,$json) ON CONFLICT(id) DO UPDATE SET device_id=excluded.device_id,path_key=excluded.path_key,removed=excluded.removed,record_json=excluded.record_json",
            ("$id", installation.Id), ("$device", installation.Details.DeviceId), ("$path", path), ("$removed", installation.RemovedAt is not null), ("$json", Json(installation)));
        Event(db, tx, installation.Id, "installation-write", before is null ? null : Json(before), Json(installation));
    }
    private static void WriteComponent(SqliteConnection db, SqliteTransaction tx, InstallationComponent component)
    {
        var before = Entity<InstallationComponent>(db, tx, "installation_components", component.Id);
        Execute(db, tx, "INSERT INTO installation_components VALUES($id,$installation,$item,$json) ON CONFLICT(id) DO UPDATE SET installation_id=excluded.installation_id,item_id=excluded.item_id,record_json=excluded.record_json",
            ("$id", component.Id), ("$installation", component.InstallationId), ("$item", component.Details.ItemId), ("$json", Json(component)));
        Event(db, tx, component.Id, "component-write", before is null ? null : Json(before), Json(component));
    }
    private static LegacyLocationLink? LegacyLink(SqliteConnection db, SqliteTransaction? tx, string kind, string id)
    {
        using var command = Command(db, tx, "SELECT record_json FROM installation_legacy_links WHERE source_kind=$kind AND legacy_id=$id", ("$kind", kind), ("$id", id));
        return command.ExecuteScalar() is string json ? Read<LegacyLocationLink>(json) : null;
    }
    private static void WriteLegacyLink(SqliteConnection db, SqliteTransaction tx, LegacyLocationLink link)
    {
        var before = LegacyLink(db, tx, link.SourceKind, link.LegacyId);
        Execute(db, tx, "INSERT INTO installation_legacy_links VALUES($kind,$id,$installation,$component,$json) ON CONFLICT(source_kind,legacy_id) DO UPDATE SET installation_id=excluded.installation_id,component_id=excluded.component_id,record_json=excluded.record_json",
            ("$kind", link.SourceKind), ("$id", link.LegacyId), ("$installation", link.InstallationId), ("$component", link.ComponentId), ("$json", Json(link)));
        Event(db, tx, link.LegacyId, "legacy-location-map", before is null ? null : Json(before), Json(link));
    }
}
