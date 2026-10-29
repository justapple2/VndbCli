using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string _argumentsText = "";
    [ObservableProperty] private string _launchPreview = "选择启动程序，单独填写参数；保存后可预览实际命令。";
    private bool _argumentSync;
    private string? _argumentsError;
    private void SetArguments(IReadOnlyList<string> arguments)
    {
        ArgumentsJson = JsonSerializer.Serialize(arguments);
        // A previous invalid text edit may not have changed the JSON property at all.
        OnArgumentsJsonChanged(ArgumentsJson);
    }
    partial void OnArgumentsTextChanged(string value)
    {
        if (_argumentSync) return;
        try
        {
            _argumentSync = true; ArgumentsJson = JsonSerializer.Serialize(LaunchArguments.Parse(value)); _argumentsError = null;
        }
        catch (ArgumentException e) { _argumentsError = e.Message; }
        finally { _argumentSync = false; OnPropertyChanged(nameof(LaunchDraftChanged)); OnPropertyChanged(nameof(LaunchDraftNotice)); }
    }
    partial void OnArgumentsJsonChanged(string value)
    {
        if (_argumentSync) return;
        try
        {
            var args = JsonSerializer.Deserialize<string[]>(value) ?? throw new JsonException("参数不能为空。");
            if (args.Any(a => a is null)) throw new JsonException("参数不得含null。");
            _argumentSync = true; ArgumentsText = LaunchArguments.Format(args); _argumentsError = null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { _argumentsError = e.Message; }
        finally { _argumentSync = false; OnPropertyChanged(nameof(LaunchDraftChanged)); OnPropertyChanged(nameof(LaunchDraftNotice)); }
    }
    [RelayCommand] private Task PreviewLaunchAsync() => GuardAsync(async ct =>
    {
        var start = await _launcher.PreviewStartAsync(SelectedProfile?.Id ?? throw new ArgumentException("请先保存或选择启动配置。"), ct,
            LaunchMode == "LE 默认配置" ? LaunchMethod.GlobalLocale : LaunchMethod.Direct, _services?.Settings.DefaultLocale);
        LaunchPreview = $"已保存命令（尚未运行）：\n{LaunchArguments.Format([start.FileName])} {LaunchArguments.Format(start.ArgumentList)}\n工作目录：{start.WorkingDirectory}";
    });
}
