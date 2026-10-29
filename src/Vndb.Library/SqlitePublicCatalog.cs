using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public IReadOnlyList<ProducerCount> CatalogProducers(string query = "")
    {
        if (query.Length > 200 || query.Any(char.IsControl)) throw new ArgumentException("会社筛选无效。");
        using var read = new LibraryReadScope(this, "catalog-producers");
        using var select = read.Query("""
            SELECT p.id,p.name,p.original,count(d.novel_id) total FROM producers p
            LEFT JOIN novel_developers d ON d.producer_id=p.id
            WHERE $q='' OR instr(lower(p.name),$q)>0 OR instr(lower(coalesce(p.original,'')),$q)>0 OR p.id=$q
            GROUP BY p.id ORDER BY total DESC,p.id LIMIT 60
            """, ("$q", query.Trim().ToLowerInvariant()));
        using var reader = select.ExecuteReader(); var rows = new List<ProducerCount>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3)));
        return rows;
    }
    public PublicCatalogPage BrowseCatalog(PublicCatalogQuery input)
    {
        var q = PublicCatalogRules.Validate(input);
        using var read = new LibraryReadScope(this, "public-catalog");
        var (where, parameters) = CatalogPredicates(q);
        int total;
        using (var count = read.Query("SELECT count(*) FROM novels n " + where, parameters)) total = Convert.ToInt32(count.ExecuteScalar());
        var personal = q.Sort == "personal";
        var order = q.Sort switch { "personal" => PersonalOrder, "title" => "n.title COLLATE NOCASE,n.id", "released_desc" => "(n.released IS NULL OR n.released=''),n.released DESC,n.id", "released_asc" => "(n.released IS NULL OR n.released=''),n.released,n.id", _ => "n.updated_at DESC,n.id" };
        var rows = new List<(LibraryEntry Entry, bool Known)>();
        // Large release catalogues/descriptions stay on the explicit detail route, not every card.
        using (var select = read.Query((personal ? PersonalOrderCte : "") + "SELECT json_remove(n.entry_json,'$.novel.release_catalog','$.novel.description','$.novel.relations'),json_type(n.entry_json,'$.novel.release_catalog')='object' FROM novels n " + (personal ? PersonalOrderJoin : "") + where + " ORDER BY " + order + " LIMIT $limit OFFSET $offset",
            [.. parameters, ("$limit", q.PageSize), ("$offset", (q.Page - 1) * q.PageSize)]))
        using (var reader = select.ExecuteReader()) while (reader.Read()) rows.Add((Deserialize(reader.GetString(0)), !reader.IsDBNull(1) && reader.GetBoolean(1)));
        var links = new List<(string Novel, string Item)>();
        if (rows.Count > 0)
        {
            using var select = read.ForIds("SELECT novel_id,id FROM library_items WHERE novel_id IN ({ids}) AND json_extract(record_json,'$.deleted_at') IS NULL", rows.Select(r => r.Entry.Novel.Id).ToArray());
            using var reader = select.ExecuteReader(); while (reader.Read()) links.Add((reader.GetString(0), reader.GetString(1)));
        }
        var states = links.Count == 0 ? [] : ReadItemStates(read, links.Select(l => l.Item).ToArray());
        var items = rows.Select(row =>
        {
            var own = links.Where(l => l.Novel == row.Entry.Novel.Id).Select(l => states[l.Item].State).ToArray();
            return new PublicCatalogEntry(MetadataImports.Snapshot(row.Entry), new(own.Length,
                own.Count(s => s.Progress.State == "played"), own.Count(s => s.Progress.State == "unplayed"),
                own.Count(s => s.Progress.State == "unrecorded"), own.Count(s => s.Progress.State == "review"), own.Any(s => s.IsLocal)), row.Known);
        }).ToArray();
        var jobs = new List<DatasetJob>();
        using (var select = read.Query("SELECT record_json FROM dataset_jobs WHERE state='completed' OR state NOT IN ('completed','cancelled')"))
        using (var reader = select.ExecuteReader()) while (reader.Read()) jobs.Add(Read<DatasetWork>(reader.GetString(0)).Job);
        var active = jobs.FirstOrDefault(j => j.State != "completed");
        using var all = read.Query("SELECT count(*) FROM novels");
        var coverage = new CatalogCoverage(Convert.ToInt32(all.ExecuteScalar()), jobs.Where(j => j.State == "completed").Max(j => j.SnapshotAt), active?.State, active?.Processed ?? 0, active?.Total ?? 0);
        return new(items, q.Page, q.PageSize, total, (long)q.Page * q.PageSize < total, coverage, DateTimeOffset.UtcNow);
    }

    private static (string Where, (string, object?)[] Parameters) CatalogPredicates(PublicCatalogQuery q)
    {
        // Only fixed SQL fragments are composed. Optional OR branches prevent SQLite from
        // driving the query from its selective indexes; values always remain parameters.
        var clauses = new List<string>(); var parameters = new List<(string, object?)>();
        if (q.Query.Length > 0) { clauses.Add("instr(n.search_text,$q)>0"); parameters.Add(("$q", q.Query.ToLowerInvariant())); }
        if (q.ProducerId is not null)
        {
            clauses.Add("n.id IN (SELECT novel_id FROM novel_developers WHERE producer_id=$producer)");
            parameters.Add(("$producer", q.ProducerId));
        }
        var joins = new List<string>();
        if (q.Language.Length > 0)
        {
            joins.Add("CROSS JOIN release_languages l ON l.release_id=nr.release_id AND l.language=$lang" + (q.IncludeMtl ? "" : " AND l.mtl=0"));
            parameters.Add(("$lang", q.Language));
        }
        if (q.Platform.Length > 0)
        {
            joins.Add("CROSS JOIN release_platforms p ON p.release_id=nr.release_id AND p.platform=$platform");
            parameters.Add(("$platform", q.Platform));
        }
        if (q.ReleaseType == "patch") joins.Add("CROSS JOIN releases r ON r.id=nr.release_id AND json_extract(r.metadata_json,'$.patch')=1");
        else if (q.ReleaseType.Length > 0)
        {
            // A bundle's role belongs to this exact novel, not another member of the release.
            joins.Add("CROSS JOIN release_roles v ON v.release_id=nr.release_id AND v.novel_id=nr.novel_id AND v.rtype=$type");
            parameters.Add(("$type", q.ReleaseType));
        }
        // nr must lead each correlated lookup. An unconstrained join order can scan all
        // releases of a language for every novel; CROSS JOIN keeps the existing PK lookup first.
        if (joins.Count > 0) clauses.Add("EXISTS (SELECT 1 FROM novel_releases nr " + string.Join(' ', joins) + " WHERE nr.novel_id=n.id)");
        return (clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses), parameters.ToArray());
    }
}
