using System.Text.Json;
using System.Text.Json.Nodes;
using Vndb.Contracts;
using Vndb.Cli;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class LocalLibraryIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static async Task<(LocalLibraryStore Store, byte[] Original)> SetupAsync(string directory)
    {
        var full = M5Tests.Item(); full = full with { Metadata = full.Metadata! with { Novel = full.Metadata.Novel! with { Description = new string('x', 30_000) } } };
        var raw = JsonSerializer.SerializeToNode(new LocalLibrary([], [full]), VndbJson.Options)!.AsObject();
        raw["future_root"] = "keep"; raw["items"]![0]!["future_view"] = 7; raw["items"]![0]!["item"]!["details"]!["future_details"] = "keep-details";
        raw["items"]![0]!["metadata"]!["future_metadata"] = "keep-metadata";
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "local-library.json"), raw, false, Ct);
        var store = new LocalLibraryStore(directory); return (store, await File.ReadAllBytesAsync(store.FilePath, Ct));
    }
    [Fact]
    public async Task ExplicitMigrationKeepsOriginalUnknownFieldsAndStableIdsWhileSummarySkipsDetails()
    {
        using var dir = new TestDirectory(); var (store, original) = await SetupAsync(dir.Path);
        var preview = await store.PreviewIndexAsync(Ct); Assert.False(File.Exists(store.IndexPath)); Assert.True(preview.IndexBytes < preview.LegacyBytes / 4);
        await store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, Ct));
        Assert.Null((await store.ReadSummaryAsync(Ct)).Items[0].Metadata!.Novel!.Description);
        Assert.Equal(30_000, (await store.ReadAsync(Ct)).Items[0].Metadata!.Novel!.Description!.Length);
        await store.UpdateAsync(v => v with { Items = v.Items.Select(i => i with { Item = i.Item with { Details = i.Item.Details with { CompanyHint = "Changed" } } }).ToArray() }, Ct);
        var index = JsonNode.Parse(await File.ReadAllBytesAsync(store.IndexPath, Ct))!;
        Assert.Equal("keep", (string?)index["library"]!["future_root"]); Assert.Equal(7, (int?)index["library"]!["items"]![0]!["future_view"]);
        Assert.Equal("keep-details", (string?)index["library"]!["items"]![0]!["item"]!["details"]!["future_details"]);
        var hash = (string)index["metadata_refs"]!["item-17"]!;
        var metadata = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(dir.Path, "local-metadata", hash + ".json"), Ct))!;
        Assert.Equal("keep-metadata", (string?)metadata["future_metadata"]); Assert.Equal("item-17", (await store.ReadAsync(Ct)).Items[0].Item.Id);
        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, Ct));
    }
    [Fact]
    public async Task SummarySaveAndConcurrentWritersCannotEraseDetailsOrOverwriteNewerIndex()
    {
        using var dir = new TestDirectory(); var (store, _) = await SetupAsync(dir.Path); var preview = await store.PreviewIndexAsync(Ct);
        await store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        var first = new LocalLibraryStore(dir.Path); var second = new LocalLibraryStore(dir.Path);
        var summary = await first.ReadSummaryAsync(Ct); await second.ReadAsync(Ct);
        await first.SaveAsync(summary with { Items = summary.Items.Select(i => i with { PlayCount = 4 }).ToArray() }, Ct);
        Assert.Equal(30_000, (await first.ReadAsync(Ct)).Items[0].Metadata!.Novel!.Description!.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => second.SaveAsync(summary, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => second.SaveAsync(summary, Ct)); // Failed CAS cannot grant a retry permission to overwrite.
    }
    [Fact]
    public async Task LegacyChangeAndStaleApprovalNeverCreateTwoWritableAuthorities()
    {
        using var dir = new TestDirectory(); var (store, original) = await SetupAsync(dir.Path); var preview = await store.PreviewIndexAsync(Ct);
        await File.AppendAllTextAsync(store.FilePath, " ", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct)); Assert.False(File.Exists(store.IndexPath));
        preview = await store.PreviewIndexAsync(Ct); await store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        await File.WriteAllBytesAsync(store.FilePath, original, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(v => v, Ct));
    }
    [Fact]
    public async Task MissingOrCorruptDetailNeverDestroysInstallationIndexAndCannotBeSilentlyReset()
    {
        using var dir = new TestDirectory(); var (store, _) = await SetupAsync(dir.Path); var preview = await store.PreviewIndexAsync(Ct);
        await store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        var bytes = await File.ReadAllBytesAsync(store.IndexPath, Ct); var file = Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "local-metadata")));
        await File.WriteAllTextAsync(file, "{}", Ct);
        Assert.Single((await store.ReadSummaryAsync(Ct)).Items);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(v => v with { Items = [] }, Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.IndexPath, Ct));
    }
    [Fact]
    public async Task OversizedLegacyIsOnlyAcceptedByExplicitBoundedMigrationAndIncompleteBlobIsNotAuthority()
    {
        using var dir = new TestDirectory(); var item = M5Tests.Item();
        var entries = Enumerable.Range(1, 1200).Select(n => item with { Item = item.Item with { Id = "synthetic-" + n }, Metadata = item.Metadata! with { Novel = item.Metadata.Novel! with { Description = new string('x', 8192) } } }).ToArray();
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(dir.Path, "local-library.json"), new LocalLibrary([], entries), false, Ct);
        var store = new LocalLibraryStore(dir.Path); await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Ct));
        var preview = await store.PreviewIndexAsync(Ct); Assert.True(preview.LegacyBytes > 8 * 1024 * 1024);
        Directory.CreateDirectory(Path.Combine(dir.Path, "local-metadata")); await File.WriteAllTextAsync(Path.Combine(dir.Path, "local-metadata", "unreferenced.json"), "{}", Ct);
        await store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        Assert.Equal(1200, (await store.ReadSummaryAsync(Ct)).Items.Count); Assert.True(File.Exists(store.FilePath));
    }
    [Fact]
    public async Task InterruptedBlobPreparationDoesNotSwitchAuthorityOrTouchUnrelatedEvidence()
    {
        using var dir = new TestDirectory(); var (store, original) = await SetupAsync(dir.Path);
        var evidence = new[] { "device.json", "launch-profiles.json", "presence-pending.json", "presence-receipts/synthetic.json", "organization-state/synthetic.json" };
        foreach (var path in evidence)
        { var file = Path.Combine(dir.Path, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, "{\"synthetic\":true}", Ct); }
        var preview = await store.PreviewIndexAsync(Ct);
        var metadata = JsonNode.Parse(original)!["items"]![0]!["metadata"]!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, VndbJson.Options); var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var blob = Path.Combine(dir.Path, "local-metadata", hash + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(blob)!); await File.WriteAllTextAsync(blob, "{}", Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct));
        Assert.False(File.Exists(store.IndexPath)); Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, Ct)); Assert.Single((await store.ReadAsync(Ct)).Items);
        await File.WriteAllBytesAsync(blob, bytes, Ct); await store.ApplyIndexAsync(preview, LocalArchiveFormat.Hash(preview), Ct);
        foreach (var path in evidence) Assert.Equal("{\"synthetic\":true}", await File.ReadAllTextAsync(Path.Combine(dir.Path, path), Ct));
    }
    [Fact]
    public async Task CliPreviewAndApprovedApplyUseTheSpecifiedDataRootWithoutNetworking()
    {
        using var dir = new TestDirectory(); var (store, original) = await SetupAsync(dir.Path);
        using var output = new StringWriter(); using var error = new StringWriter();
        var app = new CliApplication(output, error, environment: _ => null, settings: new());
        var file = Path.Combine(dir.Path, "synthetic-preview.json");
        Assert.Equal(0, await app.RunAsync(["local-data", "index-preview", "--identity-dir", dir.Path, "--json", "--output", file], Ct));
        var preview = JsonSerializer.Deserialize<LocalIndexPreview>(await File.ReadAllBytesAsync(file, Ct), VndbJson.Options)!;
        Assert.Equal(0, await app.RunAsync(["local-data", "index-apply", "--identity-dir", dir.Path, "--preview-json", file, "--approve", LocalArchiveFormat.Hash(preview), "--json"], Ct));
        Assert.True(File.Exists(store.IndexPath)); Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, Ct));
    }
}
