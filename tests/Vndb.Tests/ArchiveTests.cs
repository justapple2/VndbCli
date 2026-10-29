using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class ArchiveTests
{
    internal static ArchiveRegistration Registration(string state = "planned") => new("v17", "p1", "WINDOWS-PC",
        @"E:\old", @"E:\会社\作品名", "run1", new string('a', 64), state,
        state == "planned" ? null : new string('b', 64), state == "planned" ? null : 5,
        state == "planned" ? null : 1234, state == "planned" ? null : DateTimeOffset.Parse("2026-10-20T14:00:00Z"));

    [Theory]
    [InlineData("sqlite")]
    [InlineData("json")]
    public void CatalogRequiresImportedNovelKeepsRelationsAndIsIdempotent(string kind)
    {
        using var directory = new TestDirectory();
        ILibraryStore store = kind == "sqlite" ? new SqliteLibraryStore(directory.Path) : new JsonLibraryStore(directory.Path);
        var registration = Registration();
        Assert.Throws<ArgumentException>(() => store.RegisterArchive(registration));
        store.Save(StorageTests.Entry());
        Assert.Throws<ArgumentException>(() => store.RegisterArchive(registration with { SelectedProducerId = "p99" }));
        var pending = store.RegisterArchive(registration);
        Assert.Equal(pending, store.RegisterArchive(registration));
        var archived = store.RegisterArchive(Registration("archived"));
        Assert.Equal(pending.Id, archived.Id);
        Assert.Equal(pending.CreatedAt, archived.CreatedAt);
        Assert.Equal(archived, store.RegisterArchive(Registration("archived")));
        Assert.Throws<ArgumentException>(() => store.RegisterArchive(registration));
        Assert.Single(store.Archives("v17", "run1"));
        Assert.Empty(store.Archives("v4"));
        Assert.Empty(store.Archives(runId: "other"));
        var reopened = kind == "sqlite" ? (ILibraryStore)new SqliteLibraryStore(directory.Path) : new JsonLibraryStore(directory.Path);
        Assert.Equal(archived, Assert.Single(reopened.Archives()));
        Assert.Equal(2, reopened.Get("v17")!.Producers.Count);
        if (kind == "sqlite")
        {
            using var db = new SqliteConnection("Data Source=" + System.IO.Path.Combine(directory.Path, "library.db") + ";Pooling=False");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM archive_events";
            Assert.Equal(2L, command.ExecuteScalar());
        }
    }

    [Fact]
    public void MigratesV1WithoutLosingNovelAndBackupContainsArchives()
    {
        using var directory = new TestDirectory();
        var store = new SqliteLibraryStore(directory.Path);
        store.Save(StorageTests.Entry());
        using (var db = new SqliteConnection("Data Source=" + System.IO.Path.Combine(directory.Path, "library.db") + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE archive_events; DROP TABLE archive_locations; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        var migrated = new SqliteLibraryStore(directory.Path);
        Assert.NotNull(migrated.Get("v17"));
        var value = migrated.RegisterArchive(Registration("archived"));
        var backup = System.IO.Path.Combine(directory.Path, "backup", "library.db");
        migrated.Backup(backup);
        Assert.Equal(value, Assert.Single(new SqliteLibraryStore(System.IO.Path.GetDirectoryName(backup)!).Archives()));
    }

    [Theory]
    [InlineData("relative/game")]
    [InlineData(@"E:\..\escape")]
    [InlineData("/games/../other")]
    public void RejectsNonAbsoluteAndTraversalPaths(string path) => Assert.Throws<ArgumentException>(() =>
        ArchiveRules.Validate(Registration() with { CurrentPath = path }, StorageTests.Entry()));

    [Fact]
    public void CompletedArchiveRequiresVerificationEvidence() => Assert.Throws<ArgumentException>(() =>
        ArchiveRules.Validate(Registration() with { State = "archived" }, StorageTests.Entry()));
}
