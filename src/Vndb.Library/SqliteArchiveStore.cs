using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public ArchiveLocation RegisterArchive(ArchiveRegistration registration)
    {
        var id = ArchiveRules.Validate(registration, Get(registration.NovelId));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var find = Command(connection, transaction, "SELECT location_json FROM archive_locations WHERE id=$id", ("$id", id));
        var existing = find.ExecuteScalar() is string old ? JsonSerializer.Deserialize<ArchiveLocation>(old, VndbJson.Options) : null;
        GuardLegacyArchive(connection, transaction, existing, registration);
        if (existing is not null && existing.Registration.NovelId != registration.NovelId)
            throw new ArgumentException("同一机器原路径已经绑定其他作品，请先核对身份。");
        if (existing is not null && existing.Registration.PlanSha256 == registration.PlanSha256 &&
            existing.Registration.State != "planned" && registration.State == "planned")
            throw new ArgumentException("此批次已经归档或复原，不能降回等待归档状态。");
        if (existing?.Registration == registration) { transaction.Commit(); return existing; }
        var now = DateTimeOffset.UtcNow;
        var location = new ArchiveLocation(id, registration, existing?.CreatedAt ?? now, now);
        var json = JsonSerializer.Serialize(location, VndbJson.Options);
        Execute(connection, transaction, """
            INSERT INTO archive_locations VALUES($id,$novel,$producer,$machine,$original,$current,$run,$hash,$state,$json,$created,$updated)
            ON CONFLICT(id) DO UPDATE SET selected_producer_id=excluded.selected_producer_id,current_path=excluded.current_path,
                run_id=excluded.run_id,plan_sha256=excluded.plan_sha256,state=excluded.state,location_json=excluded.location_json,updated_at=excluded.updated_at;
            """, ("$id", id), ("$novel", registration.NovelId), ("$producer", registration.SelectedProducerId),
            ("$machine", registration.Machine), ("$original", registration.OriginalPath), ("$current", registration.CurrentPath),
            ("$run", registration.RunId), ("$hash", registration.PlanSha256), ("$state", registration.State), ("$json", json),
            ("$created", location.CreatedAt.ToString("O")), ("$updated", now.ToString("O")));
        Execute(connection, transaction, "INSERT INTO archive_events(location_id,recorded_at,state,snapshot_json) VALUES($id,$at,$state,$json)",
            ("$id", id), ("$at", now.ToString("O")), ("$state", registration.State), ("$json", json));
        ApplyLegacyArchive(connection, transaction, location);
        transaction.Commit();
        return location;
    }

    public IReadOnlyList<ArchiveLocation> Archives(string? novelId = null, string? runId = null)
    {
        if (novelId is not null) novelId = VndbId.Normalize(novelId, 'v');
        using var connection = Open();
        using var command = Command(connection, null, "SELECT location_json FROM archive_locations WHERE ($vn='' OR novel_id=$vn) AND ($run='' OR run_id=$run) ORDER BY machine,current_path",
            ("$vn", novelId ?? ""), ("$run", runId ?? ""));
        using var reader = command.ExecuteReader();
        var result = new List<ArchiveLocation>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<ArchiveLocation>(reader.GetString(0), VndbJson.Options)
            ?? throw new InvalidDataException("归档记录损坏。"));
        return result.Select(ProjectArchive).ToArray();
    }
}
