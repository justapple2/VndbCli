using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Vndb.Desktop.Services;

public sealed class DesktopDialogs(Window owner) : IDesktopDialogs
{
    public async Task<string?> RegistryFileAsync() => (await owner.StorageProvider.OpenFilePickerAsync(new()
        { Title = "读取注册信息（只预览目录字符串，不执行文件）", AllowMultiple = false, FileTypeFilter = [new("注册信息") { Patterns = ["*.reg"] }] })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<string?> ExecutableAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "明确选择本机游戏EXE（仅保存配置）", AllowMultiple = false,
            FileTypeFilter = [new("Windows程序") { Patterns = ["*.exe"] }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
    public async Task<string?> FolderAsync(string title) => (await owner.StorageProvider.OpenFolderPickerAsync(new()
        { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<string?> SaveJsonAsync(string name) => (await owner.StorageProvider.SaveFilePickerAsync(new()
        { Title = "导出JSON", SuggestedFileName = name, DefaultExtension = "json", FileTypeChoices = [new("JSON") { Patterns = ["*.json"] }] }))?.TryGetLocalPath();
}
