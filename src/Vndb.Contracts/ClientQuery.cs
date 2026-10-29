using Vndb.Core;

namespace Vndb.Contracts;

public sealed record ClientQuery(string Kind, MetadataQuery Request, string? Id = null)
{
    public static ClientQuery FromText(string text, int pageSize = 10)
    {
        var query = CatalogQuery.FromText(text, pageSize);
        return new(query.Kind, query.ToMetadata(), query.Id);
    }
    public ClientQuery Validate()
    {
        var query = Request.ToCatalog(Kind, Id).Validate();
        return this with { Request = query.ToMetadata(), Id = query.Id };
    }
}
public sealed class OfflineCacheMissException : Exception
{
    public OfflineCacheMissException() : base("网络不可达且本机没有此查询的快照；未知结果不能视为空结果。") { }
}
