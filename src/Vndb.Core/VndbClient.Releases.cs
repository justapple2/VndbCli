namespace Vndb.Core;

public sealed partial class VndbClient
{
    public async Task<ReleaseCatalog> GetNovelReleasesAsync(string id, CancellationToken ct = default)
    {
        id = VndbId.Normalize(id, 'v');
        const string fields = "title,alttitle,released,official,patch,languages{lang,title,latin,mtl,main},platforms,vns{id,rtype},producers{id,name,original,developer,publisher}";
        var items = new List<NovelRelease>();
        long last = 0; long bytes = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            object[] filter = ["vn", "=", new object[] { "id", "=", id }];
            if (last > 0) filter = ["and", filter, new object[] { "id", ">", "r" + last.ToString(System.Globalization.CultureInfo.InvariantCulture) }];
            var page = await QueryAsync<NovelRelease>("release", filter, fields, "id", 25, 1, ct);
            if (page.More && page.Results.Count == 0) throw new System.Text.Json.JsonException("发行目录分页没有进展。");
            foreach (var release in page.Results)
            {
                if (release is null || !long.TryParse(VndbId.Normalize(release.Id, 'r').AsSpan(1), out var current) || current <= last)
                    throw new System.Text.Json.JsonException("发行目录游标无效。");
                bytes += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(release, VndbJson.Options).LongLength;
                if (bytes > ReleaseRules.MaximumBytes) throw new System.Text.Json.JsonException("发行目录超过4MiB，保留已有快照。");
                last = current; items.Add(release);
            }
            if (items.Count > ReleaseRules.MaximumReleases || page.More && items.Count >= ReleaseRules.MaximumReleases)
                throw new System.Text.Json.JsonException("发行目录超过单作品限额，未替换已有目录。");
            if (!page.More) break;
        }
        var catalog = new ReleaseCatalog(items, _time.GetUtcNow());
        try { ReleaseRules.Validate(catalog, id); }
        catch (ArgumentException error) { throw new System.Text.Json.JsonException("VNDB发行目录字段无效。", error); }
        return catalog;
    }
}
