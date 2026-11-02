using Vndb.Local;
using System.Text.Json;
using Vndb.Client;
using Vndb.Core;

namespace Vndb.Desktop.Services;
public sealed record UiPreferences(string Theme = "Default", bool ReducedMotion = false, string TitleLanguage = "japanese", bool ShowJapaneseSubtitle = true)
{
    [System.Text.Json.Serialization.JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}
public sealed class UiPreferencesStore(string directory)
{
    private string FilePath => Path.Combine(directory, "ui-preferences.json");
    public async Task<UiPreferences> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 64 * 1024) throw new InvalidDataException("界面偏好文件过大，原件保留。");
        var result = JsonSerializer.Deserialize<UiPreferences>(await File.ReadAllTextAsync(FilePath, ct), VndbJson.Options) ?? new();
        return result with { Theme = result.Theme is "Default" or "Light" or "Dark" ? result.Theme : "Default", TitleLanguage = NovelPresentation.ValidPreference(result.TitleLanguage) ? result.TitleLanguage : "japanese" };
    }
    public Task SaveAsync(UiPreferences preferences, CancellationToken ct = default) => JsonSnapshotStore.AtomicWriteAsync(FilePath, preferences, true, ct);
}
