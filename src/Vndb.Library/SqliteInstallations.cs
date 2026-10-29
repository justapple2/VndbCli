using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static IReadOnlyList<T> Rows<T>(SqliteConnection db, SqliteTransaction? tx, string table)
    {
        using var command = Command(db, tx, "SELECT record_json FROM " + table + " ORDER BY id");
        using var reader = command.ExecuteReader(); var rows = new List<T>();
        while (reader.Read()) rows.Add(Read<T>(reader.GetString(0))); return rows;
    }
    public IReadOnlyList<LibraryDevice> Devices() { using var db = Open(); return Rows<LibraryDevice>(db, null, "devices"); }
    public Installation? GetInstallation(string id) { using var db = Open(); return Entity<Installation>(db, null, "installations", id); }
    public IReadOnlyList<Installation> Installations() { using var db = Open(); return Rows<Installation>(db, null, "installations"); }
    public IReadOnlyList<InstallationComponent> Components(string? installationId = null)
    {
        using var db = Open(); return Rows<InstallationComponent>(db, null, "installation_components").Where(c => installationId is null || c.InstallationId == installationId).ToArray();
    }
    public IReadOnlyList<LegacyLocationLink> LegacyLocations()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM installation_legacy_links ORDER BY source_kind,legacy_id");
        using var reader = command.ExecuteReader(); var rows = new List<LegacyLocationLink>(); while (reader.Read()) rows.Add(Read<LegacyLocationLink>(reader.GetString(0))); return rows;
    }
    private static LibraryDevice ActiveDevice(SqliteConnection db, SqliteTransaction tx, string id)
    {
        var device = Entity<LibraryDevice>(db, tx, "devices", id) ?? throw new ArgumentException("设备不存在。");
        if (device.MappedToId is not null) throw new PlayConflictException("旧设备已映射，请使用新设备ID。"); return device;
    }
    private static Installation ActiveInstallation(SqliteConnection db, SqliteTransaction tx, string id, long? revision)
    {
        var current = Entity<Installation>(db, tx, "installations", id) ?? throw new ArgumentException("安装不存在。");
        if (current.Revision != revision || current.RemovedAt is not null) throw new PlayConflictException("安装已更新／移除，请重新载入。"); return current;
    }
    public LibraryDevice SaveDevice(DeviceWrite write, string key)
    {
        if (!Guid.TryParseExact(write.Id, "D", out var guid) || guid.ToString("D") != write.Id) throw new ArgumentException("新设备ID必须是客户端随机生成并保存的规范UUID。");
        InstallationRules.Text(write.Details.Name, 128);
        if (write.Details.Platform is not ("windows" or "linux" or "macos" or "unknown")) throw new ArgumentException("无效设备平台。");
        return Mutate("device/save/" + write.Id, key, write, (db, tx) =>
        {
            var before = Entity<LibraryDevice>(db, tx, "devices", write.Id);
            if (before is not null && before.Revision != write.ExpectedRevision) throw new PlayConflictException("设备修订不一致。");
            var now = DateTimeOffset.UtcNow;
            var result = before is null ? new LibraryDevice(write.Id, write.Details, 1, now, now) : before with { Details = write.Details, Revision = before.Revision + 1, UpdatedAt = now };
            WriteDevice(db, tx, result); return result;
        });
    }
    public LibraryDevice DeviceSeen(string id, RevisionRequest revision, string key) => Mutate("device/seen/" + id, key, revision, (db, tx) =>
    {
        var before = ActiveDevice(db, tx, id);
        if (before.Legacy || before.Revision != revision.ExpectedRevision) throw new PlayConflictException("请先注册稳定设备ID并重新载入修订。");
        var now = DateTimeOffset.UtcNow; var result = before with { LastSeenAt = now, UpdatedAt = now, Revision = before.Revision + 1 };
        WriteDevice(db, tx, result); return result;
    });
    public LibraryDevice ClaimLegacyDevice(string id, LegacyDeviceClaim claim, string key) => Mutate("device/claim/" + id, key, claim, (db, tx) =>
    {
        var old = ActiveDevice(db, tx, id); var target = ActiveDevice(db, tx, claim.DeviceId);
        if (!old.Legacy || target.Legacy || old.Id == target.Id || old.Revision != claim.ExpectedRevision) throw new PlayConflictException("仅能将未映射的旧机器明确关联到已注册客户端设备。");
        foreach (var installation in Rows<Installation>(db, tx, "installations").Where(i => i.Details.DeviceId == old.Id))
        {
            EnsureNoArchiveLease(db, tx, installation.Id);
            WriteInstallation(db, tx, installation with { Details = installation.Details with { DeviceId = target.Id }, Revision = installation.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow });
        }
        Execute(db, tx, "UPDATE device_legacy_names SET device_id=$target WHERE device_id=$old", ("$target", target.Id), ("$old", old.Id));
        var result = old with { MappedToId = target.Id, Revision = old.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        WriteDevice(db, tx, result); return result;
    });
    private static void ValidateInstallation(InstallationData data)
    { ArgumentNullException.ThrowIfNull(data); InstallationRules.Text(data.Title); InstallationRules.Text(data.CompanyGroup, required: false); _ = InstallationRules.PathKey(data.RootPath); }
    public Installation SaveInstallation(InstallationWrite write, string? id, string key)
    {
        ValidateInstallation(write.Details);
        return Mutate("installation/save/" + (id ?? "new"), key, write, (db, tx) =>
        {
            _ = ActiveDevice(db, tx, write.Details.DeviceId);
            var old = id is null ? null : ActiveInstallation(db, tx, id, write.ExpectedRevision);
            if (old is not null) EnsureNoArchiveLease(db, tx, old.Id);
            if (old is not null && old.Details.DeviceId != write.Details.DeviceId) throw new ArgumentException("安装不能通过编辑换设备，请明确登记另一设备的安装。");
            var changedPath = old is null || InstallationRules.PathKey(old.Details.RootPath) != InstallationRules.PathKey(write.Details.RootPath);
            var now = DateTimeOffset.UtcNow;
            var result = new Installation(old?.Id ?? "installation-" + Guid.NewGuid().ToString("N"), write.Details,
                changedPath ? "unverified" : old!.Status, changedPath ? null : old!.ObservedAt, (old?.Revision ?? 0) + 1, old?.CreatedAt ?? now, now);
            WriteInstallation(db, tx, result); return result;
        });
    }
    public Installation ObserveInstallation(string id, InstallationObservation observation, string key) => Mutate("installation/observe/" + id, key, observation, (db, tx) =>
    {
        if (observation.Status is not ("present" or "missing" or "unverified") || observation.ObservedAt == default || observation.ObservedAt > DateTimeOffset.UtcNow.AddMinutes(5)) throw new ArgumentException("请提交客户端实际核对状态和有效时间。");
        var before = ActiveInstallation(db, tx, id, observation.ExpectedRevision);
        EnsureNoArchiveLease(db, tx, id);
        if (before.ObservedAt > observation.ObservedAt) throw new PlayConflictException("较旧核对不能覆盖新状态。");
        var result = before with { Status = observation.Status, ObservedAt = observation.ObservedAt, Revision = before.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        WriteInstallation(db, tx, result); return result;
    });
    public Installation SetInstallationRemoved(string id, RevisionRequest revision, bool removed, string key) => Mutate("installation/state/" + id, key, new { revision, removed }, (db, tx) =>
    {
        var before = Entity<Installation>(db, tx, "installations", id) ?? throw new ArgumentException("安装不存在。");
        EnsureNoArchiveLease(db, tx, id);
        if (before.Revision != revision.ExpectedRevision || before.MergedIntoId is not null) throw new PlayConflictException("修订冲突或安装已并入完整容器。");
        var now = DateTimeOffset.UtcNow;
        var result = before with { RemovedAt = removed ? now : null, Revision = before.Revision + 1, UpdatedAt = now };
        WriteInstallation(db, tx, result); return result;
    });
    public InstallationComponent SaveComponent(string installationId, ComponentWrite write, string? id, string key)
    {
        InstallationRules.Component(write.Details);
        return Mutate("component/save/" + (id ?? "new"), key, new { installationId, write }, (db, tx) =>
        {
            var installation = Entity<Installation>(db, tx, "installations", installationId) ?? throw new ArgumentException("安装不存在。");
            EnsureNoArchiveLease(db, tx, installationId);
            if (installation.RemovedAt is not null || Item(db, tx, write.Details.ItemId).DeletedAt is not null) throw new PlayConflictException("请先恢复安装／书架条目。");
            var old = id is null ? null : Entity<InstallationComponent>(db, tx, "installation_components", id) ?? throw new ArgumentException("组件不存在。");
            if (old is not null && (old.Revision != write.ExpectedRevision || old.InstallationId != installationId)) throw new PlayConflictException("组件已更新或移至完整容器。");
            var details = write.Details with { RelativePath = InstallationRules.Relative(write.Details.RelativePath) };
            if (Rows<InstallationComponent>(db, tx, "installation_components").Any(c => c.InstallationId == installationId && c.Id != id && c.Details == details)) throw new PlayConflictException("相同组件已存在。");
            var now = DateTimeOffset.UtcNow; var result = new InstallationComponent(old?.Id ?? "component-" + Guid.NewGuid().ToString("N"), installationId, details, (old?.Revision ?? 0) + 1, old?.CreatedAt ?? now, now);
            WriteComponent(db, tx, result); return result;
        });
    }
    public LegacyLocationLink ResolveLegacyLocation(string kind, string id, LegacyLocationDecision decision, string key) => Mutate("legacy/resolve/" + kind + "/" + id, key, decision, (db, tx) =>
    {
        var old = LegacyLink(db, tx, kind, id) ?? throw new ArgumentException("旧位置不存在。");
        if (old.Revision != decision.ExpectedRevision) throw new PlayConflictException("旧位置映射已改变。");
        var component = Entity<InstallationComponent>(db, tx, "installation_components", decision.ComponentId) ?? throw new ArgumentException("组件不存在。");
        var installation = Entity<Installation>(db, tx, "installations", decision.InstallationId) ?? throw new ArgumentException("安装不存在。");
        if (component.InstallationId != installation.Id || installation.RemovedAt is not null) throw new ArgumentException("请选择有效安装内的组件。");
        string machine;
        using (var source = Command(db, tx, kind == "archive" ? "SELECT machine FROM archive_locations WHERE id=$id" : "SELECT machine FROM local_games WHERE id=$id", ("$id", id)))
            machine = source.ExecuteScalar() as string ?? throw new ArgumentException("旧来源不存在。");
        using var device = Command(db, tx, "SELECT device_id FROM device_legacy_names WHERE machine_key=$machine", ("$machine", MachineKey(machine)));
        if (device.ExecuteScalar() as string != installation.Details.DeviceId) throw new PlayConflictException("旧来源和目标不属于同一设备；请先明确认领旧设备。");
        var result = old with { InstallationId = installation.Id, ComponentId = component.Id, State = "mapped", Basis = "人工明确核对；原始证据保留。", Revision = old.Revision + 1 };
        WriteLegacyLink(db, tx, result); return result;
    });
}
