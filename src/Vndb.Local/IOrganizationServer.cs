using Vndb.Contracts;

namespace Vndb.Local;

public interface IOrganizationServer
{
    Task<ServiceInfo> OrganizationInfoAsync(CancellationToken ct);
    Task<LocalArchiveRunView> ArchiveRunAsync(string id, CancellationToken ct);
    Task<ArchiveReceipt> PlanArchiveAsync(LocalArchivePlan plan, CancellationToken ct);
    Task<ArchiveReceipt> PlanRestoreAsync(LocalRestorePlan plan, CancellationToken ct);
    Task<EvidenceReceipt> UploadEvidenceAsync(string file, string runId, string phase, CancellationToken ct);
    Task<ArchiveReceipt> CompleteArchiveAsync(string runId, ArchiveCompletion completion, bool restore, CancellationToken ct);
    Task<ArchiveReceipt> CancelArchiveAsync(string runId, ArchiveCompletion completion, CancellationToken ct);
}
