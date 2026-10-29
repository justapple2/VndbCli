namespace Vndb.Contracts;

public sealed record DatasetRequest(string SnapshotDate);
public sealed record DatasetRevision(long ExpectedRevision);
public sealed record DatasetJob(string Id, string Source, string State, string Phase, long Revision,
    DateTimeOffset CreatedAt, string? Sha256 = null, DateTimeOffset? SnapshotAt = null,
    long Total = 0, long Processed = 0, long Written = 0, long Kept = 0, long DownloadBytes = 0,
    string? Error = null, DateTimeOffset? CompletedAt = null, DateTimeOffset? EarliestResumeAt = null);
