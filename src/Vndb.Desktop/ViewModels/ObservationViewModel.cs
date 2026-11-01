using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    public ObservationFeatureViewModel Observation { get; }
    private ObservationResult? _shownObservation;
    private bool _applyingObservation;
    private void ApplyObservation()
    {
        if (_services is null) return;
        var result = _services.ObservationResult;
        Observation.Poll(result, _services.ObservationHost?.Status ?? "后台监控未启用；可在设置中添加管理根或手动核对。");
        if (result is null || ReferenceEquals(result, _shownObservation) || result.Catalog is null) return;
        _shownObservation = result; _installations = result.Catalog.Installations; _packages = result.Catalog.Packages ?? []; _services.Library.Seed(result.Catalog);
        var managed = (_services.ObservationSettings.Roots ?? []).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var book in _books.Concat(_queryBooks))
        {
            UpdateOwnership(book);
            var ids = _installations.Where(i => MatchesBook(i, book)).Select(i => i.Installation.Id).ToHashSet(StringComparer.Ordinal);
            var componentIds = _installations.SelectMany(i => BookComponents(i, book)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            book.SetObservation(result.Snapshot.Installations.Where(r => ids.Contains(r.Id) && managed.Contains(r.RootId)).ToArray(), Profiles.Where(p => p.ComponentId is not null && componentIds.Contains(p.ComponentId)).ToArray());
        }
        var chosenId = SelectedInstallation?.Installation.Id;
        _applyingObservation = true;
        try
        {
            Installations.Clear(); foreach (var i in _installations.Where(i => SelectedBook is null || MatchesBook(i, SelectedBook))) Installations.Add(i);
            SelectedInstallation = Installations.SingleOrDefault(i => i.Installation.Id == chosenId);
        }
        finally { _applyingObservation = false; }
        OnPropertyChanged(nameof(LaunchInstallations));
        if (!LaunchDraftChanged && !QuickEditorOpen) Shelf.Refresh();
        UpdateBookDetails();
    }
    private void UseObservationCandidate(ObservationCandidateRow row)
    {
        var path = Path.GetFullPath(Path.Combine(row.Root.Path, row.Candidate.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!ObservationSettingsStore.Within(row.Root.Path, path)) throw new ArgumentException("候选越过管理范围。");
        if ((row.Candidate.PreviousInstallationId ?? row.Candidate.PreviousPackageId) is { } oldId)
        {
            Observation.Summary = $"只读路径映射预览 · 安装 {oldId}\n{row.Candidate.PreviousPath}\n→ {path}\n原安装ID、启动配置与恢复证据保留；请人工核对，不创建重复安装或自动改路径。";
            Status = "发现疑似手动搬移；原存在状态保持未知，路径映射等待人工确认。"; return;
        }
        if (row.Candidate.Kind == "directory")
        {
            Registration.ManualWorkCommand.Execute(null); Registration.RootPath = path; Registration.ContainerTitle = Path.GetFileName(path); Section = 7;
            Discovery.Query = Path.GetFileName(path);
            Status = "候选目录已填入登记草稿；请查询并明确选择准确作品，再添加组件、预览确认。尚未关联或改动游戏。";
        }
        else
        {
            Packages.ScanRoot = row.Root.Path;
            Packages.Candidates = [new(row.Root.Path, row.Candidate.RelativePath, Path.GetFileNameWithoutExtension(path), "观测候选", row.Candidate.Length)];
            Packages.Selected = Packages.Candidates[0]; Section = 4;
            Status = "安装包候选已选定；请显式查询／选择作品，再预览登记。不解压、不运行。";
        }
    }
}
