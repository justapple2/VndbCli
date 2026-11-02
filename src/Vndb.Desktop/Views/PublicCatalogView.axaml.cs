using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vndb.Desktop.ViewModels;

namespace Vndb.Desktop.Views;

public partial class PublicCatalogView : UserControl
{
    private PublicCatalogViewModel? _browser;
    private string? _key;
    private long _revision;
    public PublicCatalogView()
    {
        InitializeComponent(); AttachedToVisualTree += (_, _) => Subscribe(); DetachedFromVisualTree += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => Subscribe();
    }
    private void Subscribe()
    {
        var next = DataContext as PublicCatalogViewModel;
        if (!ReferenceEquals(next, _browser)) { Unsubscribe(); _browser = next; if (_browser is not null) { _browser.DisplayChanging += Capture; _browser.DisplayChanged += Restore; } }
        Restore();
    }
    private void Unsubscribe() { Capture(); _revision++; if (_browser is not null) { _browser.DisplayChanging -= Capture; _browser.DisplayChanged -= Restore; } _browser = null; }
    private ListBox? List(string key) => this.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Classes.Contains(key.EndsWith("/True", StringComparison.Ordinal) ? "book-list" : "card-rows"));
    private void Capture()
    {
        if (_browser is null || _key is null || !IsEffectivelyVisible) return;
        var scroll = List(_key)?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null) _browser.RememberPosition(_key, scroll.Offset.Y);
    }
    private void Restore()
    {
        if (_browser is null || !IsEffectivelyVisible) return;
        var key = _browser.ViewKey; _key = key; var revision = ++_revision;
        Dispatcher.UIThread.Post(() =>
        {
            if (_browser is null || !IsEffectivelyVisible || revision != _revision || key != _browser.ViewKey) return;
            var list = List(key); list?.UpdateLayout(); var scroll = list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroll is not null) scroll.Offset = new(0, _browser.Position(key));
        }, DispatcherPriority.Loaded);
    }
    private void OnResize(object? sender, SizeChangedEventArgs e) { if (IsEffectivelyVisible) (DataContext as PublicCatalogViewModel)?.Resize(e.NewSize.Width); }
    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublicCatalogViewModel vm && sender is Control { DataContext: CatalogBookViewModel book }) vm.Selected = book;
    }
}
