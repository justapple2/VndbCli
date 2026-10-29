using Avalonia.Controls;
using Avalonia.Interactivity;
using Vndb.Desktop.ViewModels;

namespace Vndb.Desktop.Views;

public partial class PublicCatalogView : UserControl
{
    public PublicCatalogView() => InitializeComponent();
    private void OnResize(object? sender, SizeChangedEventArgs e) => (DataContext as PublicCatalogViewModel)?.Resize(e.NewSize.Width);
    private void OnSelect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PublicCatalogViewModel vm && sender is Control { DataContext: CatalogBookViewModel book }) vm.Selected = book;
    }
}
