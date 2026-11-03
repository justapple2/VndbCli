using Vndb.Contracts;

namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    public ShelfSummaryPage ReadShelfSummary(string? query = null, bool deleted = false, bool unmatched = false,
        int page = 1, int pageSize = 100, string? progress = null, string? status = null, bool? isLocal = null, bool? favorite = null, string? label = null)
    {
        var result = ReadItemsCore(query, deleted, unmatched, page, pageSize, progress, status, isLocal, true, favorite, label);
        return new(result.Items, result.Page, result.PageSize, result.Total, result.More);
    }
    public ItemHeadPage ReadItemHeads(int page = 1, int pageSize = 100, bool deleted = false)
    {
        LibraryRules.ValidatePage(page, pageSize);
        using var read = new LibraryReadScope(this, "item-heads");
        const string where = " WHERE (json_extract(record_json,'$.deleted_at') IS NOT NULL)=$deleted";
        using var count = read.Query("SELECT count(*) FROM library_items" + where, ("$deleted", deleted));
        var total = Convert.ToInt32(count.ExecuteScalar());
        using var select = read.Query("SELECT id,json_extract(record_json,'$.revision') FROM library_items" + where + " ORDER BY id LIMIT $limit OFFSET $offset",
            ("$deleted", deleted), ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        using var reader = select.ExecuteReader(); var rows = new List<ItemHead>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetInt64(1), deleted));
        return new(rows, page, pageSize, total, (long)page * pageSize < total);
    }
}
