using Vndb.Local;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Threading;
using Vndb.Client;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;
using Vndb.Desktop.Views;

namespace Vndb.Desktop;

public partial class App : Application
{
    public static CoverCache? Covers { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DesktopServices? startupServices = null; HttpClient? startupCovers = null;
            try
            {
                var directory = Environment.GetEnvironmentVariable("VN_DESKTOP_HOME") ?? ClientSettings.UserDirectory;
                var services = startupServices = new DesktopServices(directory);
                var window = new MainWindow();
                var vm = new MainViewModel(services.Library, services.Launcher, new DesktopDialogs(window), services);
                window.DataContext = vm; desktop.MainWindow = window;
                var coverHttp = startupCovers = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                void ReconfigureCovers() => Covers = new(coverHttp, services.StateDirectory,
                    services.Settings.Server is { } server ? ClientSettings.ServerUri(server) : null);
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StateLocation)) ReconfigureCovers(); };
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => vm.UpdateSessionStatus();
                vm.StartupFinished += success => { if (success) timer.Start(); else timer.Stop(); };
                window.Opened += async (_, _) => await vm.StartAsync(ReconfigureCovers);
                window.Activated += (_, _) => { if (vm.SelectedInstallation is { } selected) services.ObservationHost?.Focus(selected.Installation.Details.RootPath); };
                var closing = false;
                window.Closing += async (_, e) =>
                {
                    if (closing) return;
                    e.Cancel = true;
                    if (vm.KeepTimingOnClose && services.Sessions?.Running == true) { window.WindowState = WindowState.Minimized; return; }
                    closing = true;
                    try { await services.StopObservationAsync(); if (services.ActivityHost is { } host) await host.DisposeAsync(); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                    { closing = false; vm.Status = "关闭前检查点未保存，窗口保留：" + error.Message; return; }
                    timer.Stop(); window.Close();
                };
                desktop.Exit += (_, _) => { services.Dispose(); coverHttp.Dispose(); Covers = null; };
            }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                startupServices?.Dispose(); startupCovers?.Dispose(); Covers = null;
                var fallback = new Window { Title = "VN Library · 启动检查", Width = 720, Height = 560 };
                var diagnostics = new StartupDiagnosticViewModel(() => new DesktopDialogs(fallback).SaveJsonAsync("vn-startup-diagnostics.json"));
                diagnostics.Trace.Begin(); diagnostics.Trace.RecordFailure(StartupStage.Configuration, error); diagnostics.Refresh();
                fallback.Content = new ScrollViewer { Margin = new Thickness(20), Content = new StartupDiagnosticView { DataContext = diagnostics } };
                desktop.MainWindow = fallback;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
