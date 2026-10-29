using Vndb.Contracts;

namespace Vndb.Library;

/// <summary>Read-only data quality projection. Client paths are opaque; only NAS cover files are inspected.</summary>
public sealed class QualityReview(SqliteLibraryStore store, CatalogEnricher catalog)
{
    public QualityPage Read(string? deviceId = null, string? kind = null, string? query = null, int page = 1, int pageSize = 30)
    {
        LibraryRules.ValidatePage(page, pageSize);
        var allItems = store.PersonalItems().Where(i => i.DeletedAt is null).ToDictionary(i => i.Id);
        var installations = store.Installations().Where(i => i.RemovedAt is null && i.MergedIntoId is null).ToArray();
        var components = store.Components(); var issues = new List<QualityIssue>();
        var localItems = components.Where(c => installations.Any(i => i.Id == c.InstallationId && (deviceId is null || i.Details.DeviceId == deviceId)))
            .Select(c => c.Details.ItemId).ToHashSet(StringComparer.Ordinal);
        localItems.UnionWith(store.PresentItemIds(deviceId));
        var played = store.ItemPlays(); var playedNovelIds = played.Where(p => p.HasPlayed && p.Details.NovelId is not null).Select(p => p.Details.NovelId!).ToHashSet(StringComparer.Ordinal);
        foreach (var item in allItems.Values.Where(i => deviceId is null || localItems.Contains(i.Id)))
            if (item.Details.NovelId is null)
                issues.Add(new("unlinked", "item", item.Id, item.Details.Title, "尚未确认VNDB关联；不能以首个模糊结果代替。", "manage", DeviceId: deviceId));
        var relevant = allItems.Values.Where(i => deviceId is null || localItems.Contains(i.Id)).Select(i => i.Details.NovelId)
            .Concat(deviceId is null ? playedNovelIds : []).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var id in relevant)
        {
            var entry = store.Get(id);
            if (entry is null) { issues.Add(new("metadata_missing", "novel", id, id, "已关联作品缺少本地公开资料。", "query", id)); continue; }
            if (entry.Novel.Developers.Count == 0 || entry.Producers.Count < entry.Novel.Developers.Count)
                issues.Add(new("company_missing", "novel", id, entry.Novel.Title, "开发会社或会社详情缺失；不使用发行商／引擎商替代。", "query", id));
            if (entry.Novel.Image is not null && (entry.Cover is null || !File.Exists(LibraryRules.CoverPath(store.DataDirectory, entry.Cover))))
                issues.Add(new("cover_missing", "novel", id, entry.Novel.Title, "上游有封面，本地封面尚未就绪。", "query", id));
        }
        foreach (var row in catalog.Rows().Where(r => r.Status != "matched" && (deviceId is null || r.Source.LocalIds.Count + r.Source.ArchiveIds.Count + (r.Source.ItemIds?.Count ?? 0) > 0)))
            issues.Add(new("catalog_" + row.Status, "catalog", row.Source.Key, row.Source.Title,
                row.Status == "not_found" ? "当前检索未找到；可补充原名／文件属性线索后重查。" : "候选需要人工核对，不自动改关联。", "catalog"));
        foreach (var installation in installations.Where(i => deviceId is null || i.Details.DeviceId == deviceId))
        {
            var related = components.Where(c => c.InstallationId == installation.Id).ToArray();
            void Add(string code, string message) => issues.Add(new(code, "installation", installation.Id, installation.Details.Title,
                message, "installations", DeviceId: installation.Details.DeviceId, InstallationId: installation.Id));
            if (installation.Status != "present") Add(installation.Status == "missing" ? "path_missing" : "observation_needed",
                installation.Status == "missing" ? "上次本机核对目录不存在；不会删除记录或游戏。" : "历史位置或待核对；请由所属客户端重新核对，NAS不检查此路径。");
            if (related.Length == 0) Add("components_missing", "完整安装尚未关联作品组件。");
            if (related.Any(c => !allItems.ContainsKey(c.Details.ItemId))) Add("component_item_removed", "组件关联的个人条目已移除，请核对或恢复。");
        }
        foreach (var link in store.LegacyLocations().Where(l => l.State != "mapped"))
            issues.Add(new("legacy_unmapped", "legacy", link.LegacyId, link.SourceKind, link.Basis, "installations"));
        var counts = issues.GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var filtered = issues.Where(i => (kind is null || i.Kind == kind) && (string.IsNullOrWhiteSpace(query) ||
            (i.Title + "\n" + i.NovelId).Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.EntityId, StringComparer.Ordinal).ToArray();
        return new(filtered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), counts, filtered.Length, page, pageSize, (long)page * pageSize < filtered.Length);
    }
}
