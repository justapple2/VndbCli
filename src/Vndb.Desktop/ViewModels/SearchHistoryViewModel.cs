using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed partial class SearchHistoryViewModel(Func<LocalQueryScope?> scope, string channel, Action<string> recall,
    Func<Func<CancellationToken, Task>, Task> run) : ObservableObject
{
    [ObservableProperty] private IReadOnlyList<string> _entries = [];
    [ObservableProperty] private string? _selected;
    [ObservableProperty] private string _notice = "仅保存显式提交，最多20项；选中后回填，不自动查询。";
    private string? _loadedScope;
    public async Task ReloadAsync(CancellationToken ct)
    {
        Entries = []; Selected = null;
        var current = scope(); _loadedScope = current?.DirectoryPath;
        if (current is null) return;
        try { var rows = await new SearchHistoryStore(current, channel).ReadAsync(ct); if (_loadedScope == current.DirectoryPath) Entries = rows; }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { Notice = "历史未读取，原文件保留；查询仍可使用。"; }
    }
    public async Task RecordAsync(string text, CancellationToken ct)
    {
        var current = scope(); if (current is null) return;
        try { var rows = await new SearchHistoryStore(current, channel).RecordAsync(text, ct); if (scope()?.DirectoryPath == current.DirectoryPath) Entries = rows; }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { Notice = "历史未保存；查询仍可使用，原文件保留。"; }
    }
    [RelayCommand] private void Recall() { if (Selected is { } text) recall(text); }
    [RelayCommand] private Task RemoveAsync() => ChangeAsync(Selected, false);
    [RelayCommand] private Task ClearAsync() => ChangeAsync(null, true);
    private Task ChangeAsync(string? text, bool all) => run(async ct =>
    {
        if (!all && text is null) return;
        var current = scope(); if (current is null) { Entries = []; Selected = null; return; }
        var rows = await new SearchHistoryStore(current, channel).RemoveAsync(text, ct);
        if (scope()?.DirectoryPath == current.DirectoryPath) { Entries = rows; Selected = null; }
    });
}
