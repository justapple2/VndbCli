namespace Vndb.Core;

public sealed record VndbClientOptions
{
    public TimeSpan RequestInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; init; } = 2;
    public TimeSpan ThrottleBackoff { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan TransientBackoff { get; init; } = TimeSpan.FromSeconds(2);
}
