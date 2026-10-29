using Vndb.Contracts;

namespace Vndb.Local;

// Canonical local JSON models. Legacy Contracts equivalents are server-maintenance compatibility only.
public sealed record LocalInstallationData(string DeviceId, string RootPath, string Title, string? CompanyGroup = null);
public sealed record LocalInstallation(string Id, LocalInstallationData Details, string Status, DateTimeOffset? ObservedAt,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? RemovedAt = null, string? MergedIntoId = null);
public sealed record LocalComponentData(string ItemId, string RelativePath, string Kind = "game", string? Label = null);
public sealed record LocalInstallationComponent(string Id, string InstallationId, LocalComponentData Details, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record LocalInstallationView(LocalInstallation Installation, IReadOnlyList<LocalInstallationComponent> Components);
public sealed record LocalPhysicalIdentity(string Volume, string FileId, long CreatedFileTime);
public sealed record LocalFileProof(string Path, bool Directory, LocalPhysicalIdentity Identity, long Length, long LastWriteFileTime, int Attributes, string? Sha256);
public sealed record LocalTreeProof(LocalPhysicalIdentity Root, IReadOnlyList<LocalFileProof> Entries, string Sha256, long Files, long Bytes);
public sealed record LocalArchivePolicy(string SourceScope, string TargetRoot, string Naming = "company-japanese-v1", string Conflict = "stop");
public sealed record LocalArchiveComponent(string Id, string RelativePath);
public sealed record LocalDependencyHint(string File, string Kind, string Detail);
public sealed record LocalArchivePlan(string Id, string DeviceId, string InstallationId, long InstallationRevision,
    string SourcePath, string DestinationPath, LocalArchivePolicy Policy, LocalPhysicalIdentity SourceScopeIdentity,
    LocalPhysicalIdentity TargetRootIdentity, LocalTreeProof Before, IReadOnlyList<LocalArchiveComponent> Components,
    IReadOnlyList<LocalDependencyHint> Dependencies, string Company, string Title, DateTimeOffset CreatedAt,
    string Format = "vn-archive-plan", int FormatVersion = 1);
public sealed record LocalRestorePlan(string RunId, string ArchiveSha256, string SourcePath, string DestinationPath,
    LocalTreeProof Before, long InstallationRevision, long RunRevision, DateTimeOffset CreatedAt);
public sealed record LocalArchiveRunView(LocalArchivePlan Plan, ArchiveReceipt Receipt, LocalRestorePlan? RestorePlan = null);
public sealed record LocalMoveEvidence(string RunId, string PlanSha256, string Phase, LocalTreeProof Before, LocalTreeProof After,
    string JournalHead, DateTimeOffset CompletedAt);
public sealed record LocalComponentCheck(string ComponentId, string RelativePath, string Status);
public sealed record LocalAuditExecutable(string Path, string Description);
public sealed record LocalOrganizationSuggestion(string State, string? DestinationPath, string Message);
public sealed record LocalInstallationCheck(string InstallationId, long ExpectedRevision, string RootPath,
    string Status, string Reason, IReadOnlyList<LocalComponentCheck> Components,
    string LaunchStatus, IReadOnlyList<LocalAuditExecutable> Executables, LocalOrganizationSuggestion? Suggestion = null);
public sealed record LocalInstallationAudit(string Id, string ServiceInstanceId, string DeviceId, string SourceScope,
    string TargetRoot, DateTimeOffset ObservedAt, IReadOnlyList<LocalInstallationCheck> Items,
    string Format = "vn-library-installation-audit", int FormatVersion = 1);
