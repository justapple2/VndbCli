using Vndb.Contracts;

namespace Vndb.Local;

public sealed record DirectoryCandidate(string Path, string? InstallationId, IReadOnlyList<LocalDependencyHint> Clues, string Basis,
    string? SuggestedName = null, bool NameNeedsReview = false);
public sealed record ScanIssue(string Path, string Kind);
public sealed record DirectoryScan(string Scope, IReadOnlyList<DirectoryCandidate> Candidates, IReadOnlyList<ScanIssue> Issues);

/// <summary>Explicit bounded discovery only. EXE clues are candidates, never permission to move or a VN match.</summary>
public static class DirectoryScanner
{
    public static DirectoryScan Scan(string scope, int depth = 3, IReadOnlyList<LocalInstallationView>? known = null, CancellationToken ct = default)
    {
        scope = LocalFiles.Full(scope); LocalFiles.CheckAncestors(scope); _ = NativeFiles.Identity(scope);
        if (depth is < 1 or > 6) throw new ArgumentException("扫描深度为1–6。");
        var mappings = (known ?? []).Where(v => v.Installation.RemovedAt is null).ToArray();
        var candidates = new List<DirectoryCandidate>(); var issues = new List<ScanIssue>(); var visited = 0;
        void Walk(string path, int level)
        {
            ct.ThrowIfCancellationRequested(); if (++visited > 5000) throw new IOException("扫描超过5000目录，请缩小源范围。");
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { issues.Add(new(path, "reparse-skipped")); return; }
                var match = mappings.SingleOrDefault(v => LocalFiles.Same(v.Installation.Details.RootPath, path));
                var entries = System.IO.Directory.GetFileSystemEntries(path);
                if (match is not null || entries.Any(p => Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(p) & FileAttributes.Directory) == 0))
                {
                    var name = Path.GetFileName(path); var needsReview = !GameDirectoryNaming.IsCompatibleName(name); string? suggested = null;
                    if (needsReview)
                    {
                        try { suggested = GameDirectoryNaming.Name(name); }
                        catch (ArgumentException) { issues.Add(new(path, "name-needs-manual-text")); }
                    }
                    candidates.Add(new(path, match?.Installation.Id, LocalFiles.Inspect(path), match is null ? "启动程序线索待确认；不自动认作视觉小说或建立VNDB关联。" : "既有完整物理安装映射；不拆分其内部章节／特典。", suggested, needsReview)); return;
                }
                if (level == depth) { issues.Add(new(path, "depth-limit")); return; }
                foreach (var child in entries.Order(StringComparer.Ordinal))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { issues.Add(new(child, "reparse-skipped")); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) Walk(child, level + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { issues.Add(new(path, e.GetType().Name)); }
        }
        Walk(scope, 0); return new(scope, candidates, issues);
    }
}
