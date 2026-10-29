using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private static void GuardLegacyArchive(SqliteConnection db, SqliteTransaction tx, ArchiveLocation? before, ArchiveRegistration next)
    {
        if (before is null || LegacyLink(db, tx, "archive", before.Id)?.ComponentId is not { } componentId) return;
        var component = Entity<InstallationComponent>(db, tx, "installation_components", componentId)!;
        EnsureNoArchiveLease(db, tx, component.InstallationId);
        var actual = InstallationRules.PathKey(ComponentPath(db, tx, component));
        var old = InstallationRules.PathKey(ActualPath(before.Registration)); var proposed = InstallationRules.PathKey(ActualPath(next));
        if (actual != proposed && (actual != old || component.Details.RelativePath != ".")) throw new PlayConflictException("安装位置已通过新接口修改或属于完整容器，请使用安装修订接口，不回退旧路径。");
    }
    private static void ApplyLegacyArchive(SqliteConnection db, SqliteTransaction tx, ArchiveLocation archive)
    {
        var link = LegacyLink(db, tx, "archive", archive.Id);
        if (link is null) { SeedArchive(db, tx, archive); return; }
        if (link.ComponentId is not { } componentId) return;
        var component = Entity<InstallationComponent>(db, tx, "installation_components", componentId)!;
        var installation = Entity<Installation>(db, tx, "installations", component.InstallationId)!;
        if (InstallationRules.PathKey(ComponentPath(db, tx, component)) == InstallationRules.PathKey(ActualPath(archive.Registration))) return;
        var r = archive.Registration;
        WriteInstallation(db, tx, installation with { Details = installation.Details with { RootPath = ActualPath(r) },
            Status = r.State == "planned" ? "unverified" : "recorded", ObservedAt = r.State == "planned" ? null : r.VerifiedAt,
            Revision = installation.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow });
    }
    private static LocalGame LinkLegacyLocal(SqliteConnection db, SqliteTransaction tx, LocalGame before, string novelId, long revision)
    {
        var link = LegacyLink(db, tx, "local", before.Id);
        if (link?.ComponentId is not { } id) throw new PlayConflictException("请先核对旧目录映射，再关联作品。");
        var component = Entity<InstallationComponent>(db, tx, "installation_components", id)!;
        if (component.Revision != revision) throw new PlayConflictException("安装组件已更新，请重新载入。");
        var item = Item(db, tx, component.Details.ItemId);
        if (item.Details.NovelId is { } current && current != novelId) throw new PlayConflictException("组件已有不同作品关联，请使用明确的组件修正入口。");
        if (item.Details.NovelId != novelId)
        {
            LinkItemCore(db, tx, item.Id, new(novelId, item.Revision));
            component = component with { Revision = component.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            WriteComponent(db, tx, component);
        }
        return before with { Details = before.Details with { Path = ComponentPath(db, tx, component) }, NovelId = novelId, Revision = component.Revision, UpdatedAt = component.UpdatedAt };
    }
}
