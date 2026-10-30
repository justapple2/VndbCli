namespace Vndb.Library;

public sealed partial class SqliteLibraryStore
{
    // Exact item/play association first, then an explicitly VN-aggregated browse priority.
    // Review/removed/mismatched plays cannot promote a novel as played. No extra cache truth.
    private const string PersonalOrderCte = """
        WITH item_priority AS (
          SELECT i.novel_id,i.id,
            CASE WHEN sum(CASE WHEN p.id IS NOT NULL AND
                (p.novel_id IS NOT i.novel_id OR p.status NOT IN ('not_started','playing','played','completed','on_hold','dropped'))
                THEN 1 ELSE 0 END)=0
              AND max(CASE WHEN p.status IN ('playing','played','completed','on_hold','dropped') THEN 1 ELSE 0 END)=1 THEN 0
              WHEN EXISTS(SELECT 1 FROM local_presence lp WHERE lp.item_id=i.id AND lp.is_local=1) THEN 1 ELSE 2 END tier
          FROM library_items i
          LEFT JOIN play_item_links l ON l.item_id=i.id
          LEFT JOIN play_records p ON p.id=l.play_id AND json_extract(p.record_json,'$.deleted_at') IS NULL
          WHERE i.novel_id IS NOT NULL AND json_extract(i.record_json,'$.deleted_at') IS NULL
          GROUP BY i.id
        ), personal_priority AS (SELECT novel_id,min(tier) tier FROM item_priority GROUP BY novel_id)
        """;
    private const string PersonalOrderJoin = " LEFT JOIN personal_priority priority ON priority.novel_id=n.id ";
    private const string PersonalOrder = "coalesce(priority.tier,2),n.title COLLATE NOCASE,n.id";

}
