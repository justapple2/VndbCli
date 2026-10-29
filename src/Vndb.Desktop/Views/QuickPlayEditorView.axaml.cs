using Avalonia.Controls;
namespace Vndb.Desktop.Views;
public partial class QuickPlayEditorView : UserControl
{
    public QuickPlayEditorView()
    {
        InitializeComponent(); Loaded += (_, _) => { if (PlayPicker.IsEffectivelyEnabled) PlayPicker.Focus(); else CloseEditor.Focus(); };
    }
}
