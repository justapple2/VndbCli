using Vndb.Local;
using System.Text.Json;
using Vndb.Client;
using Vndb.Core;

namespace Vndb.Desktop.Services;
public sealed record UiPreferences(string Theme = "Default", bool ReducedMotion = false);
public sealed class UiPreferencesStore(string directory)
{
    private string FilePath => Path.Combine(directory, "ui-preferences.json");
    public async Task<UiPreferences> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return new();
        var result = JsonSerializer.Deserialize<UiPreferences>(await File.ReadAllTextAsync(FilePath, ct), VndbJson.Options) ?? new();
        return result.Theme is "Default" or "Light" or "Dark" ? result : result with { Theme = "Default" };
    }
    public Task SaveAsync(UiPreferences preferences, CancellationToken ct = default) => JsonSnapshotStore.AtomicWriteAsync(FilePath, preferences, true, ct);
}
