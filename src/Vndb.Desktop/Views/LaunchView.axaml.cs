using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Vndb.Desktop.Views;

public partial class LaunchView : UserControl
{
    public LaunchView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            var compact = Bounds.Width < (double)this.FindResource("FormWideThreshold")!;
            LayoutGrid.ColumnDefinitions = new(compact ? "*" : "2*,3*");
            LayoutGrid.RowDefinitions = new(compact ? "Auto,Auto" : "*");
            PageScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            EditorScroll.VerticalScrollBarVisibility = compact ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            ProfilesPane.Height = compact ? 340 : double.NaN;
            Grid.SetColumn(EditorScroll, compact ? 0 : 1); Grid.SetRow(EditorScroll, compact ? 1 : 0);
        };
    }
}
