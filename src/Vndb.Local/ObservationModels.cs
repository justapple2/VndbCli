using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vndb.Local;

// Local JSON only. None of these paths, identities or observations are wire contracts.
public sealed record ObservationRoot(string Id, string Path, string Kind, int Depth, LocalPhysicalIdentity Identity);
public sealed record ObservationSettings(bool Enabled = false, IReadOnlyList<ObservationRoot>? Roots = null,
    int WatcherLimit = 64, int PeriodMinutes = 10, string Format = "vn-local-observation-settings", int FormatVersion = 1)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed record ObservationFile(string Path, LocalPhysicalIdentity Identity, long Length, DateTime LastWriteUtc);
public sealed record LocalInstallationObservation(string Id, string RootId, string Path, LocalPhysicalIdentity? Identity,
    IReadOnlyList<ObservationFile> Anchors, string Status, string Reason, string LaunchState, DateTimeOffset? NegativeSince = null, bool Complete = false, string? LaunchSignature = null, IReadOnlyList<string>? ReadyProfiles = null);
public sealed record PackageObservation(string Id, string RootId, string Status, string Reason, DateTimeOffset? NegativeSince = null, LocalPhysicalIdentity? Identity = null);
public sealed record ObservationCandidate(string Id, string RootId, string RelativePath, string Kind, LocalPhysicalIdentity Identity,
    long Length, DateTime LastWriteUtc, DateTimeOffset StableSince, string State = "checking", string? PreviousInstallationId = null, string? PreviousPath = null, string? PreviousPackageId = null)
{
    public string Label => RelativePath + " · " + ObservationText.Candidate(State) + (PreviousPath is null ? "" : " · 疑似原安装搬移（只读映射预览）：" + PreviousPath + " → " + RelativePath);
}
public sealed record ObservationSnapshot(string DeviceId, IReadOnlyList<LocalInstallationObservation> Installations,
    IReadOnlyList<PackageObservation> Packages, IReadOnlyList<ObservationCandidate> Candidates,
    string Format = "vn-local-observations", int FormatVersion = 1);
public sealed record ObservationMetrics(int Roots, int Probes, int Enumerations, int Entries, int ChangedInstallations,
    int ChangedPackages, int SkippedConcurrent, int JsonWrites, long ContentHashBytes = 0);
public sealed record ObservationResult(ObservationSnapshot Snapshot, ObservationMetrics Metrics, IReadOnlyList<string> WatchPaths,
    bool Degraded, bool Deferred, DateTimeOffset CheckedAt, LocalLibrary? Catalog = null);
public static class ObservationText
{
    public static string Reason(string value) => value switch
    {
        "present" => "已核对登记资源；不代表游戏可运行或版本完整",
        "missing" => "管理根身份正确，资源经两次检查确认缺失",
        "confirming_missing" => "疑似缺失，等待第二次确认",
        "root_unavailable" => "管理根不可读或身份变化；保留上次存在状态",
        "identity_changed" => "原位置已被替换，请重新登记核对身份",
        "entry_missing" => "部分启动入口缺失或不可读，但已有游戏内容仍在",
        "insufficient_evidence" => "仅有目录或残留，资源情况待核对",
        "file_changed" => "文件元数据发生变化，请核对；未更新信任指纹",
        "archive_pending" => "整理／复原尚未完成，暂不解释为卸载",
        "concurrent_change" => "登记在检查期间变化，等待再次核对",
        "location_changed" => "同一目录身份出现在新位置；保留原登记与存在标记，待人工确认路径映射",
        _ => "检查不可用，保留登记与启动配置"
    };
    public static string Candidate(string state) => state switch { "checking" => "待稳定／待匹配", "pending" => "待人工匹配", "registered" => "已登记", "missing" => "已消失", "ignored" => "已忽略", _ => "待核对" };
}
