using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private sealed record StateRow(ItemState State, int Count, bool Deleted);
    private static Dictionary<string, StateRow> ReadItemStates(LibraryReadScope read, IReadOnlyList<string>? ids = null)
    {
        const string sql = """
            SELECT i.id,CASE WHEN p.id IS NOT NULL AND p.novel_id IS NOT i.novel_id THEN 'association_review' ELSE p.status END progress_status,
                count(p.id),coalesce(local.is_local,0),json_extract(i.record_json,'$.deleted_at') IS NOT NULL
            FROM library_items i
            LEFT JOIN play_item_links l ON l.item_id=i.id
            LEFT JOIN play_records p ON p.id=l.play_id AND json_extract(p.record_json,'$.deleted_at') IS NULL
            LEFT JOIN (SELECT item_id,max(is_local) is_local FROM local_presence GROUP BY item_id) local ON local.item_id=i.id
            {filter}
            GROUP BY i.id,progress_status
            """;
        using var query = ids is null ? read.Query(sql.Replace("{filter}", "")) :
            read.ForIds(sql.Replace("{filter}", "WHERE i.id IN ({ids})"), ids);
        using var reader = query.ExecuteReader();
        var statuses = new Dictionary<string, (List<string> States, int Count, bool Local, bool Deleted)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var prior = statuses.GetValueOrDefault(id, ([], 0, false, false));
            if (!reader.IsDBNull(1)) prior.States.Add(reader.GetString(1));
            statuses[id] = (prior.States, prior.Count + reader.GetInt32(2), reader.GetBoolean(3), reader.GetBoolean(4));
        }
        return statuses.ToDictionary(p => p.Key, p => new StateRow(new(PersonalProgress.FromStatuses(p.Value.States), p.Value.Local), p.Value.Count, p.Value.Deleted), StringComparer.Ordinal);
    }
    public CollectionStatistics CollectionStatistics()
    {
        using var read = new LibraryReadScope(this, "collection");
        var states = ReadItemStates(read).Values.Where(s => !s.Deleted).ToArray();
        using var count = read.Query("SELECT count(*) FROM novels");
        return new(Convert.ToInt32(count.ExecuteScalar()), states.Length,
            states.Count(s => s.State.Progress.State == "played"), states.Count(s => s.State.Progress.State == "unplayed"),
            states.Count(s => s.State.Progress.State == "unrecorded"), states.Count(s => s.State.Progress.State == "review"),
            states.Count(s => s.State.IsLocal));
    }
    private static bool Matches(ItemState state, string? progress, string? status, bool? isLocal) =>
        state.Progress.Matches(progress, status) &&
        (isLocal is null || state.IsLocal == isLocal);
}
