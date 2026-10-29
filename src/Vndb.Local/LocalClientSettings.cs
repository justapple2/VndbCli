using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LocalClientSettings(string? Server = null, string SourceScope = "", string TargetRoot = "",
    string Grouping = "company-japanese-v1", string LocaleEmulatorPath = "", LocaleLaunch? DefaultLocale = null, string? CacheDirectory = null);

public sealed class LocalClientSettingsStore(string directory)
{
    private readonly JsonStateFile<LocalClientSettings> _state = new(Path.GetFullPath(directory), "desktop-settings.json");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string Scope(string? server) => LocalQueryScope.ServerDirectory(DirectoryPath, server);
    public async Task<LocalClientSettings> LoadAsync(CancellationToken ct = default)
    {
        var current = await _state.ReadAsync(ct);
        if (current is not null) return current;
        var legacy = ClientSettings.Load(ClientSettings.DefaultFile(DirectoryPath));
        return new(Server: legacy.Server, CacheDirectory: legacy.CacheDirectory);
    }

    public Task SaveAsync(LocalClientSettings settings, CancellationToken ct = default)
    {
        if (settings.Server is not null) _ = ClientSettings.ServerUri(settings.Server);
        if (settings.Grouping != "company-japanese-v1") throw new ArgumentException("仅支持会社／日文标题规则，冲突停止。");
        return _state.SaveAsync(settings, ct);
    }
    public async Task SaveCredentialAsync(string server, string? key, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此平台仅支持会话内密钥；持久凭据暂仅支持Windows DPAPI。");
        var file = Path.Combine(Scope(server), "credential.json");
        if (string.IsNullOrEmpty(key)) { if (File.Exists(file)) File.Delete(file); return; }
        if (key.Any(char.IsWhiteSpace) || key.Any(char.IsControl)) throw new ArgumentException("管理密钥不得含空白。");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = Encoding.UTF8.GetBytes(key);
        var temporary = file + "." + Guid.NewGuid().ToString("N");
        try
        {
            var encrypted = ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes(ClientSettings.ServerUri(server).AbsoluteUri), DataProtectionScope.CurrentUser);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ProtectedCredential("windows-dpapi", Convert.ToBase64String(encrypted)), VndbJson.Options), ct);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, file, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<string?> CredentialAsync(string server, CancellationToken ct = default)
    {
        var file = Path.Combine(Scope(server), "credential.json");
        if (!File.Exists(file) || !OperatingSystem.IsWindows()) return null;
        var saved = JsonSerializer.Deserialize<ProtectedCredential>(await File.ReadAllTextAsync(file, ct), VndbJson.Options) ?? throw new InvalidDataException("凭据JSON无效。");
        if (saved.Format != "windows-dpapi") throw new InvalidDataException("凭据格式不兼容。");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(saved.Data), Encoding.UTF8.GetBytes(ClientSettings.ServerUri(server).AbsoluteUri), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private sealed record ProtectedCredential(string Format, string Data);
}
