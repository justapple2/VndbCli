using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    // Cache only successful negotiation. A failed/authenticated request never means "old server".
    private ServiceInfo? _readInfo;
    private async Task<bool> SupportsReadAsync(string capability, CancellationToken ct)
    {
        _readInfo ??= await OrganizationInfoAsync(ct);
        if (_readInfo.Capabilities is null || _readInfo.ApiVersions is null || !_readInfo.ApiVersions.Contains(1)) throw new InvalidDataException("Web查询能力声明无效。");
        return _readInfo.Capabilities.Contains(capability, StringComparer.Ordinal);
    }
    public async Task<ShelfSummaryPage?> ShelfSummaryAsync(int page, bool deleted, CancellationToken ct = default)
    {
        if (!await SupportsReadAsync("shelf_summary_v1", ct)) return null;
        var result = await SendAsync<ShelfSummaryPage>(HttpMethod.Get, $"api/v1/shelf?page={page}&page_size=100&deleted={deleted.ToString().ToLowerInvariant()}", null, ct);
        ShelfSummaries.RequirePage(result, page, deleted); return result;
    }
    public async Task<ItemHeadPage?> ItemHeadsAsync(int page, bool deleted, CancellationToken ct = default)
    {
        if (!await SupportsReadAsync("item_heads_v1", ct)) return null;
        return await SendAsync<ItemHeadPage>(HttpMethod.Get, $"api/v1/item-heads?page={page}&page_size=100&deleted={deleted.ToString().ToLowerInvariant()}", null, ct);
    }
}
