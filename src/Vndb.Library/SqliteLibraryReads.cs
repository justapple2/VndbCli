using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    // Only fixed operation labels and numeric measurements; no query text, identities or private data.
    private sealed class LibraryReadScope : IDisposable
    {
        private static readonly Meter Meter = new("Vndb.Library.Reads");
        private static readonly Counter<long> Commands = Meter.CreateCounter<long>("vndb.library.read.commands");
        private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("vndb.library.read.duration", "ms");
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly string _operation;
        private readonly SqliteConnection _db;
        private readonly SqliteTransaction _tx;
        public LibraryReadScope(SqliteLibraryStore store, string operation)
        { _operation = operation; _db = store.Open(); _tx = _db.BeginTransaction(deferred: true); }
        public SqliteCommand Query(string sql, params (string Name, object? Value)[] parameters)
        {
            Commands.Add(1, new KeyValuePair<string, object?>("operation", _operation));
            return Command(_db, _tx, sql, parameters);
        }
        public SqliteCommand ForIds(string sql, IReadOnlyList<string> ids) => Query(
            sql.Replace("{ids}", string.Join(',', ids.Select((_, n) => "$id" + n)), StringComparison.Ordinal),
            ids.Select((id, n) => ("$id" + n, (object?)id)).ToArray());
        public void Dispose()
        { _tx.Dispose(); _db.Dispose(); Duration.Record(_watch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("operation", _operation)); }
    }

    public ItemPage ReadItems(string? query = null, bool deleted = false, bool unmatched = false, int page = 1, int pageSize = 24, string? progress = null, string? status = null, bool? isLocal = null)
    {
        LibraryRules.ValidatePage(page, pageSize);
        if (!string.IsNullOrEmpty(progress) && progress is not ("played" or "unplayed" or "unrecorded" or "review") ||
            !string.IsNullOrEmpty(status) && !Vndb.Core.PersonalRules.States.Contains(status)) throw new ArgumentException("无效的个人状态筛选。");
        using var read = new LibraryReadScope(this, "items"); var rows = new List<LibraryItem>();
        using (var select = read.Query("SELECT record_json FROM library_items"))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
            {
                var item = Read<LibraryItem>(reader.GetString(0));
                if ((item.DeletedAt is not null) == deleted && (!unmatched || item.Details.NovelId is null) &&
                    (string.IsNullOrEmpty(query) || string.Join('\n', item.Details.Title, item.Details.TitleOverride, item.Details.CompanyHint, item.Details.Notes).Contains(query, StringComparison.OrdinalIgnoreCase))) rows.Add(item);
            }
        var states = ReadItemStates(read);
        rows = rows.Where(i => Matches(states[i.Id].State, progress, status, isLocal)).ToList();
        var selected = rows.OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.Id, StringComparer.Ordinal).Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return new(ProjectItems(read, selected, states), page, pageSize, rows.Count, (long)page * pageSize < rows.Count);
    }
    public LibraryItemView? ReadItem(string id)
    {
        using var read = new LibraryReadScope(this, "item");
        using var select = read.Query("SELECT record_json FROM library_items WHERE id=$id", ("$id", id));
        return select.ExecuteScalar() is string json ? ProjectItems(read, [Read<LibraryItem>(json)])[0] : null;
    }
    private static IReadOnlyList<LibraryItemView> ProjectItems(LibraryReadScope read, IReadOnlyList<LibraryItem> items, IReadOnlyDictionary<string, StateRow>? states = null)
    {
        if (items.Count == 0) return [];
        var metadata = new Dictionary<string, MetadataSnapshot>(StringComparer.Ordinal);
        var novels = items.Select(i => i.Details.NovelId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (novels.Length > 0)
        {
            using var select = read.ForIds("SELECT id,entry_json FROM novels WHERE id IN ({ids})", novels);
            using var reader = select.ExecuteReader();
            while (reader.Read()) metadata.Add(reader.GetString(0), MetadataImports.Snapshot(Deserialize(reader.GetString(1))));
        }
        states ??= ReadItemStates(read, items.Select(i => i.Id).ToArray());
        return items.Select(i => new LibraryItemView(i, i.Details.NovelId is { } vn ? metadata.GetValueOrDefault(vn) : null,
            states[i.Id].Count, states[i.Id].State)).ToArray();
    }

    public IReadOnlyList<PersonalPlayView> ReadPersonalPlays(string? itemId = null, bool deleted = false)
    {
        using var read = new LibraryReadScope(this, "plays");
        return ReadPersonalPlays(read, "WHERE ($item IS NULL OR l.item_id=$item) AND (json_extract(p.record_json,'$.deleted_at') IS NOT NULL)=$deleted ORDER BY p.updated_at DESC,p.id",
            [("$item", itemId is { Length: 0 } ? null : itemId), ("$deleted", deleted)]);
    }
    public PersonalPlayView? ReadPersonalPlay(string id)
    {
        using var read = new LibraryReadScope(this, "play");
        return ReadPersonalPlays(read, "WHERE p.id=$id", [("$id", id)]).SingleOrDefault();
    }
    private static IReadOnlyList<PersonalPlayView> ReadPersonalPlays(LibraryReadScope read, string filter, (string Name, object? Value)[] parameters)
    {
        var rows = new List<(PlayRecord Play, string? ItemId)>();
        using (var select = read.Query("SELECT p.record_json,l.item_id FROM play_records p LEFT JOIN play_item_links l ON l.play_id=p.id " + filter, parameters))
        using (var reader = select.ExecuteReader()) while (reader.Read()) rows.Add((ReadPlay(reader.GetString(0)), reader.IsDBNull(1) ? null : reader.GetString(1)));
        var times = ReadTimes(read, rows.Select(r => r.Play).ToArray());
        return rows.Select(r => PersonalView(r.Play, times[r.Play.Id], r.ItemId)).ToArray();
    }
    private static PersonalPlayView PersonalView(PlayRecord p, PlayTimeSummary time, string? itemId) => new(p.Id,
        new(p.Details.Title, p.Details.NovelId, p.Details.Status, p.Details.StartedOn, p.Details.FinishedOn, p.Details.DurationMinutes,
            p.Details.Score, p.Details.Review, p.Details.Notes, p.Details.ExtraNotes, p.Details.Language), p.Revision, p.CreatedAt, p.UpdatedAt, p.DeletedAt, time, itemId);
    private static IReadOnlyDictionary<string, PlayTimeSummary> ReadTimes(LibraryReadScope read, IReadOnlyList<PlayRecord> records)
    {
        var sessions = new List<PlaySession>(); var adjustments = new List<TimeAdjustment>(); var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var ids in records.Select(p => p.Id).Distinct(StringComparer.Ordinal).Chunk(400))
        {
            using (var select = read.ForIds("SELECT record_json FROM play_sessions WHERE play_id IN ({ids})", ids))
            using (var reader = select.ExecuteReader()) while (reader.Read()) sessions.Add(Read<PlaySession>(reader.GetString(0)));
            using (var select = read.ForIds("SELECT record_json FROM play_time_adjustments WHERE play_id IN ({ids})", ids))
            using (var reader = select.ExecuteReader()) while (reader.Read()) adjustments.Add(Read<TimeAdjustment>(reader.GetString(0)));
            using (var select = read.ForIds("SELECT play_id,revision FROM play_time_heads WHERE play_id IN ({ids})", ids))
            using (var reader = select.ExecuteReader()) while (reader.Read()) revisions.Add(reader.GetString(0), reader.GetInt64(1));
        }
        var bySession = sessions.ToLookup(s => s.Sample.PlayId, StringComparer.Ordinal); var byAdjustment = adjustments.ToLookup(a => a.PlayId, StringComparer.Ordinal);
        return records.ToDictionary(p => p.Id, p => Summarize(p, bySession[p.Id].ToArray(), byAdjustment[p.Id].ToArray(), revisions.GetValueOrDefault(p.Id)), StringComparer.Ordinal);
    }
}
