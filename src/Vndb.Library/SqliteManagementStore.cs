using System.Globalization;
using Microsoft.Data.Sqlite;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    private T Mutate<T>(string scope, string key, object payload, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 160 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("写入需要 1–160 字符的 Idempotency-Key（字母、数字、.-_）。");
        var hash = PlayRules.Hash(System.Text.Encoding.UTF8.GetBytes(Json(payload)));
        using var db = Open(); using var tx = db.BeginTransaction();
        using var find = Command(db, tx, "SELECT payload_sha,result_json FROM operation_receipts WHERE scope=$scope AND key=$key", ("$scope", scope), ("$key", key));
        using (var reader = find.ExecuteReader())
            if (reader.Read())
            {
                if (reader.GetString(0) != hash) throw new PlayConflictException("同一操作键对应不同内容，未执行写入。");
                return Read<T>(reader.GetString(1));
            }
        var result = action(db, tx);
        Execute(db, tx, "INSERT INTO operation_receipts VALUES($scope,$key,$hash,$result)", ("$scope", scope), ("$key", key), ("$hash", hash), ("$result", Json(result)));
        tx.Commit(); return result;
    }
    private static LibraryItem Item(SqliteConnection db, SqliteTransaction? tx, string id)
    {
        using var command = Command(db, tx, "SELECT record_json FROM library_items WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? Read<LibraryItem>(json) : throw new ArgumentException("个人书架条目不存在。");
    }
    public LibraryItem? GetItem(string id)
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM library_items WHERE id=$id", ("$id", id));
        return command.ExecuteScalar() is string json ? Read<LibraryItem>(json) : null;
    }
    public IReadOnlyList<LibraryItem> PersonalItems()
    {
        using var db = Open(); using var command = Command(db, null, "SELECT record_json FROM library_items ORDER BY id");
        using var reader = command.ExecuteReader(); var rows = new List<LibraryItem>();
        while (reader.Read()) rows.Add(Read<LibraryItem>(reader.GetString(0)));
        return rows;
    }
    public IReadOnlyList<PlayRecord> ItemPlays(string? itemId = null, bool deleted = false)
    {
        using var db = Open();
        using var command = Command(db, null, "SELECT p.record_json FROM play_records p LEFT JOIN play_item_links l ON l.play_id=p.id WHERE ($item='' OR l.item_id=$item) ORDER BY p.updated_at DESC", ("$item", itemId ?? ""));
        using var reader = command.ExecuteReader(); var rows = new List<PlayRecord>();
        while (reader.Read()) { var p = ReadPlay(reader.GetString(0)); if ((p.DeletedAt is not null) == deleted) rows.Add(p); }
        return rows;
    }
    private void ValidateItem(LibraryItemData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(data.Title) || data.Title.Length > 500 ||
            new[] { data.Title, data.CompanyHint, data.TitleOverride, data.CompanyOverride }.Any(t => t?.Length > 500 || t?.Any(char.IsControl) == true) ||
            data.Notes?.Length > 10000 || data.ExtraNotes?.Length > 10000) throw new ArgumentException("书架标题／会社最多500字，备注最多10000字。");
        if (data.NovelId is { } id && (id != VndbId.Normalize(id, 'v') || Get(id) is null)) throw new ArgumentException("请先收录所关联的 VNDB 资料，或保留为未关联条目。");
    }
    public LibraryItem SaveItem(ItemWrite edit, string? id, string key)
    {
        ValidateItem(edit.Details);
        return Mutate("item/save/" + (id ?? "new"), key, edit, (db, tx) =>
        {
            var before = id is null ? null : Item(db, tx, id);
            if (before is not null && (before.Revision != edit.ExpectedRevision || before.DeletedAt is not null)) throw new PlayConflictException("条目已更新或移除，请重新载入。");
            if (before is not null && before.Details.NovelId != edit.Details.NovelId) throw new ArgumentException("修正 VNDB 关联请使用独立关联入口。");
            var now = DateTimeOffset.UtcNow;
            var item = new LibraryItem(before?.Id ?? "item-" + Guid.NewGuid().ToString("N"), edit.Details,
                (before?.Revision ?? 0) + 1, before?.CreatedAt ?? now, now);
            WriteItem(db, tx, item, before is null ? "item-create" : "item-edit"); return item;
        });
    }
    public LibraryItem LinkItem(string id, ItemLink link, string key)
    {
        if (link.NovelId is { } vn && (VndbId.Normalize(vn, 'v') != vn || Get(vn) is null)) throw new ArgumentException("请先收录所选 VNDB 资料。");
        return Mutate("item/link/" + id, key, link, (db, tx) => LinkItemCore(db, tx, id, link));
    }
    private static LibraryItem LinkItemCore(SqliteConnection db, SqliteTransaction tx, string id, ItemLink link)
    {
        var before = Item(db, tx, id);
        if (before.Revision != link.ExpectedRevision || before.DeletedAt is not null) throw new PlayConflictException("条目已更新或移除，请重新载入后确认关联。");
        var result = before with { Details = before.Details with { NovelId = link.NovelId }, Revision = before.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow };
        WriteItem(db, tx, result, "item-link");
        var plays = new List<PlayRecord>();
        using (var command = Command(db, tx, "SELECT p.record_json FROM play_records p JOIN play_item_links l ON l.play_id=p.id WHERE l.item_id=$id", ("$id", id)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) plays.Add(ReadPlay(reader.GetString(0)));
        foreach (var play in plays)
            if (play.Details.NovelId != link.NovelId)
                WritePlay(db, tx, play with { Details = play.Details with { NovelId = link.NovelId }, Revision = play.Revision + 1, UpdatedAt = result.UpdatedAt }, "item-link");
        return result;
    }
    public LibraryItem SetItemRemoved(string id, RevisionRequest revision, bool removed, string key) =>
        Mutate("item/state/" + id, key, new { revision, removed }, (db, tx) =>
        {
            var old = Item(db, tx, id);
            if (old.Revision != revision.ExpectedRevision) throw new PlayConflictException("条目修订不一致，请重新载入。");
            if ((old.DeletedAt is not null) == removed) return old;
            var now = DateTimeOffset.UtcNow;
            var result = old with { DeletedAt = removed ? now : null, Revision = old.Revision + 1, UpdatedAt = now };
            WriteItem(db, tx, result, removed ? "item-remove" : "item-restore"); return result;
        });
    public PlayRecord SavePersonalPlay(PersonalPlayWrite edit, string? id, string key)
    {
        var d = edit.Details ?? throw new ArgumentException("缺少游玩数据。");
        return Mutate("play/save/" + (id ?? "new"), key, edit, (db, tx) =>
        {
            var details = new PlayDetails(d.Title, d.NovelId, d.Status, d.StartedOn, d.FinishedOn, d.DurationMinutes, d.Score, d.Review, d.Notes, d.ExtraNotes, d.Language);
            if (edit.ItemId is { } itemId)
            {
                var item = Item(db, tx, itemId);
                if (item.DeletedAt is not null) throw new PlayConflictException("请先恢复书架条目。");
                details = details with { NovelId = item.Details.NovelId };
            }
            PlayRules.Validate(details, Get);
            PlayRecord? old = null;
            if (id is not null)
            {
                using var get = Command(db, tx, "SELECT record_json FROM play_records WHERE id=$id", ("$id", id));
                old = get.ExecuteScalar() is string json ? ReadPlay(json) : throw new ArgumentException("游玩记录不存在。");
                if (old.Revision != edit.ExpectedRevision || old.DeletedAt is not null) throw new PlayConflictException("游玩记录已更新或移除。");
            }
            var now = DateTimeOffset.UtcNow;
            var result = new PlayRecord(old?.Id ?? "play-" + Guid.NewGuid().ToString("N"), details, old?.Source,
                (old?.Revision ?? 0) + 1, old?.CreatedAt ?? now, now);
            WritePlay(db, tx, result, old is null ? "create" : "edit", edit.ItemId);
            if (edit.ItemId is { } parent)
            {
                var item = Item(db, tx, parent);
                if (item.Details.NovelId != details.NovelId || item.DeletedAt is not null) throw new PlayConflictException("书架条目已经改变。");
                Execute(db, tx, "UPDATE play_item_links SET item_id=$item WHERE play_id=$play", ("$item", parent), ("$play", result.Id));
            }
            return result;
        });
    }
    public PlayRecord SetPlayRemoved(string id, RevisionRequest revision, bool removed, string key) =>
        Mutate("play/state/" + id, key, new { revision, removed }, (db, tx) =>
        {
            using var get = Command(db, tx, "SELECT record_json FROM play_records WHERE id=$id", ("$id", id));
            var old = get.ExecuteScalar() is string json ? ReadPlay(json) : throw new ArgumentException("游玩记录不存在。");
            if (old.Revision != revision.ExpectedRevision) throw new PlayConflictException("游玩修订不一致，请重新载入。");
            if ((old.DeletedAt is not null) == removed) return old;
            var now = DateTimeOffset.UtcNow;
            var result = old with { DeletedAt = removed ? now : null, Revision = old.Revision + 1, UpdatedAt = now };
            WritePlay(db, tx, result, removed ? "remove" : "restore"); return result;
        });
    public MetadataProvenance? Provenance(string id)
    {
        using var db = Open(); using var command = Command(db, null, "SELECT source,fetched_at,completeness,revision,batch_id,verified_at FROM metadata_provenance WHERE vndb_id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(id, reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetString(2), reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture)) : null;
    }
}
