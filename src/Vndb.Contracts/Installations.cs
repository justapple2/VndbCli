namespace Vndb.Contracts;

public sealed record DeviceData(string Name, string Platform);
public sealed record LibraryDevice(string Id, DeviceData Details, long Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? LastSeenAt = null, bool Legacy = false, string? MappedToId = null);
public sealed record DeviceWrite(string Id, DeviceData Details, long? ExpectedRevision = null);
public sealed record LegacyDeviceClaim(string DeviceId, long ExpectedRevision);
public sealed record InstallationData(string DeviceId, string RootPath, string Title, string? CompanyGroup = null);
public sealed record Installation(string Id, InstallationData Details, string Status, DateTimeOffset? ObservedAt,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RemovedAt = null, string? MergedIntoId = null);
public sealed record InstallationWrite(InstallationData Details, long? ExpectedRevision = null);
public sealed record InstallationObservation(string Status, DateTimeOffset ObservedAt, long ExpectedRevision);
public sealed record ComponentData(string ItemId, string RelativePath, string Kind = "game", string? Label = null);
public sealed record InstallationComponent(string Id, string InstallationId, ComponentData Details, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ComponentWrite(ComponentData Details, long? ExpectedRevision = null);
public sealed record InstallationView(Installation Installation, IReadOnlyList<InstallationComponent> Components);
public sealed record InstallationPage(IReadOnlyList<InstallationView> Items, int Page, int PageSize, int Total, bool More);
public sealed record LegacyLocationLink(string SourceKind, string LegacyId, string? InstallationId, string? ComponentId,
    string State, string Basis, long Revision = 1);
public sealed record LegacyLocationDecision(string InstallationId, string ComponentId, long ExpectedRevision);
public sealed record InstallationReference(string Id, long ExpectedRevision);
public sealed record InstallationBundle(InstallationData Details, IReadOnlyList<InstallationReference> Children, InstallationReference? Container = null);
public sealed record PresenceLocation(string DeviceId, string DeviceName, string InstallationId, string ComponentId,
    string Path, string Status, DateTimeOffset? ObservedAt, string DeviceState, string Scope);
public sealed record NovelPresence(string NovelId, IReadOnlyList<PresenceLocation> Locations);
