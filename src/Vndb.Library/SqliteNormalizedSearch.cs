using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    // Search association titles, not full play histories, paths or per-row installation projections.
    internal HashSet<string> AssociatedNovelIds(string key)
    {
        using var read = new LibraryReadScope(this, "search-associations");
        using var select = read.Query("""
            SELECT json_extract(p.record_json,'$.details.title'),p.novel_id FROM play_records p
            WHERE p.status IN ('playing','played','completed','on_hold','dropped') AND json_extract(p.record_json,'$.deleted_at') IS NULL
            UNION ALL
            SELECT g.title,CASE WHEN l.component_id IS NULL THEN json_extract(g.record_json,'$.novel_id') ELSE i.novel_id END FROM local_games g
            LEFT JOIN installation_legacy_links l ON l.source_kind='local' AND l.legacy_id=g.id
            LEFT JOIN installation_components c ON c.id=l.component_id LEFT JOIN library_items i ON i.id=c.item_id
            UNION ALL
            SELECT coalesce((SELECT t.title FROM novel_titles t WHERE t.novel_id=n.id AND t.main=1 LIMIT 1),n.alt_title,n.title),n.id
            FROM archive_locations a JOIN novels n ON n.id=a.novel_id WHERE a.state<>'planned'
            UNION ALL
            SELECT json_extract(i.record_json,'$.details.title'),i.novel_id FROM library_items i
            WHERE json_extract(i.record_json,'$.deleted_at') IS NULL AND EXISTS(SELECT 1 FROM local_presence p WHERE p.item_id=i.id AND p.is_local=1)
            """);
        using var reader = select.ExecuteReader(); var ids = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            var title = reader.GetString(0); read.ObserveText(title); var id = reader.GetString(1); read.ObserveText(id);
            if (CatalogRules.Key(title).Contains(key, StringComparison.Ordinal)) ids.Add(id);
        }
        return ids;
    }
    // A miss must not page/deserialise every full novel (including release catalogues).
    // Stream only title fields, then materialise the requested page in the same read snapshot.
    internal LibraryPage FindNormalized(string key, IReadOnlySet<string> associated, string? producer, int page, int limit)
    {
        using var read = new LibraryReadScope(this, "normalized-search");
        var filter = producer is null ? "" : " WHERE n.id IN (SELECT novel_id FROM novel_developers WHERE producer_id=$producer)";
        (string, object?)[] parameters = producer is null ? [] : [("$producer", producer)];
        var matches = new HashSet<string>(StringComparer.Ordinal);
        bool Match(string? title) => !string.IsNullOrWhiteSpace(title) && title.Length <= 500 &&
            !title.Any(char.IsControl) && CatalogRules.Key(title).Contains(key, StringComparison.Ordinal);
        using (var select = read.Query("SELECT n.id,n.title,n.alt_title,json_extract(n.entry_json,'$.novel.aliases') FROM novels n" + filter, parameters))
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                for (var column = 0; column < 4; column++) if (!reader.IsDBNull(column)) read.ObserveText(reader.GetString(column));
                if (associated.Contains(id) || Match(reader.GetString(1)) || Match(reader.IsDBNull(2) ? null : reader.GetString(2)))
                { matches.Add(id); continue; }
                if (reader.IsDBNull(3)) continue;
                using var aliases = JsonDocument.Parse(reader.GetString(3));
                if (aliases.RootElement.EnumerateArray().Any(a => Match(a.GetString()))) matches.Add(id);
            }
        }
        using (var select = read.Query("SELECT t.novel_id,t.title,t.latin FROM novel_titles t JOIN novels n ON n.id=t.novel_id" + filter, parameters))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
            {
                for (var column = 0; column < 3; column++) if (!reader.IsDBNull(column)) read.ObserveText(reader.GetString(column));
                if (!matches.Contains(reader.GetString(0)) && (Match(reader.GetString(1)) || Match(reader.IsDBNull(2) ? null : reader.GetString(2))))
                    matches.Add(reader.GetString(0));
            }
        var entries = new List<LibraryEntry>();
        if (matches.Count > 0 && (long)(page - 1) * limit < matches.Count)
        {
            using var select = read.Query(PersonalOrderCte + "SELECT n.entry_json FROM novels n" + PersonalOrderJoin +
                " WHERE n.id IN (SELECT value FROM json_each($matches)) ORDER BY " + PersonalOrder + " LIMIT $limit OFFSET $offset",
                ("$matches", JsonSerializer.Serialize(matches, VndbJson.Options)), ("$limit", limit), ("$offset", (page - 1) * limit));
            using var reader = select.ExecuteReader();
            while (reader.Read()) entries.Add(read.Decode<LibraryEntry>(reader.GetString(0)));
        }
        return new(entries, page, limit, matches.Count, (long)page * limit < matches.Count);
    }
}
