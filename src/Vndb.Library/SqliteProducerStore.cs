using System.Globalization;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public StoredProducer? GetProducer(string id)
    {
        using var read = new LibraryReadScope(this, "producer-get");
        using var command = read.Query("SELECT metadata_json,updated_at FROM producers WHERE id=$id", ("$id", VndbId.Normalize(id, 'p')));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(read.Decode<Producer>(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)) : null;
    }
    internal IReadOnlyList<StoredProducer> GetProducers(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return [];
        using var read = new LibraryReadScope(this, "producer-batch");
        using var command = read.Query("SELECT metadata_json,updated_at FROM producers WHERE id IN (SELECT value FROM json_each($ids))",
            ("$ids", Json(ids.Select(id => VndbId.Normalize(id, 'p')).Distinct().ToArray())));
        using var reader = command.ExecuteReader(); var rows = new List<StoredProducer>();
        while (reader.Read()) rows.Add(new(read.Decode<Producer>(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
        return rows;
    }
    public IReadOnlyList<StoredProducer> FindProducers(string query)
    {
        using var read = new LibraryReadScope(this, "producer-search");
        using var command = read.Query("SELECT metadata_json,updated_at FROM producers ORDER BY id");
        using var reader = command.ExecuteReader();
        var result = new List<StoredProducer>();
        while (reader.Read())
        {
            var producer = read.Decode<Producer>(reader.GetString(0));
            if (ProducerMatches(producer, query)) result.Add(new(producer, DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
        }
        return result;
    }
    internal static bool ProducerMatches(Producer p, string query) => new[] { p.Id, p.Name, p.Original ?? "" }
        .Concat(p.Aliases).Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase));
    public void SaveProducer(StoredProducer entry)
    {
        var p = entry.Producer;
        var id = VndbId.Normalize(p.Id, 'p');
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var previous = Command(connection, transaction, "SELECT metadata_json FROM producers WHERE id=$id", ("$id", id));
        var before = previous.ExecuteScalar() as string;
        Execute(connection, transaction, """
            INSERT INTO producers(id,name,original,kind,lang,metadata_json,updated_at) VALUES($id,$name,$original,$kind,$lang,$json,$updated)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name,original=excluded.original,kind=excluded.kind,
                lang=excluded.lang,metadata_json=excluded.metadata_json,updated_at=excluded.updated_at;
            """, ("$id", id), ("$name", p.Name), ("$original", p.Original), ("$kind", p.Type),
            ("$lang", p.Lang), ("$json", JsonSerializer.Serialize(p, VndbJson.Options)), ("$updated", entry.FetchedAt.ToString("O")));
        WriteProvenance(connection, transaction, id, "web_vndb", entry.FetchedAt, "full_text", null, entry.FetchedAt);
        Event(connection, transaction, id, "producer-save", before, Json(p));
        transaction.Commit();
    }
}
