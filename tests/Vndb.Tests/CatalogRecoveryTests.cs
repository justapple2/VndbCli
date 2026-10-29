using System.Text.Json;
using System.Text.Json.Nodes;
using Vndb.Library;
using Vndb.Core;
using Xunit;

namespace Vndb.Tests;

public sealed class CatalogRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task VerifiedPausedImportRestoresInputsCursorPersonalRowsAndCompletesWithoutNetwork()
    {
        using var dir = new TestDirectory(); var live = Path.Combine(dir.Path, "live"); var store = new SqliteLibraryStore(live);
        store.Save(StorageTests.Entry()); store.ImportPlays(PlayTests.Batch());
        File.WriteAllText(Path.Combine(live, "service-instance-id"), Guid.NewGuid().ToString("D"));
        var personal = JsonSerializer.Serialize(store.Plays(), VndbJson.Options);
        using var http = new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException("Offline recovery must not use network")));
        using var service = new DatasetImports(store, http);
        await using (var stream = File.OpenRead(DumpPackageTests.Archive(dir.Path))) await service.UploadAsync(stream, "upload", Ct);
        await service.StepAsync(Ct); await service.StepAsync(Ct);
        var job = store.DatasetJobs()[0]; service.Control(job.Id, "pause", new(job.Revision), "pause");
        var ignored = Path.Combine(live, "datasets", new string('a', 32) + ".zst"); File.WriteAllText(ignored, "unregistered temporary bytes");
        var snapshot = Path.Combine(dir.Path, "snapshot");
        var manifest = LibrarySnapshot.Create(live, snapshot, "synthetic-image", cancellationToken: Ct, includeDatasetInputs: true);
        Assert.Equal("vn-library-snapshot-v2", manifest.Format); Assert.True(manifest.DatasetInputsIncluded); Assert.Equal(1, manifest.Catalog!.ActiveDatasets);
        Assert.Contains(manifest.Files.Keys, p => p.StartsWith("datasets/stage-", StringComparison.Ordinal));
        Assert.DoesNotContain("datasets/" + Path.GetFileName(ignored), manifest.Files.Keys);
        var target = Path.Combine(dir.Path, "restore"); LibrarySnapshot.Restore(snapshot, target, cancellationToken: Ct);
        var restored = new SqliteLibraryStore(target); Assert.Equal(personal, JsonSerializer.Serialize(restored.Plays(), VndbJson.Options));
        using var worker = new DatasetImports(restored, http); var paused = restored.DatasetJobs()[0]; Assert.Equal(job.Processed, paused.Processed);
        worker.Control(paused.Id, "resume", new(paused.Revision), "resume");
        for (var i = 0; i < 20 && restored.DatasetJobs()[0].State != "completed"; i++) await worker.StepAsync(Ct);
        Assert.Equal("completed", restored.DatasetJobs()[0].State); Assert.Equal(personal, JsonSerializer.Serialize(restored.Plays(), VndbJson.Options));
        Assert.NotNull(restored.Get("v17")!.Novel.ReleaseCatalog);
        Assert.Equal(JsonSerializer.Serialize(manifest, VndbJson.Options), JsonSerializer.Serialize(LibrarySnapshot.Verify(snapshot), VndbJson.Options));
        var complete = LibrarySnapshot.Create(target, Path.Combine(dir.Path, "completed"), "synthetic-image", cancellationToken: Ct, includeDatasetInputs: true);
        Assert.Equal(1, complete.Catalog!.CompletedDatasets); Assert.Equal(0, complete.Catalog.ActiveDatasets);
        Assert.DoesNotContain(complete.Files.Keys, p => p.StartsWith("datasets/", StringComparison.Ordinal));
        var raw = manifest.Files.Keys.Single(p => p.StartsWith("datasets/", StringComparison.Ordinal) && p.EndsWith(".zst", StringComparison.Ordinal));
        File.AppendAllText(Path.Combine(snapshot, raw), "corruption");
        Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Restore(snapshot, Path.Combine(dir.Path, "rejected"), cancellationToken: Ct));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "rejected")));
    }
    [Fact]
    public void LegacyV1StillVerifiesAndV2CatalogTamperingFailsClosed()
    {
        using var dir = new TestDirectory(); var source = Path.Combine(dir.Path, "live"); var store = new SqliteLibraryStore(source); store.Save(StorageTests.Entry());
        File.WriteAllText(Path.Combine(source, "service-instance-id"), Guid.NewGuid().ToString("D"));
        var snapshot = Path.Combine(dir.Path, "snapshot"); LibrarySnapshot.Create(source, snapshot, "synthetic-image", cancellationToken: Ct);
        var file = Path.Combine(snapshot, "snapshot-manifest.json"); var manifest = JsonNode.Parse(File.ReadAllText(file))!;
        manifest["catalog"]!["novels"] = 200; File.WriteAllText(file, manifest.ToJsonString());
        Assert.Throws<InvalidDataException>(() => LibrarySnapshot.Verify(snapshot));
        manifest["format"] = "vn-library-snapshot-v1"; manifest.AsObject().Remove("catalog"); manifest.AsObject().Remove("dataset_inputs_included");
        File.WriteAllText(file, manifest.ToJsonString()); Assert.Null(LibrarySnapshot.Verify(snapshot).Catalog);
        LibrarySnapshot.Restore(snapshot, Path.Combine(dir.Path, "restored"), cancellationToken: Ct);
    }
}
