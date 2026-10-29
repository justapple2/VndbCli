using Vndb.Local;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Desktop.Services;

namespace Vndb.Desktop.ViewModels;
public partial class MainViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(QuickEditorOpen))] private QuickPlayEditorViewModel? _quickEditor;
    public bool QuickEditorOpen => QuickEditor is not null;
    [RelayCommand] private Task EditBookPlayAsync(BookViewModel? book) => OpenPlayEditorAsync(book, null);
    [RelayCommand] private Task EditPlayRecordAsync(PlayDetailRow? row) => OpenPlayEditorAsync(SelectedBook, row?.Play.Id);
    private Task OpenPlayEditorAsync(BookViewModel? book, string? playId) => GuardAsync(async ct =>
    {
        if (book?.Item is null) throw new ArgumentException("请先选择Web书架作品，再编辑游玩记录。");
        if (QuickEditor is { } existing) { existing.CloseCommand.Execute(null); return; }
        SelectedBook = book;
        var service = _quickPlayService ?? throw new InvalidOperationException("此宿主没有配置个人记录编辑服务。");
        var editor = new QuickPlayEditorViewModel(book, service, () => QuickEditor = null, () => RefreshCommand.ExecuteAsync(null));
        await editor.InitializeAsync(ct);
        if (SelectedBook != book) return;
        if (!editor.HasDraft && playId is not null)
            editor.SelectedPlay = editor.Plays.SingleOrDefault(p => p.Play.Id == playId) ?? throw new ArgumentException("此游玩记录已变化，请重新读取。");
        QuickEditor = editor;
        if (SelectedBook == book) SetSessionPlays(book.Plays);
        UpdateBookDetails(); Shelf.Refresh();
    });
    private readonly IQuickPlayService? _quickPlayService;
}
