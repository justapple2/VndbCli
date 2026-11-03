using Vndb.Local;
using Vndb.Core;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Desktop.Services;

namespace Vndb.Desktop.ViewModels;

public sealed record PlayStateChoice(string Code, string Label);
public sealed record PlayChoice(PersonalPlayView Play, string Label);
public sealed partial class QuickPlayEditorViewModel : ObservableObject
{
    private readonly BookViewModel _book;
    private QuickPlayBook Reference => new(_book.Id, _book.Title, _book.Item, _book.Plays, _book.NeedsPlayReview, _book.PlaysPending);
    private readonly IQuickPlayService _service;
    private readonly Action _close;
    private readonly Func<Task> _saved;
    public string Title => _book.Title;
    public ObservableCollection<PlayChoice> Plays { get; } = [];
    public IReadOnlyList<PlayStateChoice> States { get; } = [new("not_started", "未游玩"), new("playing", "游玩中"), new("played", "已游玩"), new("completed", "本次完成"), new("on_hold", "搁置"), new("dropped", "放弃")];
    [ObservableProperty] private PlayChoice? _selectedPlay;
    [ObservableProperty] private PlayStateChoice? _selectedState;
    [ObservableProperty] private DateTimeOffset? _startedOn;
    [ObservableProperty] private DateTimeOffset? _finishedOn;
    [ObservableProperty] private decimal? _hours;
    [ObservableProperty] private decimal? _minutes;
    [ObservableProperty] private bool _durationKnown;
    [ObservableProperty] private decimal? _score;
    [ObservableProperty] private string? _review;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private string? _extraNotes;
    [ObservableProperty] private string? _language;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChooseRecord))] private bool _isDirty;
    [ObservableProperty] private bool _confirmClose;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable)), NotifyCanExecuteChangedFor(nameof(SaveCommand)), NotifyCanExecuteChangedFor(nameof(CloseCommand))] private bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _hasSelection;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable)), NotifyPropertyChangedFor(nameof(HasDraft)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] private PersonalEditDraft? _draft;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Editable)), NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _applied;
    [ObservableProperty] private bool _conflict;
    [ObservableProperty] private string _message = "选择要更新的游玩记录，或明确新建一次游玩／重玩。";
    [ObservableProperty] private string _timeSummary = "自动会话和人工调整保持原样；此处只编辑手工记录时长。";
    [ObservableProperty] private IReadOnlyList<FieldComparisonDisplay> _differences = [];
    [ObservableProperty] private string _comparisonSummary = "对照只读原稿与Web，不自动合并或改变操作ID。";
    private PersonalEditInspection? _inspection;
    public string Outcome => Applied ? "已应用" : Conflict ? "明确冲突／拒绝" : _inspection?.State == PersonalOperationState.Uncertain ? "结果未确认" : HasDraft ? "待同步" : "尚未保存";
    private PersonalPlayData? _basis;
    private bool _newRecord;
    private bool _settingFields;
    private PlayChoice? _acceptedPlay;
    private bool _rejectSelection;
    public bool HasDraft => Draft is not null;
    public bool Editable => !Busy && HasSelection && Draft is null && !Applied;
    public bool CanChooseRecord => !Busy && !HasDraft && !Applied && !IsDirty;
    public bool CanRetry => HasDraft && !Busy && !Applied && !Conflict;
    public string TargetSummary => $"条目 {_book.Id}\n" + (Draft is { } d ? $"待确认原稿 · {d.PlayId ?? "新建记录"} · 基于修订 {d.Request.ExpectedRevision?.ToString() ?? "新建"}" :
        SelectedPlay is { } p ? $"记录 {p.Play.Id} · 修订 {p.Play.Revision}" : _newRecord ? "新建独立游玩／重玩，原记录保留" : "请选择具体记录，或新建一次游玩");
    private bool CanSave() => Editable;
    private bool CanClose() => !Busy;
    public QuickPlayEditorViewModel(BookViewModel book, IQuickPlayService service, Action close, Func<Task> saved)
    { _book = book; _service = service; _close = close; _saved = saved; }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var plays = await _service.LoadAsync(Reference, ct); _book.ApplyPlays(plays); Plays.Clear();
        foreach (var (play, index) in plays.Select((p, i) => (p, i))) Plays.Add(new(play, $"{play.Details.StartedOn?.ToString("yyyy-MM-dd") ?? "日期未记录"} · {States.FirstOrDefault(s => s.Code == play.Details.Status)?.Label} · 记录 {index + 1}"));
        if (Plays.Count == 1) SelectedPlay = Plays[0];
        if (await _service.PendingAsync(Reference, ct) is { } pending)
        {
            Draft = pending; SelectedPlay = Plays.SingleOrDefault(p => p.Play.Id == pending.PlayId);
            _basis = pending.Request.Details; SetFields(_basis); HasSelection = true;
            Message = "已载入上次未同步的修改，可以重试同步。";
            await InspectCoreAsync(false);
            if (Conflict) Message = "已恢复明确冲突／拒绝的原稿；可先只读字段对照，再保留原稿并重新读取。";
            else if (_inspection?.State == PersonalOperationState.Uncertain) Message = "已恢复结果未确认的原操作；只按原ID重试，不按相似Web内容推断已应用。";
        }
    }
    partial void OnSelectedPlayChanged(PlayChoice? value)
    {
        if (Draft is not null || _rejectSelection) return;
        if (IsDirty && value != _acceptedPlay)
        { _rejectSelection = true; SelectedPlay = _acceptedPlay; _rejectSelection = false; Message = "请先保存或放弃当前修改，再选择另一条记录。"; return; }
        _acceptedPlay = value;
        if (value is null) return;
        _newRecord = false; _basis = value.Play.Details; SetFields(_basis); HasSelection = true;
        TimeSummary = value.Play.Time is { } time
            ? $"自动会话 {time.SessionSeconds / 60d:0.##} 分钟 · 调整 {time.AdjustmentSeconds / 60d:0.##} 分钟\n合计：{(time.TotalSeconds is { } total ? (total / 3600d).ToString("0.##") + " 小时" : "未知（手工基数未记录）")}"
            : "此处修改手工记录时长；自动会话和调整记录单独保留。";
    }
    private void SetFields(PersonalPlayData details)
    {
        _settingFields = true;
        SelectedState = States.Single(s => s.Code == details.Status);
        StartedOn = details.StartedOn is { } start ? new(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        FinishedOn = details.FinishedOn is { } end ? new(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DurationKnown = details.DurationMinutes is not null; Hours = details.DurationMinutes / 60; Minutes = details.DurationMinutes % 60;
        Score = details.Score; Review = details.Review; Notes = details.Notes; ExtraNotes = details.ExtraNotes; Language = details.Language;
        _settingFields = false; IsDirty = false;
    }
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_settingFields && Editable && e.PropertyName is nameof(SelectedState) or nameof(StartedOn) or nameof(FinishedOn) or nameof(DurationKnown) or nameof(Hours) or nameof(Minutes) or nameof(Score) or nameof(Review) or nameof(Notes) or nameof(ExtraNotes) or nameof(Language))
        { try { IsDirty = BuildDetails() != _basis; } catch (ArgumentException) { IsDirty = true; } }
        if (e.PropertyName is nameof(Busy) or nameof(Draft) or nameof(Applied) or nameof(Conflict))
        { OnPropertyChanged(nameof(CanChooseRecord)); OnPropertyChanged(nameof(CanRetry)); }
        if (e.PropertyName is nameof(Draft) or nameof(SelectedPlay) or nameof(HasSelection)) OnPropertyChanged(nameof(TargetSummary));
        if (e.PropertyName is nameof(Draft) or nameof(Applied) or nameof(Conflict)) OnPropertyChanged(nameof(Outcome));
    }
    [RelayCommand] private void NewRecord()
    {
        if (!CanChooseRecord) return;
        SelectedPlay = null; _newRecord = true;
        _basis = new(_book.Item?.Item.Details.Title ?? Title, _book.Item?.Item.Details.NovelId, "playing", null, null, null, null, null, null, null, null);
        SetFields(_basis); HasSelection = true; TimeSummary = "新的独立游玩记录，原记录及重玩时间不合并。";
        OnPropertyChanged(nameof(TargetSummary));
        Message = "正在新建一次独立游玩，点击保存后才会写入。";
    }
    public PersonalPlayData BuildDetails()
    {
        if (_basis is null || !HasSelection || !_newRecord && SelectedPlay is null && Draft is null) throw new ArgumentException("请明确选择记录或新建一次游玩。");
        int? duration = null;
        if (DurationKnown)
        {
            var hours = Hours ?? 0; var minutes = Minutes ?? 0;
            if (hours < 0 || hours != decimal.Truncate(hours) || minutes is < 0 or > 59 || minutes != decimal.Truncate(minutes) || hours > (int.MaxValue - minutes) / 60m)
                throw new ArgumentException("小时须为非负整数，分钟为0–59，且总时长不能溢出。");
            duration = (int)(hours * 60 + minutes);
        }
        var start = StartedOn is { } s ? DateOnly.FromDateTime(s.DateTime) : (DateOnly?)null;
        var end = FinishedOn is { } e ? DateOnly.FromDateTime(e.DateTime) : (DateOnly?)null;
        var state = SelectedState?.Code ?? throw new ArgumentException("请选择游玩状态。");
        var details = _basis with { Status = state, StartedOn = start, FinishedOn = end, DurationMinutes = duration,
            Score = Score, Review = Review, Notes = Notes, ExtraNotes = ExtraNotes, Language = Language };
        PersonalRules.Validate(details.ToPersonal());
        return details;
    }
    [RelayCommand(CanExecute = nameof(CanSave))] private Task SaveAsync() => RunAsync(async () =>
    {
        Draft = await _service.QueueAsync(Reference, _newRecord ? null : SelectedPlay?.Play, BuildDetails(), CancellationToken.None);
        Message = "编辑原稿已保存到本机，正在同步…"; await SyncCoreAsync();
    }, true);
    [RelayCommand] private Task RetryAsync() => RunAsync(SyncCoreAsync, true);
    [RelayCommand] private Task PreviewChangesAsync() => RunAsync(async () =>
    {
        if (Draft is not null) await InspectCoreAsync(false);
        else
        {
            Differences = FieldComparisonDisplay.Rows(PersonalComparison.Play(_newRecord ? null : _basis?.ToPersonal(), true, null, false, BuildDetails().ToPersonal()));
            ComparisonSummary = "原值为本次编辑基准；Web当前值未读取。尚未保存，也未发送请求。";
        }
    });
    [RelayCommand] private Task ReadComparisonAsync() => RunAsync(() => InspectCoreAsync(true));
    private async Task InspectCoreAsync(bool refresh)
    {
        if (Draft is not { } draft) throw new ArgumentException("请先保存原稿；未保存内容可使用字段预览。");
        _inspection = await _service.InspectAsync(draft, refresh, CancellationToken.None);
        Conflict |= _inspection.State == PersonalOperationState.Conflict;
        Applied |= _inspection.State == PersonalOperationState.Applied;
        var rows = _inspection.Differences.ToList();
        if (_inspection.Conflict is { ItemRevision: not null } target && target.ItemNovelId != draft.Request.Details.NovelId)
            rows.Add(new("item_novel_id", new(draft.Request.Details.NovelId), new(target.ItemNovelId), new(draft.Request.Details.NovelId)));
        Differences = FieldComparisonDisplay.Rows(rows);
        ComparisonSummary = (draft.OriginalKnown ? "原值来自原稿基准。" : "旧稿未保存原值，明确显示未知。") +
            (_inspection.ObservedAt is { } at ? $" Web观测：{at.ToLocalTime():g}。" : _inspection.CurrentKnown ? " Web值来自已确认回执，尚未重新读取。" : " Web值尚未读取。") +
            (_inspection.ReadFailed ? " 本次读取未完成，保留已有观测与原稿。" : "") +
            (_inspection.Current?.ItemId is { } actualItem && draft.Request.ItemId is { } expectedItem && actualItem != expectedItem ? " 记录归属已改变，需回书架核对。" : "") +
            (_inspection.Conflict?.ItemRemoved == true ? " 书架条目已移除，需回书架核对。" : "");
        OnPropertyChanged(nameof(Outcome));
    }
    private async Task SyncCoreAsync()
    {
        var draft = Draft ?? throw new ArgumentException("没有待同步原稿。");
        await _service.SyncAsync(draft, CancellationToken.None); Applied = true; Conflict = false; IsDirty = false;
        TimeSummary = "手工时长已保存；自动会话和调整值保留，刷新后的书架显示最新合计。";
        Message = "已同步到Web，游玩记录已更新；原始编辑与回执保留在本机。";
        try { await _saved(); }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ClientApiException or HttpRequestException)
        { Message = "已同步到Web，书架刷新失败；请稍后手动刷新。"; }
    }
    [RelayCommand] private Task ReloadConflictAsync() => RunAsync(async () =>
    {
        await _service.WithdrawConflictAsync(Draft ?? throw new ArgumentException("没有待核对原稿。"), CancellationToken.None);
        IsDirty = false; _acceptedPlay = null; Draft = null; Conflict = false; Applied = false; HasSelection = false; SelectedPlay = null; _basis = null; _inspection = null; Differences = [];
        await InitializeAsync(); Message = "已保留冲突原稿并载入Web版本，请核对后重新编辑。";
    });
    [RelayCommand] private void ClearDates() { if (Editable) { StartedOn = null; FinishedOn = null; } }
    [RelayCommand] private void ResetUnsaved()
    {
        if (!Editable || _basis is null) return;
        SetFields(_basis); ConfirmClose = false; Message = "未保存的修改已放弃，没有写入。";
    }
    [RelayCommand(CanExecute = nameof(CanClose))] private void Close()
    {
        if (IsDirty && !HasDraft && !Applied) { ConfirmClose = true; Message = "有未保存的修改。继续编辑，或明确放弃后返回。"; }
        else _close();
    }
    [RelayCommand] private void ContinueEditing() => ConfirmClose = false;
    [RelayCommand] private void DiscardAndClose() { if (!Busy && !HasDraft) _close(); }
    private async Task RunAsync(Func<Task> action, bool inspectAfter = false)
    {
        if (Busy) return; Busy = true;
        try { await action(); }
        catch (ClientApiException e) when (e.Status == 409) { Conflict = true; Message = "Web记录已改变。原稿保留；请重新载入Web版本，核对后再编辑。"; }
        catch (WebUnavailableException) { Message = "Web暂时不可达，修改已保存在本机；联网后可重试同步。"; }
        catch (ClientApiException e) when (e.Status is 401 or 403) { Message = "修改已保留，请在设置中核对Web管理密钥后重试。"; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or ClientApiException or HttpRequestException or System.Text.Json.JsonException) { Message = "未完成：" + e.Message; }
        finally
        {
            if (inspectAfter && Draft is not null)
                try { await InspectCoreAsync(false); }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or UnauthorizedAccessException) { ComparisonSummary = "原稿／回执对照无法读取；未确认新的结果，请保留原件核对。"; }
            Busy = false;
        }
    }
}
