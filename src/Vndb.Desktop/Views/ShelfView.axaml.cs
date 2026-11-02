using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vndb.Desktop.ViewModels;
using Vndb.Desktop.Controls;

namespace Vndb.Desktop.Views;

/// <summary>Layout, focus and input routing only; all actions use the existing feature commands.</summary>
public partial class ShelfView : UserControl
{
    private ShelfBrowserViewModel? _browser;
    private bool _overlay;
    private string? _positionKey;
    private long _positionRevision;
    private void CapturePosition()
    {
        if (_browser is null || _positionKey is null || !IsEffectivelyVisible) return;
        var list = _positionKey.EndsWith("/True", StringComparison.Ordinal) ? BookList : CardRows;
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null) _browser.RememberPosition(_positionKey, scroll.Offset.Y);
    }
    private void RestorePosition()
    {
        if (_browser is null || !IsEffectivelyVisible) return;
        var key = _browser.ViewKey; _positionKey = key; var revision = ++_positionRevision;
        Dispatcher.UIThread.Post(() =>
        {
            if (_browser is null || !IsEffectivelyVisible || revision != _positionRevision || key != _browser.ViewKey) return;
            var list = _browser.ListMode ? BookList : CardRows; list.UpdateLayout();
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null) scroll.Offset = new(0, _browser.Position(key));
        }, DispatcherPriority.Loaded);
    }
    public ContextMenu CreateBookMenu(BookViewModel book)
    {
        var vm = (MainViewModel)DataContext!;
        return new ContextMenu { Items =
        {
            new MenuItem { Header = "直接启动", Icon = new AppIcon { Kind = "Play" }, Command = vm.DirectBookCommand, CommandParameter = book, IsEnabled = book.Item is not null && book.CanQuickLaunch },
            new MenuItem { Header = "使用LE默认配置启动", Icon = new AppIcon { Kind = "Play" }, Command = vm.LocaleBookCommand, CommandParameter = book, IsEnabled = book.Item is not null && book.CanQuickLaunch },
            new MenuItem { Header = "更新游玩状态与时间…", Icon = new AppIcon { Kind = "Clock" }, Command = vm.EditBookPlayCommand, CommandParameter = book, IsEnabled = book.Item is not null },
            new MenuItem { Header = "打开目录", Icon = new AppIcon { Kind = "Folder" }, Command = vm.OpenBookFolderCommand, CommandParameter = book, IsEnabled = book.Item is not null && book.HasInstallation },
            new Separator(),
            new MenuItem { Header = "进入配置", Command = vm.ConfigureBookCommand, CommandParameter = book },
            new MenuItem { Header = "检查／修复注册路径…", Command = vm.RegistryBookCommand, CommandParameter = book, IsEnabled = book.Item is not null && book.HasInstallation },
            new MenuItem { Header = "登记安装包…", Command = vm.RegisterPackageBookCommand, CommandParameter = book },
            new Separator(),
            new MenuItem { Header = "查看详情", Command = vm.Shelf.ShowDetailsCommand, CommandParameter = book },
            new MenuItem { Header = book.LaunchLabel, CommandParameter = book, IsEnabled = false }
        } };
    }
    private void OpenMenu(Control host, BookViewModel book)
    {
        _browser?.SelectIfVisible(book);
        host.ContextMenu?.Close(); host.ContextMenu = CreateBookMenu(book); host.ContextMenu.Open(host);
    }
    private void OnBookContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: BookViewModel book } host) return;
        OpenMenu(host, book); e.Handled = true;
    }
    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: BookViewModel book } host) OpenMenu(host, book);
        e.Handled = true;
    }
    private void OnCardClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: BookViewModel book }) _browser?.SelectCommand.Execute(book); }
    private void OnBookDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BookViewModel book } || e.Source is Control source && source.GetVisualAncestors().OfType<Control>().Prepend(source).OfType<Button>().Any(b => b.Classes.Contains("book-more"))) return;
        _browser?.ShowDetailsCommand.Execute(book); e.Handled = true;
    }
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) { _browser?.CloseDetailsCommand.Execute(null); e.Handled = true; }
    public ShelfView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => Subscribe();
        SizeChanged += (_, _) => UpdateAdaptive();
        BookArea.SizeChanged += (_, _) => UpdateColumns();
        AddHandler(KeyDownEvent, OnBookKeyDown, RoutingStrategies.Tunnel);
    }
    private void Subscribe()
    {
        var next = (DataContext as MainViewModel)?.Shelf;
        if (!ReferenceEquals(next, _browser))
        {
            Unsubscribe(); _browser = next;
            if (_browser is not null) { _browser.PropertyChanged += BrowserChanged; _browser.DisplayChanging += CapturePosition; _browser.DisplayChanged += RestorePosition; }
        }
        UpdateAdaptive(); RestorePosition();
    }
    private void Unsubscribe() { CapturePosition(); _positionRevision++; if (_browser is not null) { _browser.PropertyChanged -= BrowserChanged; _browser.DisplayChanging -= CapturePosition; _browser.DisplayChanged -= RestorePosition; } _browser = null; }
    private void BrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShelfBrowserViewModel.SelectedBook) or nameof(ShelfBrowserViewModel.DetailsOpen)) UpdateAdaptive();
        if (e.PropertyName == nameof(ShelfBrowserViewModel.ListMode)) Dispatcher.UIThread.Post(FocusSelectedBook, DispatcherPriority.Loaded);
    }
    private double Token(string key) => this.TryFindResource(key, out var value) && value is double number ? number : throw new InvalidOperationException("Missing layout token: " + key);
    private void UpdateAdaptive()
    {
        if (_browser is null || TopLevel.GetTopLevel(this) is null) return;
        var wide = Bounds.Width >= Token("ShelfWideThreshold");
        var reserve = wide && _browser.HasSelection;
        var overlay = !wide && _browser.HasSelection && _browser.DetailsOpen;
        var entering = overlay && !_overlay; var leaving = !overlay && _overlay; _overlay = overlay;
        ShelfColumns.ColumnDefinitions[1].Width = new(reserve ? Token("ShelfDetailWidth") : 0);
        ShelfColumns.ColumnSpacing = reserve ? 16 : 0;
        DetailPanel.IsVisible = reserve || overlay; DetailsBackdrop.IsVisible = overlay;
        Grid.SetRow(DetailPanel, wide ? 2 : 0); Grid.SetRowSpan(DetailPanel, wide ? 1 : 3);
        DetailPanel.Width = wide ? Token("ShelfDetailWidth") : Math.Min(Token("ShelfDrawerWidth"), Bounds.Width);
        DetailsCover.Height = Token(wide ? "ShelfDetailCoverHeight" : "ShelfDrawerCoverHeight");
        CloseDetails.IsVisible = !wide; BookArea.IsEnabled = !overlay; ShelfToolbar.IsEnabled = !overlay; ShelfFilters.IsEnabled = !overlay;
        if (entering) Dispatcher.UIThread.Post(() => CloseDetails.Focus(), DispatcherPriority.Loaded);
        if (leaving) Dispatcher.UIThread.Post(FocusSelectedBook, DispatcherPriority.Loaded);
        UpdateColumns();
    }
    private void UpdateColumns()
    {
        if (IsEffectivelyVisible && _browser is not null && BookArea.Bounds.Width > 0)
        {
            var oldColumns = _browser.Columns;
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            var restoreFocus = focused?.DataContext is BookViewModel;
            _browser.SetColumns((int)(BookArea.Bounds.Width / Token("ShelfCardMinimumWidth")));
            if (restoreFocus && oldColumns != _browser.Columns) Dispatcher.UIThread.Post(FocusSelectedBook, DispatcherPriority.Loaded);
        }
    }
    private void OnBookKeyDown(object? sender, KeyEventArgs e)
    {
        if (_browser is null || e.Handled) return;
        if (e.Key == Key.Escape && _overlay) { _browser.CloseDetailsCommand.Execute(null); e.Handled = true; return; }
        if (_overlay) return;
        var source = e.Source as Control;
        var owner = source is null ? null : source.GetVisualAncestors().OfType<Control>().Prepend(source).FirstOrDefault(c => c.DataContext is BookViewModel);
        var book = owner?.DataContext as BookViewModel;
        if (book is null && source != BookList && source != CardRows) return;
        if (book is not null) _browser.SelectIfVisible(book);
        if (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
        { if (_browser.SelectedBook is { } target) OpenMenu(owner ?? source!, target); e.Handled = true; return; }
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Enter)
        {
            if (source is Button button && button.Classes.Contains("book-more")) return;
            if (_browser.HasSelection) _browser.OpenDetailsCommand.Execute(null); e.Handled = true; return;
        }
        var stride = _browser.ListMode ? 1 : _browser.Columns;
        var delta = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -stride, Key.Down => stride, _ => 0 };
        if (delta == 0) return;
        _browser.MoveSelection(delta); FocusSelectedBook(); e.Handled = true;
    }
    public void FocusSelectedBook()
    {
        if (_browser?.SelectedBook is not { } book || !IsEffectivelyVisible) return;
        if (_browser.ListMode) { BookList.ScrollIntoView(book); BookList.UpdateLayout(); if (!BookList.Focus()) ShelfViewToggle.Focus(); return; }
        var row = _browser.Rows.FirstOrDefault(r => r.Books.Any(b => b.Id == book.Id));
        if (row is not null) CardRows.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() =>
        {
            CardRows.UpdateLayout();
            if (CardRows.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("card") && ReferenceEquals(b.DataContext, book))?.Focus() != true)
                ShelfViewToggle.Focus();
        }, DispatcherPriority.Loaded);
    }
}
