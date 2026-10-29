using Vndb.Local;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Threading;
using Vndb.Client;
using Vndb.Desktop.Services;
using Vndb.Desktop.ViewModels;

namespace Vndb.Desktop;

public partial class App : Application
{
    public static CoverCache? Covers { get; set; }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var directory = Environment.GetEnvironmentVariable("VN_DESKTOP_HOME") ?? ClientSettings.UserDirectory;
            var services = new DesktopServices(directory);
            var window = new MainWindow();
            var vm = new MainViewModel(services.Library, services.Launcher, new DesktopDialogs(window), services);
            window.DataContext = vm; desktop.MainWindow = window;
            var coverHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            void ReconfigureCovers() => Covers = new(coverHttp, services.StateDirectory,
                services.Settings.Server is { } server ? ClientSettings.ServerUri(server) : null);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StateLocation)) ReconfigureCovers(); };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => vm.UpdateSessionStatus();
            window.Opened += async (_, _) => { await vm.InitializeAsync(); ReconfigureCovers(); await vm.RefreshCommand.ExecuteAsync(null); await vm.InitializeSessionsAsync(); timer.Start(); };
            var closing = false;
            window.Closing += async (_, e) =>
            {
                if (closing) return;
                e.Cancel = true;
                if (vm.KeepTimingOnClose && services.Sessions?.Running == true) { window.WindowState = WindowState.Minimized; return; }
                closing = true;
                try { if (services.ActivityHost is { } host) await host.DisposeAsync(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                { closing = false; vm.Status = "关闭前检查点未保存，窗口保留：" + error.Message; return; }
                timer.Stop(); window.Close();
            };
            desktop.Exit += (_, _) => { services.Dispose(); coverHttp.Dispose(); Covers = null; };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
