using Microsoft.Data.Sqlite;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

internal sealed class TestDirectory : IDisposable
{
    public static string ScratchRoot => System.IO.Path.Combine(Environment.GetEnvironmentVariable("VN_PRIVATE_ROOT") ?? DefaultPrivateRoot(), "test-scratch");
    private static string DefaultPrivateRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        System.IO.Path.GetFullPath("../../../VndbCli.private", System.IO.Path.GetDirectoryName(source)!);
    public string Path { get; } = System.IO.Path.Combine(ScratchRoot, "test-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public sealed class StorageTests
{
    internal static LibraryEntry Entry(string id = "v17")
    {
        Producer[] companies = [new() { Id = "p1", Name = "First", Original = "会社一", Type = "co" }, new() { Id = "p2", Name = "Second", Type = "ng" }];
        var novel = new VisualNovel
        {
            Id = id, Title = "Example", AltTitle = "作品名", Developers = companies, Released = "2026-10",
            Aliases = ["别名"], Titles = [new() { Lang = "ja", Title = "作品名", Main = true, Official = true }]
        };
        return new(novel, companies, null, "none", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static ILibraryStore Store(string kind, string path) => kind == "json" ? new JsonLibraryStore(path) : new SqliteLibraryStore(path);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("json")]
    public void PersistenceUpsertSearchAndProducerRelations(string kind)
    {
        using var directory = new TestDirectory();
        var store = Store(kind, directory.Path);
        var entry = Entry();
        store.Save(entry);
        var reopened = Store(kind, directory.Path);
        Assert.Equal("2026-10", reopened.Get("v17")!.Novel.Released);
        Assert.Equal(2, reopened.Get("v17")!.Producers.Count);
        Assert.Single(reopened.List("别名", "p2").Results);
        Assert.Single(reopened.List("EXAMPLE", "").Results);
        Assert.Empty(reopened.List("' OR 1=1 --").Results);
        var updated = entry with { Novel = entry.Novel with { Title = "Updated", Developers = [entry.Producers[1]] }, Producers = [entry.Producers[1]], UpdatedAt = DateTimeOffset.UtcNow };
        reopened.Save(updated);
        Assert.Equal(1, store.Statistics().Novels);
        Assert.Equal(1, store.Statistics().Producers);
        Assert.Empty(store.List(producer: "p1").Results);
        Assert.Equal("Updated", store.Get("v17")!.Novel.Title);
        Assert.Equal(entry.CreatedAt, store.Get("v17")!.CreatedAt);
    }

    [Fact]
    public void FailedTransactionDoesNotReplacePreviousMetadataOrRelations()
    {
        using var directory = new TestDirectory();
        var store = new SqliteLibraryStore(directory.Path);
        var entry = Entry();
        store.Save(entry);
        var broken = entry with { Novel = entry.Novel with { Title = "Must roll back", Titles = [entry.Novel.Titles[0], entry.Novel.Titles[0]] } };
        Assert.Throws<SqliteException>(() => store.Save(broken));
        Assert.Equal("Example", store.Get("v17")!.Novel.Title);
        Assert.Equal(2, store.Producers().Count);
    }

    [Fact]
    public void OnlineBackupCanBeOpenedAsASeparateLibrary()
    {
        using var directory = new TestDirectory();
        var store = new SqliteLibraryStore(directory.Path);
        store.Save(Entry());
        var backupDirectory = System.IO.Path.Combine(directory.Path, "backup");
        store.Backup(System.IO.Path.Combine(backupDirectory, "library.db"));
        var restored = new SqliteLibraryStore(backupDirectory);
        Assert.Equal("Example", restored.Get("v17")!.Novel.Title);
        Assert.Equal(2, restored.Statistics().Producers);
        Assert.Throws<IOException>(() => store.Backup(System.IO.Path.Combine(backupDirectory, "library.db")));
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public void RejectsInvalidPagination(int page, int limit) => Assert.Throws<ArgumentException>(() => LibraryRules.ValidatePage(page, limit));

    [Fact]
    public void StoredCoverPathCannotEscapeDataDirectory()
    {
        var asset = new CoverAsset(new string('a', 64), "../outside.jpg", "image/jpeg", 1, "https://t.vndb.org/a.jpg", DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() => LibraryRules.CoverPath("data", asset));
    }
}
