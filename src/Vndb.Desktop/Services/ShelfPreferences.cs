using Vndb.Local;
using System.Text.Json;
using Vndb.Client;
using Vndb.Core;

namespace Vndb.Desktop.Services;

public sealed record ShelfPreferences(string Sort = "添加时间", bool Descending = true, string Company = "全部会社", string State = "全部状态", bool LocalOnly = false, bool ListMode = false, string Resource = "全部本机资源", bool FavoritesOnly = false, string LabelId = "");
public sealed record LaunchHistory(string ProfileId, string? InstallationId, string? ComponentId, DateTimeOffset OpenedAt);
public sealed class ShelfPreferencesStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath(string name) => Path.Combine(directory, name + ".json");
    public async Task<ShelfPreferences> ReadAsync(CancellationToken ct) => File.Exists(FilePath("shelf-preferences"))
        ? JsonSerializer.Deserialize<ShelfPreferences>(await File.ReadAllTextAsync(FilePath("shelf-preferences"), ct), VndbJson.Options) ?? new() : new();
    public Task SaveAsync(ShelfPreferences value, CancellationToken ct) => JsonSnapshotStore.AtomicWriteAsync(FilePath("shelf-preferences"), value, true, ct);
    public async Task<IReadOnlyList<LaunchHistory>> HistoryAsync(CancellationToken ct) => File.Exists(FilePath("launch-history"))
        ? JsonSerializer.Deserialize<LaunchHistory[]>(await File.ReadAllTextAsync(FilePath("launch-history"), ct), VndbJson.Options) ?? [] : [];
    public async Task OpenedAsync(Vndb.Local.LaunchProfile profile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var rows = (await HistoryAsync(ct)).Where(h => h.ProfileId != profile.Id).Append(new(profile.Id, profile.InstallationId, profile.ComponentId, DateTimeOffset.UtcNow)).ToArray();
            await JsonSnapshotStore.AtomicWriteAsync(FilePath("launch-history"), rows, true, ct);
        }
        finally { _gate.Release(); }
    }
}
