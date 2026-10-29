namespace Vndb.Contracts;

public sealed record SessionSample(string Id, string DeviceId, string PlayId, string? InstallationId, string? ComponentId,
    DateTimeOffset StartedAt, DateTimeOffset CheckpointAt, DateTimeOffset? EndedAt, long Sequence, long CumulativeSeconds,
    string State, string? Reason = null, bool NeedsReview = false);
public sealed record PlaySession(SessionSample Sample, long Revision, string ReviewState = "accepted", bool Reviewed = false);
public sealed record SessionDecision(long ExpectedRevision, bool Include);
public sealed record PlayTimeSummary(string PlayId, int? BaseMinutes, long SessionSeconds, long AdjustmentSeconds,
    long? TotalSeconds, int PendingSessions, long Revision);
public sealed record TimeAdjustment(string Id, string PlayId, long Seconds, string Reason, long ExpectedRevision);
public sealed record ActivityItemSeed(string ClientId, LibraryItemData Details);
public sealed record ActivityInstallationSeed(string ClientId, InstallationData Details, IReadOnlyList<ComponentData> Components);
public sealed record ActivityPlaySeed(string ClientId, string ItemRef, PersonalPlayData Details);
public sealed record PlayEditOperation(string Id, string PlayRef, long ExpectedRevision, PersonalPlayData Details);
public sealed record ActivityResult(string Key, string Kind, string Status, string? ServerId = null, string? Message = null,
    PersonalPlayView? Current = null, PersonalPlayData? Incoming = null);
public sealed record ActivityReceipt(string BatchId, string PayloadSha256, string ServiceInstanceId, IReadOnlyList<ActivityResult> Results,
    bool Complete, DateTimeOffset ReceivedAt);
public sealed record PlayEditConflict(string Id, string DeviceId, PlayEditOperation Incoming, PersonalPlayView Current,
    long Revision, string State = "pending");
public sealed record PlayEditDecision(long ExpectedRevision, long PlayRevision, bool UseIncoming);
public sealed record ActivityBatch
{
    public string Format { get; init; } = "vn-library-activity";
    public int FormatVersion { get; init; } = 1;
    public string Kind { get; init; } = "play_activity";
    public required string BatchId { get; init; }
    public required string DeviceId { get; init; }
    public required DeviceData Device { get; init; }
    public IReadOnlyList<MetadataSnapshot> Metadata { get; init; } = [];
    public IReadOnlyList<ActivityItemSeed> Items { get; init; } = [];
    public IReadOnlyList<ActivityInstallationSeed> Installations { get; init; } = [];
    public IReadOnlyList<ActivityPlaySeed> Plays { get; init; } = [];
    public IReadOnlyList<SessionSample> PlaySessions { get; init; } = [];
    public IReadOnlyList<PlayEditOperation> PlayEdits { get; init; } = [];
}

// Web input keeps only an empty compatibility slot. Its public schema must never
// describe installation/component trees, even when the local legacy DTO still exists.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record WebActivityBatch
{
    public string Format { get; init; } = "vn-library-activity";
    public int FormatVersion { get; init; } = 1;
    public string Kind { get; init; } = "play_activity";
    public required string BatchId { get; init; }
    public required string DeviceId { get; init; }
    public required DeviceData Device { get; init; }
    public IReadOnlyList<MetadataSnapshot> Metadata { get; init; } = [];
    public IReadOnlyList<ActivityItemSeed> Items { get; init; } = [];
    public IReadOnlyList<System.Text.Json.JsonElement> Installations { get; init; } = [];
    public IReadOnlyList<ActivityPlaySeed> Plays { get; init; } = [];
    public IReadOnlyList<SessionSample> PlaySessions { get; init; } = [];
    public IReadOnlyList<PlayEditOperation> PlayEdits { get; init; } = [];
    public ActivityBatch ToBatch()
    {
        if (Installations is null || Installations.Count != 0) throw new ArgumentException("安装结构仅保存在本机JSON。");
        return new() { Format = Format, FormatVersion = FormatVersion, Kind = Kind, BatchId = BatchId, DeviceId = DeviceId, Device = Device,
            Metadata = Metadata, Items = Items, Plays = Plays, PlaySessions = PlaySessions, PlayEdits = PlayEdits };
    }
}
