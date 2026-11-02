using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Core;
using Vndb.Contracts;

namespace Vndb.Desktop.ViewModels;

public sealed record RelatedNovelRow(string OwnerId, string Id, string Kind, bool Official, string Title)
{
    public System.Windows.Input.ICommand? OpenCommand { get; init; }
    public string Label => NovelPresentation.Relation(Kind) + " · " + Id + " · " + (Official ? "官方关联" : "非官方关联");
}
public sealed partial class RelationsViewModel : ObservableObject
{
    public System.Windows.Input.ICommand? OpenCommand { get; set; }
    private string? _owner, _preference;
    private IReadOnlyList<NovelRelation>? _relations;
    private IReadOnlyList<RelatedNovelRow> _all = [];
    private int _limit = 20;
    private bool _pending;
    [ObservableProperty] private IReadOnlyList<RelatedNovelRow> _rows = [];
    [ObservableProperty] private string _summary = "关联资料未知。";
    [ObservableProperty] private bool _hasMore;
    public void Set(BookViewModel? book, IEnumerable<MetadataSnapshot>? known, string preference)
    {
        var relations = book?.Metadata?.Novel?.Relations;
        if (_owner == book?.Id && ReferenceEquals(_relations, relations) && _preference == preference && _pending == (book?.DetailsPending == true)) return;
        _pending = book?.DetailsPending == true;
        _owner = book?.Id; _relations = relations; _preference = preference; _limit = 20; _all = [];
        Summary = book?.DetailsPending == true ? "详情尚未加载；读取作品资料后显示已收录关联。" : relations is null ? "旧快照未记录关联；可显式刷新该作品资料。" : relations.Count == 0 ? "已读取关联字段，未列出关联作品。" : $"已收录 {relations.Count} 条直接关联；点击只读本库，不递归查询。";
        if (relations is { Count: > 0 })
        {
            try
            {
                if (relations.Count > 1000 || relations.Any(r => r is null || VndbId.Normalize(r.Id, 'v') != r.Id || string.IsNullOrWhiteSpace(r.Relation))) throw new ArgumentException();
                var names = (known ?? []).Where(m => m.Kind == "vn").DistinctBy(m => m.VndbId).ToDictionary(m => m.VndbId, StringComparer.Ordinal);
                _all = relations.DistinctBy(r => (r.Id, r.Relation, r.Official)).Select(r => new RelatedNovelRow(book!.Id, r.Id, r.Relation, r.Official,
                    names.TryGetValue(r.Id, out var m) ? NovelPresentation.Title(m.Novel, m.Title, preference).Title : "读取本库资料 · " + r.Id) { OpenCommand = OpenCommand }).ToArray();
            }
            catch (ArgumentException) { Summary = "关联字段无效，未按相似标题猜测目标。"; }
        }
        Render();
    }
    private void Render() { Rows = _all.Take(_limit).ToArray(); HasMore = _all.Count > _limit; }
    [RelayCommand] private void More() { _limit += 20; Render(); }
}
