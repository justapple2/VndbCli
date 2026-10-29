using Avalonia.Controls;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vndb.Desktop.Views;
using Vndb.Desktop.ViewModels;

namespace Vndb.Desktop;

public partial class MainWindow : Window
{
    private MainViewModel? _subscribed;
    private Control? _editorReturnFocus;
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => { var compact = Bounds.Width < 1050; Classes.Set("compact", compact); Workspace.ColumnDefinitions[0].Width = new(compact ? 72 : 212); };
        DataContextChanged += (_, _) =>
        {
            if (_subscribed is not null) { _subscribed.PropertyChanged -= PreferencesChanged; _subscribed.PropertyChanging -= BeforeEditorChanged; }
            _subscribed = DataContext as MainViewModel;
            if (_subscribed is not null) { _subscribed.PropertyChanged += PreferencesChanged; _subscribed.PropertyChanging += BeforeEditorChanged; }
            ApplyAppearance();
        };
    }
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ThemePreference) or nameof(MainViewModel.ReducedMotion)) ApplyAppearance();
        if (e.PropertyName == nameof(MainViewModel.QuickEditor) && _subscribed?.QuickEditor is null)
            Dispatcher.UIThread.Post(() =>
            {
                var restored = _editorReturnFocus is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } control && TopLevel.GetTopLevel(control) == this && control.Focus();
                if (!restored) this.GetVisualDescendants().OfType<ShelfView>().FirstOrDefault(v => v.IsEffectivelyVisible)?.FocusSelectedBook();
                _editorReturnFocus = null;
            }, DispatcherPriority.Loaded);
    }
    private void BeforeEditorChanged(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.QuickEditor) && _subscribed?.QuickEditor is null) _editorReturnFocus = FocusManager?.GetFocusedElement() as Control; }
    private void ApplyAppearance()
    {
        if (_subscribed is not { } vm) return;
        RequestedThemeVariant = vm.ThemePreference switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        Classes.Set("no-motion", vm.ReducedMotion);
        PageHost.PageTransition = vm.ReducedMotion ? null : new CrossFade(TimeSpan.FromMilliseconds(160)) { FadeInEasing = new CubicEaseOut(), FadeOutEasing = new CubicEaseIn() };
        UpdateIcons();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == ActualThemeVariantProperty || change.Property == WindowStateProperty) UpdateIcons(); }
    private void UpdateIcons()
    {
        if (ThemeIcon is not null) ThemeIcon.Kind = ActualThemeVariant == ThemeVariant.Dark ? "Sun" : "Moon";
        if (MaximizeIcon is not null) MaximizeIcon.Kind = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }
    private async void ToggleTheme(object? sender, RoutedEventArgs e)
    { if (DataContext is MainViewModel vm) await vm.SetThemeAsync(ActualThemeVariant == ThemeVariant.Dark ? "Light" : "Dark"); }
    private void Minimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize(object? sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_subscribed?.QuickEditor is { } editor) { e.Cancel = true; editor.CloseCommand.Execute(null); return; }
        base.OnClosing(e);
    }
}
