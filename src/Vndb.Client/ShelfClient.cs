using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<PersonalPlayView> PersonalPlayAsync(string id, CancellationToken ct = default) => SendAsync<PersonalPlayView>(HttpMethod.Get, "api/v1/plays/" + Uri.EscapeDataString(id), null, ct);
    public Task<PersonalPlayView> SavePersonalPlayAsync(PersonalPlayWrite request, string? id, string operationKey, CancellationToken ct = default) =>
        SendAsync<PersonalPlayView>(id is null ? HttpMethod.Post : HttpMethod.Patch, "api/v1/plays" + (id is null ? "" : "/" + Uri.EscapeDataString(id)), request, ct, operationKey);
    public Task<CollectionStatistics> CollectionStatisticsAsync(CancellationToken ct = default) =>
        SendAsync<CollectionStatistics>(HttpMethod.Get, "api/v1/collection/stats", null, ct);
    public Task<ItemPage> FilterItemsAsync(string? progress = null, string? status = null, bool? isLocal = null, int page = 1, CancellationToken ct = default) =>
        SendAsync<ItemPage>(HttpMethod.Get, $"api/v1/items?page={page}&page_size=100&progress={Uri.EscapeDataString(progress ?? "")}&status={Uri.EscapeDataString(status ?? "")}" +
            (isLocal is null ? "" : "&is_local=" + (isLocal.Value ? "true" : "false")), null, ct);
    public Task<ItemPage> ItemsAsync(int page = 1, CancellationToken ct = default) => SendAsync<ItemPage>(HttpMethod.Get,
        "api/v1/items?page=" + page + "&page_size=100", null, ct);
    public Task<ItemPage> RemovedItemsAsync(int page = 1, CancellationToken ct = default) => SendAsync<ItemPage>(HttpMethod.Get,
        "api/v1/items?deleted=true&page=" + page + "&page_size=100", null, ct);
    public Task<IReadOnlyList<PersonalPlayView>> PlaysAsync(string? itemId = null, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<PersonalPlayView>>(HttpMethod.Get, "api/v1/plays" +
            (itemId is null ? "" : "?item_id=" + Uri.EscapeDataString(itemId)), null, ct);
}
