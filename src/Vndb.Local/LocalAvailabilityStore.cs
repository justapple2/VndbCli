using System.Text.Json;
using System.Text.Json.Nodes;
using Vndb.Core;

namespace Vndb.Local;

public sealed partial class LocalLibraryStore
{
    // Observation is allowed to update availability only. Metadata blobs are never hydrated here.
    internal async Task<LocalLibrary> UpdateAvailabilityAsync(Func<LocalLibrary, LocalLibrary> update, CancellationToken ct)
    {
        using var format = await LocalFileLease.AcquireAsync(DirectoryPath, "local-library-format", ct);
        LocalLibrary Change(LocalLibrary before)
        {
            Validate(before); var after = update(before); Validate(after);
            if (JsonSerializer.Serialize(before with { Installations = [], Packages = null }, VndbJson.Options) != JsonSerializer.Serialize(after with { Installations = [], Packages = null }, VndbJson.Options))
                throw new InvalidDataException("可用性事务不能修改作品或资料引用。");
            return after;
        }
        if (!File.Exists(IndexPath))
        {
            var state = new JsonStateFile<JsonObject>(DirectoryPath, "local-library.json");
            var raw = await state.UpdateAsync(node =>
            {
                node ??= Object(new LocalLibrary([], [])); var before = ReadNode<LocalLibrary>(node); var after = Change(before);
                return MergeUnknown(node, Object(before), Object(after))!.AsObject();
            }, ct);
            return ReadNode<LocalLibrary>(raw);
        }
        using var lease = await LocalFileLease.AcquireAsync(DirectoryPath, "json-state/local-library.v2.json", ct);
        var envelope = await ReadIndexNodeAsync(ct); var original = envelope["library"]!;
        var prior = ReadNode<LocalLibrary>(original); var changed = Change(prior);
        var merged = MergeUnknown(original, Object(prior), Object(changed));
        if (!JsonNode.DeepEquals(original, merged))
        {
            envelope["library"] = merged;
            await JsonSnapshotStore.AtomicWriteAsync(IndexPath, envelope, true, ct, IndexMaxBytes);
            _indexObserved = Sha(await IndexState().BytesAsync(ct));
        }
        return changed;
    }
}
