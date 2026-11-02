using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Contracts;
using Vndb.Desktop.Services;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record QuerySourceChoice(string Code, string Label);

/// <summary>Query form and commands; routing and export rules belong to shared use cases.</summary>
public partial class QueryFeatureViewModel(IDesktopLibrary library, IDesktopDialogs dialogs,
    Func<Func<CancellationToken, Task>, Task> run, Action<string> status, Func<LocalQueryScope?>? historyScope = null) : ObservableObject
{
    [ObservableProperty] private string _query = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedSource))] private string _querySource = "auto";
    public IReadOnlyList<QuerySourceChoice> SourceChoices { get; } = [new("auto", "自动 · Web优先"), new("web", "通过Web查询"), new("vndb", "直连VNDB"), new("cache", "仅本机快照")];
    public QuerySourceChoice SelectedSource { get => SourceChoices.FirstOrDefault(s => s.Code == QuerySource) ?? SourceChoices[0]; set { if (value is not null) QuerySource = value.Code; } }
    public MetadataResponse? Result { get; private set; }
    public event Action<MetadataResponse>? Completed;
    private long _generation;
    private SearchHistoryViewModel? _history;
    public SearchHistoryViewModel History => _history ??= new(historyScope ?? (() => null), "lookup", text => Query = text, run);
    public void Clear() { _generation++; Result = null; }

    [RelayCommand] private Task SearchAsync() => run(async ct =>
    {
        var generation = ++_generation; var text = Query.Trim(); var source = QuerySource;
        _ = ClientQuery.FromText(text, 20);
        await History.RecordAsync(text, ct);
        ct.ThrowIfCancellationRequested(); if (generation != _generation) return;
        var result = await library.QueryAsync(text, source, ct);
        ct.ThrowIfCancellationRequested(); if (generation != _generation) return;
        Result = result;
        Completed?.Invoke(Result);
        status($"查询完成：{Result.Items.Count} 项。");
    });
    [ObservableProperty] private bool _refreshReleases;
    [RelayCommand] private Task ReleasesAsync() => run(async ct =>
    {
        var generation = ++_generation; var id = Vndb.Core.VndbId.Normalize(Query.Trim(), 'v');
        await History.RecordAsync(id, ct);
        ct.ThrowIfCancellationRequested(); if (generation != _generation) return;
        var result = await library.ReleasesAsync(id, QuerySource, RefreshReleases, ct);
        ct.ThrowIfCancellationRequested(); if (generation != _generation) return;
        Result = result;
        Completed?.Invoke(Result);
        status("发行目录查询完成；打开作品详情查看版本，结果可导出JSON。");
    });
    [RelayCommand] private Task ExportAsync() => run(async ct =>
    {
        if (Result is null) throw new ArgumentException("请先查询；导出只含metadata，不包含启动配置或管理密钥。");
        if (await dialogs.SaveJsonAsync("vndb-metadata.json") is { } file)
        {
            await MetadataExport.WriteAsync(Result, file, ct);
            status("metadata JSON已导出，可通过Web批量导入。");
        }
    });
}
