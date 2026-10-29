using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public async Task<PublicCatalogPage> BrowseCatalogAsync(PublicCatalogQuery query, CancellationToken ct)
    {
        var q = PublicCatalogRules.Validate(query);
        var path = $"api/v1/catalog?q={Uri.EscapeDataString(q.Query)}&producer_id={q.ProducerId}&language={Uri.EscapeDataString(q.Language)}&release_type={q.ReleaseType}&platform={q.Platform}&include_mtl={q.IncludeMtl.ToString().ToLowerInvariant()}&sort={q.Sort}&page={q.Page}&page_size={q.PageSize}";
        var result = await SendAsync<PublicCatalogPage>(HttpMethod.Get, path, null, ct);
        if (!PublicCatalogRules.ValidPage(result, q))
            throw new ClientApiException("protocol_invalid", 200);
        return result;
    }
}
