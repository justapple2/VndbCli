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
public sealed partial class LocalLibraryStore(string directory)
{
    private readonly JsonStateFile<LocalLibrary> _state = new(Path.GetFullPath(directory), "local-library.json");
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "local-library.json");
    public async Task<LocalLibrary> ReadAsync(CancellationToken ct = default)
    {
        if (File.Exists(IndexPath)) return await ReadIndexAsync(true, ct);
        var library = await _state.ReadAsync(ct);
        if (library is null) return new([], []);
        Validate(library);
        return library;
    }
    public async Task SaveAsync(LocalLibrary library, CancellationToken ct = default)
    {
        Validate(library);
        using var lease = await LocalFileLease.AcquireAsync(DirectoryPath, "local-library-format", ct);
        if (File.Exists(IndexPath)) await SaveIndexAsync(library, ct);
        else await _state.SaveAsync(library, ct);
    }
    public async Task<LocalLibrary> UpdateAsync(Func<LocalLibrary, LocalLibrary> update, CancellationToken ct = default)
    {
        using var lease = await LocalFileLease.AcquireAsync(DirectoryPath, "local-library-format", ct);
        if (!File.Exists(IndexPath)) return await _state.UpdateAsync(current => { current ??= new([], []); Validate(current); var changed = update(current); Validate(changed); return changed; }, ct);
        var prior = await ReadIndexAsync(true, ct); var value = update(prior); Validate(value); await SaveIndexAsync(value, ct); return value;
    }
    public async Task<LocalLibrary> ReadSummaryAsync(CancellationToken ct = default) => File.Exists(IndexPath)
        ? await ReadIndexAsync(false, ct) : await ReadAsync(ct);
    private static void Validate(LocalLibrary library)
    {
        if (library.Format != "vn-local-library" || library.FormatVersion != 1 || library.Installations is null || library.Items is null)
            throw new InvalidDataException("本机资料格式不兼容。");
        if (library.Items.Any(i => i?.Item?.Details is null || string.IsNullOrWhiteSpace(i.Item.Id) || i.Metadata is { Developers: null }) ||
            library.Installations.Any(i => i?.Installation?.Details is null || i.Components is null || i.Components.Any(c => c?.Details is null)))
            throw new InvalidDataException("本机登记缺少条目或安装字段，原件保留。");
        if (library.Items.Select(i => i.Item.Id).Distinct(StringComparer.Ordinal).Count() != library.Items.Count) throw new InvalidDataException("本机登记条目ID重复，原件保留。");
        OwnedPackages.Validate(library.Packages ?? []);
    }
}
