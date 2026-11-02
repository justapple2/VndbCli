using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Core;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    private BookViewModel? _relatedBook;
    private readonly List<BookViewModel> _relatedHistory = [];
    private long _relatedGeneration;
    [ObservableProperty] private string _relatedNotice = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMissingRelated))] private string? _missingRelatedId;
    public bool HasMissingRelated => MissingRelatedId is not null;
    public bool CanBackRelated => _relatedHistory.Count > 0 || MissingRelatedId is not null;
    private void ClearRelatedDetails()
    { _relatedGeneration++; _relatedBook = null; _relatedHistory.Clear(); MissingRelatedId = null; RelatedNotice = ""; OnPropertyChanged(nameof(CanBackRelated)); }
    [RelayCommand] private Task OpenRelatedAsync(RelatedNovelRow row) => GuardAsync(async ct =>
    {
        var parent = _relatedBook ?? SelectedBook;
        if (parent is null || parent.Id != row.OwnerId || !BookDetails.Relations.Rows.Contains(row)) throw new ArgumentException("关联目标已变化，请重新选择。");
        if (_relatedHistory.Count >= 8) throw new ArgumentException("关联导航最多8层，请先返回；没有递归读取作品。");
        var generation = ++_relatedGeneration; var section = Section;
        try
        {
            var response = await _library.LocalNovelAsync(row.Id, ct); ct.ThrowIfCancellationRequested();
            if (generation != _relatedGeneration || section != Section || !ReferenceEquals(parent, _relatedBook ?? SelectedBook)) return;
            if (response.Items.Count == 0) { MissingRelatedId = row.Id; RelatedNotice = "本库和本机快照暂无 " + row.Id + "；可回填查询后显式联网补全。"; OnPropertyChanged(nameof(CanBackRelated)); return; }
            if (response.Items.Count != 1 || response.Items[0] is not { Kind: "vn", Novel: { } novel } metadata || metadata.VndbId != row.Id || novel.Id != row.Id)
                throw new InvalidDataException("关联回复身份不符，原详情保留。");
            _relatedHistory.Add(parent); _relatedBook = new(null, metadata); _relatedBook.SetTitlePreference(TitleChoice.Code, ShowJapaneseSubtitle);
            MissingRelatedId = null; RelatedNotice = "只读公共关联资料 · " + response.Source + "；未选择或创建个人版本。";
            UpdateBookDetails(); OnPropertyChanged(nameof(CanBackRelated));
        }
        catch (Exception e) when (e is Vndb.Contracts.OfflineCacheMissException or NotSupportedException)
        { if (generation == _relatedGeneration) { MissingRelatedId = row.Id; RelatedNotice = "当前无法只读此关联资料；原详情保留，可回填ID后手动查询。"; OnPropertyChanged(nameof(CanBackRelated)); } }
    });
    [RelayCommand] private void BackRelated()
    {
        _relatedGeneration++; RelatedNotice = "";
        if (MissingRelatedId is not null) MissingRelatedId = null;
        else if (_relatedHistory.Count > 0)
        { var previous = _relatedHistory[^1]; _relatedHistory.RemoveAt(_relatedHistory.Count - 1); _relatedBook = ReferenceEquals(previous, SelectedBook) ? null : previous; }
        UpdateBookDetails(); OnPropertyChanged(nameof(CanBackRelated));
    }
    [RelayCommand] private void PrepareRelatedQuery()
    {
        if (MissingRelatedId is not { } id) return;
        Discovery.Query = id; Section = 2; Status = "关联ID已回填；请明确选择查询来源并执行查询。尚未联网补全。";
    }
}
