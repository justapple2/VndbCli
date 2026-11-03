using Vndb.Local;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class M2Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static MetadataSnapshot Snapshot(string id = "v17", bool image = false) => new(id, "作品名", "vn",
        new VisualNovel { Id = id, Title = "作品名", Description = "完整离线简介", Titles = [new() { Lang = "ja", Title = "作品名", Main = true }],
            Developers = [new ProducerSummary { Id = "p1", Name = "Studio" }], Image = image ? new() { Id = "cv1", Url = "https://t.vndb.org/cv/01/1.jpg" } : null },
        [new() { Id = "p1", Name = "Studio", Description = "会社详情", Aliases = ["会社别名"], Lang = "ja", Type = "co" }], null,
        new(image ? "not_downloaded" : "none"), DateTimeOffset.Parse("2026-10-20T00:00:00Z"), "client_vndb");
    private static JsonElement Payload(MetadataSnapshot? snapshot = null, string? batchId = null) =>
        JsonSerializer.SerializeToElement(new MetadataBatch([snapshot ?? Snapshot()], DateTimeOffset.UtcNow, batchId ?? "test-batch"), VndbJson.Options);
    private static ImportBatchReceipt Preview(SqliteLibraryStore store, JsonElement payload, bool shelf = false)
    {
        var parsed = MetadataInput.Parse(payload, new()); return store.PreviewMetadata(parsed.Items, parsed.ClientId, parsed.RawSha, shelf);
    }
    private static void Process(SqliteLibraryStore store) { for (var i = 0; i < 501 && store.ProcessMetadataItem(); i++) { } }
    private static long Count(string directory, string table)
    {
        using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "library.db") + ";Pooling=False"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM " + table; return (long)command.ExecuteScalar()!;
    }
    private static PersonalPlayData Play(string? novelId = null) => new("我的标题", novelId, "completed", new(2024, 1, 2), new(2024, 1, 5), null, null, "保留评价", "备注一", "备注二", "zh");
    private sealed class Factory(string directory, StubHandler upstream) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Library:DataDirectory", directory).UseSetting("Library:WriteKey", "m2-test-key").UseSetting("CatalogJobs:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<VndbClient>(); services.RemoveAll<CoverDownloader>();
                services.AddSingleton(_ => new VndbClient(new HttpClient(upstream, false), VndbClientTests.FastOptions));
                services.AddSingleton(_ => new CoverDownloader(new HttpClient(new StubHandler((_, _) => Task.FromResult(ImportTests.Image()))), directory));
            });
        }
    }
    private static void Auth(HttpClient http)
    { http.DefaultRequestHeaders.Add("X-Library-Request", "1"); http.DefaultRequestHeaders.Add("X-Library-Key", "m2-test-key"); http.DefaultRequestHeaders.Add("Idempotency-Key", "test-operation"); }

    [Fact]
    public void OfflineSnapshotPersistsAllDetailsAndProvenanceAndRetriesHaveOneEffect()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var input = Payload(); var preview = Preview(store, input, true);
        Assert.False(preview.Accepted); Assert.Null(store.Get("v17"));
        store.CommitMetadata(preview.Id, "commit-one"); Process(store);
        var result = store.GetMetadataBatch(preview.Id); Assert.True(result.Accepted); Assert.Equal("complete", result.State);
        Assert.Equal("imported", Assert.Single(result.Items).Status);
        var entry = store.Get("v17")!; Assert.Equal("完整离线简介", entry.Novel.Description); Assert.Equal("会社详情", entry.Producers[0].Description);
        Assert.Equal("client_vndb_import", entry.Provenance); Assert.Equal(Snapshot().FetchedAt, entry.UpdatedAt);
        Assert.Null(store.Provenance("v17")!.VerifiedAt); Assert.Single(store.PersonalItems());
        Assert.Equal(1, Count(dir.Path, "import_events")); Assert.Equal(1, Count(dir.Path, "metadata_operations"));
        var again = Preview(store, input); store.CommitMetadata(again.Id, "sync-same-batch"); Process(store);
        var differentAlias = JsonSerializer.SerializeToElement(new MetadataBatch([Snapshot()], DateTimeOffset.UtcNow, "another-batch"), VndbJson.Options);
        var alias = Preview(store, differentAlias); store.CommitMetadata(alias.Id, "sync-other-alias"); Process(store);
        Assert.Equal(result.Id, alias.Id); Assert.Single(store.PersonalItems()); Assert.Equal(1, Count(dir.Path, "import_events"));
        var removed = store.SetItemRemoved(store.PersonalItems()[0].Id, new(1), true, "remove-item");
        Preview(store, input, true); store.CommitMetadata(preview.Id, "import-after-remove"); Process(store);
        Assert.Equal(removed, store.GetItem(removed.Id));
    }

    [Fact]
    public void SnapshotCannotOverwriteVerifiedOrNewerMetadataWithoutExplicitRevisionGuardedReview()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var book = store.SaveItem(new(new("个人显示", "v17", TitleOverride: "手工名称", Notes: "个人备注")), null, "new-book");
        var future = Snapshot() with { FetchedAt = DateTimeOffset.UtcNow.AddYears(10) };
        var preview = Preview(store, Payload(future)); store.CommitMetadata(preview.Id, "commit-future"); Process(store);
        var result = Assert.Single(store.GetMetadataBatch(preview.Id).Items); Assert.Equal("needs_review", result.Status);
        Assert.Equal("Example", store.Get("v17")!.Novel.Title); Assert.Equal(book, store.GetItem(book.Id));
        var candidate = store.MetadataCandidates().Single(c => c.Id == result.CandidateId);
        store.Save(StorageTests.Entry() with { Novel = StorageTests.Entry().Novel with { Title = "New upstream" } });
        Assert.Throws<PlayConflictException>(() => store.DecideCandidate(candidate.Id, new(candidate.ExpectedRevision), true, "accept-stale"));
        Assert.Equal("New upstream", store.Get("v17")!.Novel.Title); Assert.Equal(book, store.GetItem(book.Id));
        store.DecideCandidate(candidate.Id, new(candidate.ExpectedRevision), false, "reject-old");
        Assert.Equal("rejected", store.MetadataCandidates().Single(c => c.Id == candidate.Id).State);
    }

    [Fact]
    public void ExplicitCandidateAcceptancePreservesManualFieldsAndProducerConflictsAreNotSilentlyApplied()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var book = store.SaveItem(new(new("个人名称", "v17", Notes: "book note")), null, "new");
        var preview = Preview(store, Payload()); store.CommitMetadata(preview.Id, "commit"); Process(store);
        var candidate = store.MetadataCandidates().Single(c => c.VndbId == "v17");
        var accepted = store.DecideCandidate(candidate.Id, new(candidate.ExpectedRevision), true, "accept");
        Assert.Equal("accepted", accepted.State); Assert.Equal("完整离线简介", store.Get("v17")!.Novel.Description); Assert.Equal(book, store.GetItem(book.Id));
        Assert.Equal("client_vndb_import", store.Provenance("v17")!.Source); Assert.Null(store.Provenance("v17")!.VerifiedAt);
        Assert.Contains(store.MetadataCandidates(), c => c.VndbId == "p1" && c.State == "pending");
        Assert.Equal(StorageTests.Entry().Producers[0].Name, store.GetProducer("p1")!.Producer.Name);
        var imported = store.Get("v17")!; var revision = store.Provenance("v17")!.Revision;
        store.Save(StorageTests.Entry());
        Assert.Throws<PlayConflictException>(() => store.SaveEnrichment(imported, revision, imported.Producers));
        Assert.Equal("Example", store.Get("v17")!.Novel.Title);
    }

    [Fact]
    public void ListingIsDurableAndRestartAndPauseRetainCursorWithoutFakingFullMetadata()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var payload = JsonSerializer.SerializeToElement(new { format = "vn-library-batch", format_version = 1, kind = "metadata", batch_id = "listing", items = new object[] { new { title = "尚未收录" }, new { vndb_id = "v17", title = "作品名" } } });
        var preview = Preview(store, payload, true); store.CommitMetadata(preview.Id, "commit"); Assert.True(store.ProcessMetadataItem());
        store.PauseMetadata(preview.Id, true, "pause"); Assert.False(store.ProcessMetadataItem()); Assert.Equal(1, store.GetMetadataBatch(preview.Id).Completed);
        var restarted = new SqliteLibraryStore(dir.Path); restarted.PauseMetadata(preview.Id, false, "resume"); Process(restarted);
        Assert.Equal(2, restarted.GetMetadataBatch(preview.Id).Completed); Assert.Equal(0, store.Statistics().Novels);
        Assert.Equal(2, store.MetadataJobs().Count); Assert.Equal(2, store.PersonalItems().Count);
        Assert.All(store.GetMetadataBatch(preview.Id).Items, i => Assert.Equal("queued", i.Status));
    }

    [Fact]
    public void UnmatchedItemsPlayCrudLinkCorrectionAndRestoreKeepIdentityAndPersonalFields()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var item = store.SaveItem(new(new("未收录游戏", Notes: "备注一", ExtraNotes: "后续备注")), null, "create-item");
        Assert.Equal(item, store.SaveItem(new(item.Details), null, "create-item"));
        Assert.Throws<PlayConflictException>(() => store.SaveItem(new(new("不同内容")), null, "create-item"));
        var play = store.SavePersonalPlay(new(Play(), item.Id), null, "new-play");
        var replay = store.SavePersonalPlay(new(Play() with { ExtraNotes = "重玩" }, item.Id), null, "replay");
        Assert.Equal(2, store.ItemPlays(item.Id).Count); Assert.NotEqual(play.Id, replay.Id);
        Assert.Single(store.PersonalItems());
        var linked = store.LinkItem(item.Id, new("v17", item.Revision), "link"); Assert.Equal(item.Id, linked.Id);
        var after = store.GetPlay(play.Id)!; Assert.Equal("v17", after.Details.NovelId);
        Assert.Equal(play.Details with { NovelId = "v17" }, after.Details); Assert.Null(after.Details.DurationMinutes); Assert.Null(after.Details.Score);
        Assert.Throws<PlayConflictException>(() => store.SaveItem(new(linked.Details, item.Revision), item.Id, "stale-edit"));
        var removed = store.SetPlayRemoved(after.Id, new(after.Revision), true, "remove-play");
        Assert.Single(store.ItemPlays(item.Id)); Assert.NotNull(store.GetPlay(removed.Id)!.DeletedAt);
        Assert.Equal(removed, store.SetPlayRemoved(after.Id, new(after.Revision), true, "remove-play"));
        var restored = store.SetPlayRemoved(removed.Id, new(removed.Revision), false, "restore-play"); Assert.Equal(after.Details, restored.Details);
        var removedItem = store.SetItemRemoved(item.Id, new(linked.Revision), true, "remove-book");
        Assert.Equal(2, store.ItemPlays(item.Id).Count); Assert.NotNull(store.Get("v17"));
        var restoredItem = store.SetItemRemoved(item.Id, new(removedItem.Revision), false, "restore-book"); Assert.Equal(linked.Details, restoredItem.Details);
        Assert.True(Count(dir.Path, "business_events") >= 10);
    }

    [Fact]
    public void ExcelReimportDoesNotResurrectRemovedRecordsOrLoseSourceOrManualEdits()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.ImportPlays(PlayTests.Batch());
        var original = store.Plays().Results[0];
        var edited = store.SavePlay(original.Details with { Score = 8.5m, DurationMinutes = 120, Review = "手工评价" }, original.Id, original.Revision);
        var removed = store.SetPlayRemoved(edited.Id, new(edited.Revision), true, "remove");
        store.ImportPlays(PlayTests.Batch()); Assert.Equivalent(removed, store.GetPlay(removed.Id)); Assert.Equal(1, store.Plays().Total);
        var restored = store.SetPlayRemoved(removed.Id, new(removed.Revision), false, "restore");
        Assert.Equal(edited.Details, restored.Details); Assert.Equivalent(original.Source, restored.Source); Assert.Equal(2, store.Plays().Total);
    }

    [Theory]
    [InlineData("{\"format\":\"vn-library-batch\",\"format_version\":2,\"kind\":\"metadata\",\"items\":[{}]}")]
    [InlineData("{\"items\":[{}]}")]
    [InlineData("{\"format\":\"vn-library-batch\",\"format_version\":\"1\",\"kind\":\"metadata\",\"items\":[{}]}")]
    [InlineData("{\"format\":\"vn-library-batch\",\"format_version\":false,\"kind\":\"metadata\",\"items\":[{}]}")]
    [InlineData("{\"format\":\"vn-library-batch\",\"format\":\"evil\"}")]
    public void InvalidEnvelopeIsRejected(string json)
    { using var document = JsonDocument.Parse(json); Assert.Throws<ArgumentException>(() => MetadataInput.Parse(document.RootElement, new())); }

    [Fact]
    public void MaliciousImageAndMismatchedIdentityBecomeExplicitErrorsWithoutAccessingPaths()
    {
        var invalid = Snapshot() with { Novel = Snapshot().Novel! with { Image = new() { Id = "cv1", Url = "http://192.0.2.10/secrets" } } };
        Assert.NotNull(Assert.Single(MetadataInput.Parse(Payload(invalid), new()).Items).Error);
        Assert.NotNull(Assert.Single(MetadataInput.Parse(Payload(Snapshot() with { VndbId = "v2" }), new()).Items).Error);
        var invalidProducer = System.Text.Json.Nodes.JsonNode.Parse(Payload().GetRawText())!;
        invalidProducer["items"]![0]!["developers"]![0]!["aliases"] = null;
        Assert.NotNull(Assert.Single(MetadataInput.Parse(JsonSerializer.SerializeToElement(invalidProducer), new()).Items).Error);
        Assert.Throws<ArgumentException>(() => MetadataInput.Parse(Payload(), new(MaximumBytes: 10)));
        var tooMany = JsonSerializer.SerializeToElement(new { format = "vn-library-batch", format_version = 1, kind = "metadata", items = Enumerable.Range(0, 501).Select(i => new { title = "Title" + i }).ToArray() });
        Assert.Equal("import_item_limit", Assert.Throws<ImportValidationException>(() => MetadataInput.Parse(tooMany, new())).Code);
    }

    [Fact]
    public void KnownLegacyExportsNormalizeAndIncompleteDataOnlyQueues()
    {
        var oldWeb = JsonSerializer.SerializeToElement(StorageTests.Entry(), VndbJson.Options);
        var full = Assert.Single(MetadataInput.Parse(oldWeb, new()).Items); Assert.NotNull(full.Snapshot); Assert.Equal("v17", full.VndbId);
        var oldCli = JsonSerializer.SerializeToElement(new { results = new[] { new { id = "v17", title = "Only summary", developers = new[] { new { id = "p1", name = "Studio" } } } }, more = false });
        var listing = Assert.Single(MetadataInput.Parse(oldCli, new()).Items); Assert.Null(listing.Snapshot); Assert.Equal("listing", listing.InputKind);
    }

    [Fact]
    public async Task ManualAndClientSubmissionShareReceiptsAndPendingFilesRemainImmutable()
    {
        using var dir = new TestDirectory(); using var cache = new TestDirectory(); using var upstream = new StubHandler((_, _) => throw new HttpRequestException("offline"));
        await using var factory = new Factory(dir.Path, upstream); using var http = factory.CreateClient(); Auth(http);
        var snapshots = new JsonSnapshotStore(cache.Path, "http://localhost/"); var q = new ClientQuery("vn_get", new(), "v17");
        await snapshots.SaveAsync(q, QueryRules.Response([Snapshot()], "client_vndb", new(), null, false) with { PendingImport = true }, Ct);
        var pending = Assert.Single(Directory.GetFiles(Path.Combine(cache.Path, "pending"))); var bytes = await File.ReadAllBytesAsync(pending, Ct);
        using var input = JsonDocument.Parse(bytes);
        using var preview = await http.PostAsJsonAsync("/api/v1/imports/preview", new ImportPreviewRequest(input.RootElement, true), VndbJson.Options, Ct);
        var batch = (await preview.Content.ReadFromJsonAsync<ImportBatchReceipt>(VndbJson.Options, Ct))!;
        using var commit = await http.PostAsync("/api/v1/imports/" + batch.Id + "/commit", null, Ct); Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        var store = factory.Services.GetRequiredService<SqliteLibraryStore>(); Process(store);
        // Do not preinstall a default key on SDK client: it binds keys only to writes itself.
        using var sdkHttp = factory.CreateClient(); var sdk = new WebLibraryClient(sdkHttp, new("http://localhost/"), "m2-test-key", TimeSpan.FromSeconds(10));
        var sync = new PendingMetadataSync(sdk, "http://localhost/", cache.Path);
        var receipt = Assert.Single(await sync.SubmitAsync(Ct)); Assert.Equal(batch.Id, receipt.Receipt.Id); Assert.True(receipt.Receipt.Accepted);
        Assert.Single(await sync.SubmitAsync(Ct)); Assert.Equal(bytes, await File.ReadAllBytesAsync(pending, Ct));
        Assert.False((await snapshots.ReadAsync(q, Ct))!.PendingImport); Assert.Equal(1, Count(dir.Path, "import_events")); Assert.Empty(upstream.Requests);
        Assert.Single(store.PersonalItems());
        using var alias = new TestDirectory(); var aliasSnapshots = new JsonSnapshotStore(alias.Path, "http://localhost/");
        await aliasSnapshots.SaveAsync(q, QueryRules.Response([Snapshot()], "client_vndb", new(), null, false) with { PendingImport = true }, Ct);
        var aliasReceipt = Assert.Single(await new PendingMetadataSync(sdk, "http://localhost/", alias.Path).SubmitAsync(Ct));
        Assert.Equal(receipt.Receipt.Id, aliasReceipt.Receipt.Id); Assert.NotEqual(receipt.Receipt.RawSha256, aliasReceipt.Receipt.RawSha256);
        Assert.False((await aliasSnapshots.ReadAsync(q, Ct))!.PendingImport); Assert.Equal(1, Count(dir.Path, "import_events"));
        await File.AppendAllTextAsync(pending, " ", Ct);
        Assert.True((await snapshots.ReadAsync(q, Ct))!.PendingImport);
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.SubmitAsync(Ct));
        Assert.True((await snapshots.ReadAsync(q, Ct))!.PendingImport);
    }

    [Fact]
    public void InterruptedJobsHaveBoundedRetriesAndTitleMatchesRequireExplicitSelection()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path); store.Save(StorageTests.Entry());
        var payload = JsonSerializer.SerializeToElement(new { format = "vn-library-batch", format_version = 1, kind = "metadata", batch_id = "unknown", items = new[] { new { title = "未知标题" } } });
        var batch = Preview(store, payload, true); store.CommitMetadata(batch.Id, "commit"); Process(store);
        var now = DateTimeOffset.UtcNow.AddMinutes(1);
        for (var i = 0; i < 3; i++) { Assert.NotNull(store.BeginMetadataJob(now)); now = now.AddMinutes(1); store = new SqliteLibraryStore(dir.Path); }
        Assert.Null(store.BeginMetadataJob(now)); var exhausted = Assert.Single(store.MetadataJobs()); Assert.Equal("error", exhausted.State); Assert.Equal(3, exhausted.Attempts);
        store.ControlMetadataJob(exhausted.Id, "retry", "retry"); var job = store.BeginMetadataJob(now)!;
        var book = Assert.Single(store.PersonalItems()); var play = store.SavePersonalPlay(new(Play(), book.Id), null, "play");
        store.FinishMetadataJob(job, [Snapshot()], null, now); Assert.Null(store.GetItem(book.Id)!.Details.NovelId);
        var candidate = Assert.Single(store.MetadataCandidates()); Assert.Equal("title_matches", candidate.Kind);
        Assert.Throws<ArgumentException>(() => store.DecideCandidate(candidate.Id, new(candidate.ExpectedRevision, "v2"), true, "wrong-choice"));
        var decision = new CandidateDecision(candidate.ExpectedRevision, "v17");
        var accepted = store.DecideCandidate(candidate.Id, decision, true, "accept");
        Assert.Equivalent(accepted, store.DecideCandidate(candidate.Id, decision, true, "accept"));
        Assert.Equal("v17", store.GetItem(book.Id)!.Details.NovelId); Assert.Equal(play.Details with { NovelId = "v17" }, store.GetPlay(play.Id)!.Details);
    }

    [Fact]
    public async Task ConcurrentCreationRetriesHaveOneEffectAndParentRemovalDoesNotInvalidateReceipt()
    {
        using var dir = new TestDirectory(); var store = new SqliteLibraryStore(dir.Path);
        var write = new ItemWrite(new("Unknown"));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => store.SaveItem(write, null, "concurrent"), Ct)));
        Assert.All(results, item => Assert.Equal(results[0], item)); Assert.Single(store.PersonalItems());
        var book = results[0]; var playWrite = new PersonalPlayWrite(Play(), book.Id);
        var play = store.SavePersonalPlay(playWrite, null, "create-play"); store.SetItemRemoved(book.Id, new(book.Revision), true, "remove-book");
        Assert.Equivalent(play, store.SavePersonalPlay(playWrite, null, "create-play"));
        Assert.Throws<PlayConflictException>(() => store.SavePersonalPlay(playWrite, null, "another-play"));
        Assert.Single(store.ItemPlays(book.Id)); Assert.Equal(3, Count(dir.Path, "operation_receipts"));
    }

    [Fact]
    public async Task OfflineCoverFailurePreservesImportedTextAndDurableRetry()
    {
        using var dir = new TestDirectory(); using var offline = new StubHandler((_, _) => throw new HttpRequestException("offline"));
        var store = new SqliteLibraryStore(dir.Path); using var core = new VndbClient(new HttpClient(offline, false), VndbClientTests.FastOptions);
        var covers = new CoverDownloader(new HttpClient(offline, false), dir.Path);
        var importer = new LibraryImporter(core, covers, store); using var imports = new MetadataImports(store, core, importer, covers, TimeProvider.System, new());
        var batch = imports.Preview(new(Payload(Snapshot(image: true)))); store.CommitMetadata(batch.Id, "commit");
        Assert.True(await imports.StepAsync(Ct)); Assert.Equal("完整离线简介", store.Get("v17")!.Novel.Description);
        Assert.True(await imports.StepAsync(Ct)); var job = Assert.Single(store.MetadataJobs()); Assert.Equal("queued", job.State); Assert.Equal(1, job.Attempts);
        Assert.Equal("完整离线简介", store.Get("v17")!.Novel.Description); Assert.Equal("client_vndb_import", store.Provenance("v17")!.Source);
        Assert.Null(store.Provenance("v17")!.VerifiedAt); Assert.Single(offline.Requests);
    }

    [Fact]
    public async Task ApiRequiresKeyAndOperationKeysAndDeliversManagementUiAndSchema()
    {
        using var dir = new TestDirectory(); using var upstream = new StubHandler("{}");
        await using var factory = new Factory(dir.Path, upstream); using var http = factory.CreateClient();
        using var denied = await http.PostAsJsonAsync("/api/v1/items", new ItemWrite(new("Unknown")), VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Auth(http); using var created = await http.PostAsJsonAsync("/api/v1/items", new ItemWrite(new("Unknown")), VndbJson.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode); var item = (await created.Content.ReadFromJsonAsync<LibraryItem>(VndbJson.Options, Ct))!;
        using var malformed = await http.PostAsJsonAsync("/api/v1/imports/preview", new { payload = new { format = "vn-library-batch", format_version = "1", kind = "metadata", items = new[] { new { title = "Invalid version type" } } } }, VndbJson.Options, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        http.DefaultRequestHeaders.Remove("Idempotency-Key");
        using var noOperation = await http.PostAsJsonAsync("/api/v1/items", new ItemWrite(new("Other")), VndbJson.Options, Ct); Assert.Equal(HttpStatusCode.BadRequest, noOperation.StatusCode);
        Assert.NotNull(item.Id); Assert.Null(item.Details.NovelId); Assert.Single(factory.Services.GetRequiredService<SqliteLibraryStore>().PersonalItems());
        var page = await http.GetStringAsync("/manage.html", Ct); Assert.Contains("JSON 导入", page); Assert.Contains("已移除", page);
        using var schema = JsonDocument.Parse(await http.GetStringAsync("/api/v1/schemas/metadata-batch-v1.json", Ct)); Assert.Equal("urn:vndb-local:metadata-batch:v1", schema.RootElement.GetProperty("$id").GetString());
        var info = await http.GetFromJsonAsync<ServiceInfo>("/api/v1/info", VndbJson.Options, Ct); Assert.True(info!.MetadataImportAvailable); Assert.Equal(500, info.ImportLimits!.MaximumItems); Assert.Equal(14, info.SchemaVersion);
    }
}
