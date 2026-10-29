using System.Text.Json;
using System.Text.Json.Serialization;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record LocalLibrary(IReadOnlyList<LocalInstallationView> Installations, IReadOnlyList<LibraryItemView> Items,
    string Format = "vn-local-library", int FormatVersion = 1)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LocalOwnedPackage>? Packages { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>Filesystem authority lives exclusively in this local JSON. It is never an API request body.</summary>
public sealed class LocalLibraryStore(string directory)
{
    private readonly JsonStateFile<LocalLibrary> _state = new(Path.GetFullPath(directory), "local-library.json");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "local-library.json");
    public async Task<LocalLibrary> ReadAsync(CancellationToken ct = default)
    {
        var library = await _state.ReadAsync(ct);
        if (library is null) return new([], []);
        Validate(library);
        return library;
    }
    public Task SaveAsync(LocalLibrary library, CancellationToken ct = default)
    { Validate(library); return _state.SaveAsync(library, ct); }
    public Task<LocalLibrary> UpdateAsync(Func<LocalLibrary, LocalLibrary> update, CancellationToken ct = default) =>
        _state.UpdateAsync(current => { current ??= new([], []); Validate(current); var changed = update(current); Validate(changed); return changed; }, ct);
    private static void Validate(LocalLibrary library)
    {
        if (library.Format != "vn-local-library" || library.FormatVersion != 1 || library.Installations is null || library.Items is null)
            throw new InvalidDataException("本机资料格式不兼容。");
        OwnedPackages.Validate(library.Packages ?? []);
    }
}
