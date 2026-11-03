using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class LocalResourceArchiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ResourceRecordsKeepExactItemIdentityAndNeverCreateInstallationsOrOwnership()
    {
        using var dir = new TestDirectory(); var library = new LocalLibraryStore(dir.Path); var item = M5Tests.Item(); await library.SaveAsync(new([], [item]), Ct);
        var store = new LocalResourceArchive(library, "synthetic-device");
        var data = new LocalResourceData(item.Item.Id, "patch", dir.Path, "patch.part1.rar", Language: "ja", ReleaseId: "r17", Version: "not verified");
        var resource = await store.SaveAsync(data, ct: Ct); Assert.Equal("unreviewed", resource.Details.Verification); Assert.False(resource.Details.ManuallyApplied);
        Assert.Equal("missing", LocalResourceArchive.Inspect(resource).Status); Assert.Empty((await library.ReadAsync(Ct)).Installations); Assert.Null((await library.ReadAsync(Ct)).Packages);
        var restored = Assert.Single(await new LocalResourceArchive(library, "synthetic-device").ReadAsync(item.Item.Id, Ct)); Assert.Equal(resource.Id, restored.Id);
        var update = await store.SaveAsync(data with { Verification = "incompatible", Notes = "Keep uncertain split archive" }, resource.Id, resource.Revision, Ct);
        Assert.Equal(2, update.Revision); Assert.Equal(resource.Id, update.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(data, resource.Id, resource.Revision, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(data with { ItemId = "another-edition" }, ct: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(data with { InstallationId = "another-install" }, ct: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(data with { RelativePath = "../outside" }, ct: Ct));
        Assert.Empty(await new LocalResourceArchive(library, "another-device").ReadAsync(ct: Ct));
    }
    [Fact]
    public async Task ExplicitFileHashIsCancelableAndBecomesStaleWithoutApplyingAnything()
    {
        using var dir = new TestDirectory(); var library = new LocalLibraryStore(Path.Combine(dir.Path, "state")); var item = M5Tests.Item();
        await library.SaveAsync(new([], [item]), Ct); var file = Path.Combine(dir.Path, "synthetic-patch.bin"); await File.WriteAllTextAsync(file, "Synthetic content", Ct);
        var store = new LocalResourceArchive(library, "synthetic-device"); var resource = await store.SaveAsync(new(item.Item.Id, "patch", dir.Path, Path.GetFileName(file)), ct: Ct);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.HashAsync(resource.Id, resource.Revision, cancel.Token));
        Assert.Null(Assert.Single(await store.ReadAsync(ct: Ct)).Sha256);
        var hashed = await store.HashAsync(resource.Id, resource.Revision, Ct); Assert.Equal(64, hashed.Sha256!.Length); Assert.True(LocalResourceArchive.Inspect(hashed).HashCurrent);
        await File.AppendAllTextAsync(file, " change", Ct); Assert.False(LocalResourceArchive.Inspect(hashed).HashCurrent);
        Assert.False(hashed.Details.ManuallyApplied); Assert.Equal("unreviewed", hashed.Details.Verification);
        var movedDescription = await store.SaveAsync(hashed.Details with { RelativePath = "different.bin" }, hashed.Id, hashed.Revision, Ct); Assert.Null(movedDescription.Sha256); Assert.True(File.Exists(file));
    }
    [Fact]
    public async Task MissingRootIsUnknownAndWebShelfItemCanBeExplicitlyReferencedWithoutInstalling()
    {
        using var dir = new TestDirectory(); var library = new LocalLibraryStore(Path.Combine(dir.Path, "state")); var item = M5Tests.Item();
        var store = new LocalResourceArchive(library, "synthetic-device");
        var resource = await store.SaveAsync(new(item.Item.Id, "bonus", Path.Combine(dir.Path, "offline-root"), "bonus.zip"), ct: Ct, selectedItem: item);
        Assert.Equal("unknown", LocalResourceArchive.Inspect(resource).Status); Assert.Empty((await library.ReadAsync(Ct)).Installations);
        Assert.False(WirePrivacy.ContainsPrivateData(System.Text.Json.JsonSerializer.SerializeToElement(new PersonalMembership(item.Item.Id, true, []), VndbJson.Options)));
        Assert.True(WirePrivacy.ContainsPrivateData(System.Text.Json.JsonSerializer.SerializeToElement(resource, VndbJson.Options)));
    }
    [Fact]
    public async Task EditingResourceDescriptionPreservesFutureFieldsAndDoesNotBorrowAnotherItemsPackage()
    {
        using var dir = new TestDirectory(); var library = new LocalLibraryStore(dir.Path); var item = M5Tests.Item();
        await library.SaveAsync(new([], [item]), Ct); var store = new LocalResourceArchive(library, "synthetic-device");
        var data = new LocalResourceData(item.Item.Id, "document", dir.Path, "notes.txt")
        { Extensions = new() { ["future_field"] = System.Text.Json.JsonSerializer.SerializeToElement("keep") } };
        var row = await store.SaveAsync(data, ct: Ct);
        var saved = await store.SaveAsync(new(item.Item.Id, "document", dir.Path, "notes.txt", Notes: "Edited"), row.Id, row.Revision, Ct);
        Assert.Equal("keep", saved.Details.Extensions!["future_field"].GetString());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(data with { PackageId = "not-this-items-package" }, ct: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(data with { SourceUrl = "file:///synthetic/file" }, ct: Ct));
    }
}
