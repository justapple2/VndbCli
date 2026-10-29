namespace Vndb.Desktop.Services;

public interface IDesktopDialogs
{
    Task<string?> ExecutableAsync();
    Task<string?> FolderAsync(string title);
    Task<string?> SaveJsonAsync(string name);
    Task<string?> RegistryFileAsync() => Task.FromResult<string?>(null);
}
