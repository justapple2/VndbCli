namespace Vndb.Contracts;

public sealed record DatasetRequest(string SnapshotDate);
public sealed record DatasetRevision(long ExpectedRevision);
public sealed record DatasetSyncSettings(long Revision = 0, bool Enabled = false, int IntervalDays = 7);
public sealed record DatasetSyncWrite(long ExpectedRevision, bool Enabled, int IntervalDays);
public sealed record DatasetSyncStatus(DatasetSyncSettings Settings, string? LatestSnapshotDate, DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? CheckNotBefore, DateTimeOffset? NextCheckAt, string? Error, bool Checking,
    DateTimeOffset? CompletedSnapshotAt, DateTimeOffset? CompletedAt, DatasetJob? ActiveJob, bool? UpdateAvailable, bool WorkerEnabled);
public sealed record DatasetSyncResult(string State, string? SnapshotDate, DatasetJob? Job = null);
public sealed record DatasetJob(string Id, string Source, string State, string Phase, long Revision,
    DateTimeOffset CreatedAt, string? Sha256 = null, DateTimeOffset? SnapshotAt = null,
    long Total = 0, long Processed = 0, long Written = 0, long Kept = 0, long DownloadBytes = 0,
    string? Error = null, DateTimeOffset? CompletedAt = null, DateTimeOffset? EarliestResumeAt = null);
