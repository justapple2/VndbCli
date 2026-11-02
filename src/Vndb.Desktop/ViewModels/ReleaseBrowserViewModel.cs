using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Core;

namespace Vndb.Desktop.ViewModels;

public sealed record ReleaseDetailRow(string Id, string Title, string Summary);

public sealed partial class ReleaseBrowserViewModel : ObservableObject
{
    private ReleaseCatalog? _catalog;
    private string? _owner, _novelId;
    private int _limit = 20;
    private bool _pending;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private IReadOnlyList<ReleaseDetailRow> _rows = [];
    [ObservableProperty] private string _summary = "发行目录未知。";
    [ObservableProperty] private bool _hasMore;
    public void Set(BookViewModel? book)
    {
        var catalog = book?.Metadata?.Novel?.ReleaseCatalog;
        if (_owner == book?.Id && ReferenceEquals(_catalog, catalog) && _pending == (book?.DetailsPending == true)) return;
        _pending = book?.DetailsPending == true;
        _owner = book?.Id; _novelId = book?.Metadata?.VndbId; _catalog = null; _limit = 20; Filter = ""; _catalog = catalog;
        if (catalog is null) { Rows = []; HasMore = false; Summary = book?.DetailsPending == true ? "详情尚未加载；读取作品资料后显示已保存发行目录。" : "尚未查询／保存发行目录；可在查询页按作品ID显式查询。"; return; }
        try { ReleaseRules.Validate(catalog, _novelId!); Render(); }
        catch (ArgumentException) { _catalog = null; Rows = []; HasMore = false; Summary = "发行目录校验未通过，未展示不明版本；请重新读取资料。"; }
    }
    partial void OnFilterChanged(string value) { _limit = 20; if (_catalog is not null) Render(); }
    private void Render()
    {
        var matches = _catalog!.Items.Select(r => new ReleaseDetailRow(r.Id, r.AltTitle ?? r.Title, ReleaseRules.Describe(r, _novelId!)))
            .Where(r => (r.Id + "\n" + r.Title + "\n" + r.Summary).Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        Rows = matches.Take(_limit).ToArray(); HasMore = matches.Length > _limit;
        Summary = $"{_catalog.Items.Count} 项 · 匹配 {matches.Length} · 显示 {Rows.Count} · 更新 {_catalog.FetchedAt.ToLocalTime():g}" +
            (_catalog.Items.Count == 0 ? "；已查询，未返回发行记录。" : "；本机具体版本未知，不能据此认定已安装某语言版。");
    }
    [RelayCommand] private void More() { _limit += 20; if (_catalog is not null) Render(); }
}
