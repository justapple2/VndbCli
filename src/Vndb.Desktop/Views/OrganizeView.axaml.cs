using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Vndb.Desktop.Views;

public partial class OrganizeView : UserControl
{
    public OrganizeView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < (double)this.FindResource("FormWideThreshold")!;
            LayoutGrid.ColumnDefinitions = new(compact ? "*" : "2*,3*");
            LayoutGrid.RowDefinitions = new(compact ? "Auto,Auto" : "*");
            PageScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            FormPane.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            PreviewPane.MinHeight = compact ? 380 : 0;
            Grid.SetColumn(PreviewPane, compact ? 0 : 1); Grid.SetRow(PreviewPane, compact ? 1 : 0);
        };
    }
}
