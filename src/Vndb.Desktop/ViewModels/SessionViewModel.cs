using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<LocalPlayTarget> PlayTargets { get; } = [];
    public ObservableCollection<PersonalPlayView> SessionPlays { get; } = [];
    public ObservableCollection<ProcessObservation> RunningProcesses { get; } = [];
    public IReadOnlyList<string> PlayStatuses { get; } = ["playing", "played", "completed", "on_hold", "dropped"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartTracked))] private LocalPlayTarget? _selectedTarget;
    [ObservableProperty] private PersonalPlayView? _selectedPlay;
    [ObservableProperty] private ProcessObservation? _selectedProcess;
    [ObservableProperty] private string _actualExecutable = "";
    [ObservableProperty] private string _sessionStatus = "未开始自动会话。";
    [ObservableProperty] private string _quickReview = "";
    [ObservableProperty] private string _quickStatus = "playing";
    [ObservableProperty] private string _activityStatus = "";
    [ObservableProperty] private string _conflictDetails = "";
    [ObservableProperty] private bool _keepTimingOnClose;
    public bool CanStartTracked => SelectedProfile is not null && SelectedTarget is not null;
    partial void OnActualExecutableChanged(string value) { SelectedProcess = null; RunningProcesses.Clear(); }
    private string? _shownSession;
    partial void OnSelectedTargetChanged(LocalPlayTarget? value)
    { if (value is not null) { QuickReview = value.Details.Review ?? ""; QuickStatus = value.Details.Status == "not_started" ? "playing" : value.Details.Status; } }
    public async Task InitializeSessionsAsync()
    {
        await GuardAsync(async ct => { await Services().StartSessionsAsync(ct); await LoadTargetsAsync(ct); UpdateSessionStatus(); });
    }
    private async Task LoadTargetsAsync(CancellationToken ct)
    { PlayTargets.Clear(); foreach (var target in await Services().Activities.TargetsAsync(ct)) PlayTargets.Add(target); }
    private void SetSessionPlays(IReadOnlyList<PersonalPlayView> plays) { SelectedPlay = null; SessionPlays.Clear(); foreach (var play in plays) SessionPlays.Add(play); }
    public void UpdateSessionStatus()
    {
        var services = _services; if (services is null) return;
        ActivityStatus = services.ActivityHost?.Status ?? "会话服务尚未启动。";
        if (services.Sessions?.Current is not { } current) return;
        var state = current.Sample.State switch { "active" => "计时中", "paused" => "已暂停", "ended" => "已结束", _ => "待核对（" + current.Sample.State + "）" };
        SessionStatus = $"{state} · 已确认 {TimeSpan.FromTicks(current.ElapsedTicks):hh\\:mm\\:ss} · {current.Sample.Reason ?? current.Stage}";
        if (current.Sample.State is not ("active" or "paused") && _shownSession != current.Sample.Id)
        {
            _shownSession = current.Sample.Id; SelectedTarget = PlayTargets.FirstOrDefault(t => t.Id == current.TargetId);
            Status = "本次计时已结束。可在会话页记录感想／状态，也可直接跳过；不自动判定通关。";
        }
    }
    [RelayCommand] private Task LoadSessionPlaysAsync() => GuardAsync(async ct =>
    {
        var book = SelectedBook;
        if (book?.Item is not { } item) throw new ArgumentException("请选择书架作品；离线新作品可直接新建本次游玩。");
        var plays = await _library.PlaysAsync(item.Item.Id, ct); if (SelectedBook != book) return;
        book.ApplyPlays(plays); SetSessionPlays(plays); UpdateBookDetails(); Shelf.Refresh();
    });
    [RelayCommand] private Task NewPlayTargetAsync() => CreatePlayTargetAsync(false);
    [RelayCommand] private Task UseExistingPlayAsync() => CreatePlayTargetAsync(true);
    private Task CreatePlayTargetAsync(bool existing) => GuardAsync(async ct =>
    {
        var services = Services(); var identity = services.Identity ?? throw new InvalidOperationException("本机身份尚未加载。");
        var play = existing ? SelectedPlay ?? throw new ArgumentException("请明确选择本次游玩记录。") : null;
        var title = SelectedBook?.Title ?? SelectedProfile?.Title ?? LaunchTitle;
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("请先选择作品或明确保存本机启动配置。");
        var target = await services.Activities.CreateTargetAsync(identity, title, SelectedBook?.Metadata, SelectedBook?.Item?.Item.Id, play, ct,
            pendingItem: SelectedBook?.Item?.Item.Details);
        await LoadTargetsAsync(ct); SelectedTarget = PlayTargets.Single(t => t.Id == target.Id); Status = existing ? "已选择本次游玩；不会把重玩合并到其他记录。" : "已明确建立新的本次游玩，历史时间基数为0。";
    });
    [RelayCommand] private Task StartTrackedAsync() => GuardAsync(async ct =>
    {
        var services = Services(); var manager = services.Sessions ?? throw new InvalidOperationException("会话服务尚未启动。");
        var target = SelectedTarget ?? throw new ArgumentException("请先明确选定或新建本次游玩。");
        await manager.StartAsync(SelectedProfile?.Id ?? throw new ArgumentException("请选择本机启动配置。"), target, ct,
            LaunchMode == "LE 默认配置" ? LaunchMethod.GlobalLocale : LaunchMethod.Direct, services.Settings.DefaultLocale);
        services.ActivityHost?.SignalSync(); UpdateSessionStatus(); Status = "启动请求已处理；只有确认的游戏进程会计时。";
    });
    [RelayCommand] private Task PauseSessionAsync() => GuardAsync(async ct => { await SessionManager().PauseAsync(ct: ct); UpdateSessionStatus(); });
    [RelayCommand] private Task ResumeSessionAsync() => GuardAsync(async ct => { await SessionManager().PauseAsync(resume: true, ct); UpdateSessionStatus(); });
    [RelayCommand] private Task StopSessionAsync() => GuardAsync(async ct => { await SessionManager().StopAsync(ct: ct); Services().ActivityHost?.SignalSync(); UpdateSessionStatus(); });
    [RelayCommand] private Task RefreshProcessesAsync() => GuardAsync(async ct =>
    {
        var processes = await Task.Run(() => SessionManager().Processes(), ct); RunningProcesses.Clear();
        var expected = string.IsNullOrWhiteSpace(ActualExecutable) ? SelectedProfile?.TrackedExecutable ?? SelectedProfile?.Executable : Path.GetFullPath(ActualExecutable);
        if (expected is null) throw new ArgumentException("请先选择明确的启动／实际游戏路径。");
        foreach (var process in processes.Where(p => string.Equals(p.Identity.Executable, expected, StringComparison.OrdinalIgnoreCase))) RunningProcesses.Add(process);
        Status = "只列出指定完整程序路径的进程，请核对PID及创建时间后选择。";
    });
    [RelayCommand] private Task AttachProcessAsync() => GuardAsync(async ct =>
    {
        await SessionManager().AttachAsync(SelectedProcess?.Identity ?? throw new ArgumentException("请明确选择进程。"), SelectedTarget ?? throw new ArgumentException("请选择本次游玩。"), ct);
        Services().ActivityHost?.SignalSync(); UpdateSessionStatus();
    });
    [RelayCommand] private Task ChooseActualExecutableAsync() => GuardAsync(async _ => { if (await _dialogs.ExecutableAsync() is { } file) ActualExecutable = file; });
    [RelayCommand] private Task SaveTrackingAsync() => GuardAsync(async ct =>
    {
        var profile = await _launcher.ConfigureTrackingAsync(SelectedProfile?.Id ?? throw new ArgumentException("请选择本机配置。"), ActualExecutable, ct: ct);
        await LoadProfilesAsync(ct); SelectedProfile = Profiles.Single(p => p.Id == profile.Id); Status = "已明确保存实际游戏程序，包装器只用于启动及亲缘确认，不计其自身时间。";
    });
    [RelayCommand] private Task SaveQuickReviewAsync() => GuardAsync(async ct =>
    {
        var services = Services(); var target = await services.Activities.EditAsync(SelectedTarget ?? throw new ArgumentException("请选择本次游玩。"), QuickStatus, QuickReview, ct);
        await LoadTargetsAsync(ct); SelectedTarget = PlayTargets.Single(t => t.Id == target.Id); services.ActivityHost?.SignalSync(); Status = "感想／状态已写入本机待同步JSON；与Web冲突时保留双方版本。";
    });
    [RelayCommand] private Task SyncActivityAsync() => GuardAsync(async ct =>
    {
        var services = Services(); if (services.ActivityHost is not { } host) throw new InvalidOperationException("会话服务未加载。");
        await host.SyncAsync(ct); ActivityStatus = host.Status;
        if (services.Web is { } web) ConflictDetails = System.Text.Json.JsonSerializer.Serialize(await web.PlayEditConflictsAsync(ct), Vndb.Core.VndbJson.Options);
    });
    [RelayCommand] private Task ExportActivityAsync() => GuardAsync(async ct =>
    {
        if (await _dialogs.FolderAsync("选择新的活动JSON导出目录") is { } directory) Status = $"已导出 {await Services().Activities.ExportAsync(directory, ct)} 个完整活动批次，可在Web多文件导入。";
    });
    private LocalSessionManager SessionManager() => Services().Sessions ?? throw new InvalidOperationException("会话服务未加载。");
}
