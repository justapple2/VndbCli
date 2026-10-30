using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
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
                if (!matches.Contains(reader.GetString(0)) && (Match(reader.GetString(1)) || Match(reader.IsDBNull(2) ? null : reader.GetString(2))))
                    matches.Add(reader.GetString(0));
        var entries = new List<LibraryEntry>();
        if (matches.Count > 0 && (long)(page - 1) * limit < matches.Count)
        {
            using var select = read.Query(PersonalOrderCte + "SELECT n.entry_json FROM novels n" + PersonalOrderJoin +
                " WHERE n.id IN (SELECT value FROM json_each($matches)) ORDER BY " + PersonalOrder + " LIMIT $limit OFFSET $offset",
                ("$matches", JsonSerializer.Serialize(matches, VndbJson.Options)), ("$limit", limit), ("$offset", (page - 1) * limit));
            using var reader = select.ExecuteReader();
            while (reader.Read()) entries.Add(Deserialize(reader.GetString(0)));
        }
        return new(entries, page, limit, matches.Count, (long)page * limit < matches.Count);
    }
}
