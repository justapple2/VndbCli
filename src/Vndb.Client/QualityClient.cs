using Vndb.Contracts;

namespace Vndb.Client;

public sealed partial class WebLibraryClient
{
    public Task<QualityPage> QualityAsync(string? kind = null, int page = 1, CancellationToken ct = default) =>
        SendAsync<QualityPage>(HttpMethod.Get, "api/v1/quality?page=" + page + (kind is null ? "" : "&kind=" + Uri.EscapeDataString(kind)), null, ct);
}
