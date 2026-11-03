using Microsoft.Data.Sqlite;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class PlayTests
{
    internal static PlayDetails Details(string title = "作品名") => new(title, null, "completed",
        new DateOnly(2024, 2, 3), new DateOnly(2024, 2, 6), null, null, null, "中国語", "一週目", "zh");
    internal static PlayImportBatch Batch() => new("history.xls", new string('a', 64),
        [new(Details(), "2024", 11, "2024.2", "{\"title\":\"作品名\"}", []),
         new(Details() with { StartedOn = new(2025, 3, 4), FinishedOn = new(2025, 3, 5), ExtraNotes = "二週目" }, "2025", 9, "2025.3", "{\"replay\":true}", [])]);
    private static ILibraryStore Store(string kind, string path) => kind == "sqlite" ? new SqliteLibraryStore(path) : new JsonLibraryStore(path);

    [Theory]
    [InlineData("sqlite")]
    [InlineData("json")]
    public void ImportKeepsReplaysUnknownHoursNotesAndManualEditsOnRepeat(string kind)
    {
        using var dir = new TestDirectory();
        var store = Store(kind, dir.Path);
        store.Save(StorageTests.Entry());
        if (store is ICatalogStore catalog) catalog.SaveCatalogLookup(new(CatalogRules.Key("作品名"), "作品名", "作品名", "matched", "v17", "manual-confirmation", [], false, DateTimeOffset.UtcNow, null));
        var batch = Batch();
        var imported = store.ImportPlays(batch);
        Assert.Equal(2, imported.Inserted); Assert.Equal(2, imported.Linked);
        var all = store.Plays().Results;
        Assert.Equal(2, all.Count);
        Assert.All(all, record => { Assert.Null(record.Details.DurationMinutes); Assert.Null(record.Details.Score); Assert.Equal("v17", record.Details.NovelId); });
        Assert.Equal(1, store.Plays(year: 2024).Total);
        Assert.Equal(2, store.Plays(played: true).Total);
        var first = all[0];
        var edited = store.SavePlay(first.Details with { Score = 0, DurationMinutes = 0, Review = "我的评价 <tag>" }, first.Id, first.Revision);
        Assert.Equivalent(first.Source, edited.Source);
        Assert.Throws<PlayConflictException>(() => store.SavePlay(Details(), first.Id, first.Revision));
        var repeated = store.ImportPlays(batch);
        Assert.Equal(0, repeated.Inserted); Assert.Equal(2, repeated.Existing);
        var reopened = Store(kind, dir.Path);
        var saved = reopened.GetPlay(first.Id)!;
        Assert.Equal(0, saved.Details.Score); Assert.Equal(0, saved.Details.DurationMinutes);
        Assert.Equal("我的评价 <tag>", saved.Details.Review);
        Assert.Equal(1, reopened.PlayStatistics().Rated); Assert.Equal(1, reopened.PlayStatistics().KnownDurationCount);
        store.Save(StorageTests.Entry() with { Novel = StorageTests.Entry().Novel with { Title = "Refreshed metadata" } });
        Assert.Equal("我的评价 <tag>", store.GetPlay(first.Id)!.Details.Review);
        Assert.Throws<PlayConflictException>(() => store.ImportPlays(batch with { Rows = [batch.Rows[0]] }));
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("json")]
    public void UnmatchedAndUnplayedRecordsPersistWithoutInventedDates(string kind)
    {
        using var dir = new TestDirectory(); var store = Store(kind, dir.Path);
        var record = store.SavePlay(Details("未知作品") with { Status = "not_started", StartedOn = null, FinishedOn = null });
        Assert.False(record.HasPlayed); Assert.Equal(1, store.Plays(played: false, unmatched: true).Total);
        Assert.Equal(0, store.PlayStatistics().Played); Assert.Empty(store.PlayStatistics().Years);
        Assert.Equal(record, Store(kind, dir.Path).GetPlay(record.Id));
    }

    [Fact]
    public void SqliteImportFailureIsAtomicAndSchemaMigrationAndBackupPreservePersonalData()
    {
        using var dir = new TestDirectory();
        var store = new SqliteLibraryStore(dir.Path);
        var invalid = Batch() with { Rows = [Batch().Rows[0], Batch().Rows[1] with { Details = Details() with { Score = 11 } }] };
        Assert.Throws<ArgumentException>(() => store.ImportPlays(invalid));
        Assert.Equal(0, store.PlayStatistics().Total);
        store.ImportPlays(Batch());
        var backup = System.IO.Path.Combine(dir.Path, "backup", "library.db"); store.Backup(backup);
        var restored = new SqliteLibraryStore(System.IO.Path.GetDirectoryName(backup)!);
        Assert.Equal(2, restored.PlayStatistics().Total);
        using var db = new SqliteConnection("Data Source=" + backup + ";Pooling=False");
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", command.ExecuteScalar());
        command.CommandText = "PRAGMA user_version"; Assert.Equal(14L, command.ExecuteScalar());
    }

    [Fact]
    public void V2MigrationRetainsMetadataAndArchiveRecords()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.Save(StorageTests.Entry()); store.RegisterArchive(ArchiveTests.Registration("archived"));
        using (var db = new SqliteConnection("Data Source=" + System.IO.Path.Combine(dir.Path,"library.db") + ";Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE play_events; DROP TABLE play_import_batches; DROP TABLE play_records; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        var migrated = new SqliteLibraryStore(dir.Path);
        Assert.NotNull(migrated.Get("v17")); Assert.Single(migrated.Archives());
        Assert.Equal(2, migrated.ImportPlays(Batch()).Inserted);
    }

    [Fact]
    public void ValidatesDatesScoresDurationAndAssociation()
    {
        var d = Details();
        foreach (var invalid in new[] { d with { FinishedOn = d.StartedOn!.Value.AddDays(-1) }, d with { Score = -1 },
            d with { Score = 10.01m }, d with { Score = 1.11m }, d with { DurationMinutes = -1 },
            d with { Status = "not_started" }, d with { NovelId = "v99" } })
            Assert.Throws<ArgumentException>(() => PlayRules.Validate(invalid, _ => null));
        PlayRules.Validate(d with { Score = 10, DurationMinutes = 90 }, _ => null);
    }

    [Fact]
    public void AmbiguousNormalizedAliasesDoNotSilentlyAssignNovel()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        store.Save(StorageTests.Entry("v17")); store.Save(StorageTests.Entry("v4"));
        Assert.Equal(0, store.ImportPlays(Batch()).Linked);
        Assert.Equal(2, store.Plays(unmatched: true).Total);
    }
}
