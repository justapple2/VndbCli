using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Xunit;

namespace Vndb.Tests;

public sealed class DatasetMaintenanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static HttpClient NoNetwork() => new(new StubHandler((_, _) => throw new InvalidOperationException("No upstream expected")));
    private static async Task<DatasetJob> Upload(DatasetImports imports, byte[] bytes, string key)
    { using var input = new MemoryStream(bytes); return await imports.UploadAsync(input, key, Ct); }

    [Fact]
    public async Task RepeatedConcurrentAndLostReplyUploadsConvergeOnOneAcceptedInput()
    {
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(dir.Path);
        using var first = new DatasetImports(store, http); using var second = new DatasetImports(new(dir.Path), http);
        var bytes = await File.ReadAllBytesAsync(DumpPackageTests.Archive(dir.Path), Ct);
        var jobs = await Task.WhenAll(Upload(first, bytes, "same-key"), Upload(second, bytes, "same-key"));
        Assert.Equal(jobs[0], jobs[1]);
        Assert.Equal(jobs[0], await Upload(second, bytes, "same-key")); // retry after a lost response
        using (var lowSpace = new DatasetImports(store, http, availableSpace: _ => 0))
        {
            Assert.Equal(jobs[0], await Upload(lowSpace, bytes, "same-key"));
            await Assert.ThrowsAsync<PlayConflictException>(() => Upload(lowSpace, [1], "same-key"));
        }
        Assert.Equal(jobs[0].Id, (await Upload(first, bytes, "other-key")).Id);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
        await Assert.ThrowsAsync<PlayConflictException>(() => Upload(first, [1, 2, 3], "same-key"));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
        var status = first.MaintenanceStatus(); Assert.Equal(bytes.Length, status.InputBytes); Assert.Equal(jobs[0].Id, status.ActiveJob!.Id);
        Assert.Empty((await first.PreviewCleanupAsync("active-preview", Ct)).Entries);
    }

    private sealed class InterruptedStream(bool cancel, CancellationTokenSource? stop = null) : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_read) throw new IOException("Synthetic disconnected client");
            _read = true; buffer.Span[0] = 42; if (cancel) stop!.Cancel(); return ValueTask.FromResult(1);
        }
    }
    [Fact]
    public async Task EmptyDisconnectedCancelledAndLowSpaceAttemptsLeaveNoInputs()
    {
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(dir.Path);
        using var imports = new DatasetImports(store, http);
        await Assert.ThrowsAsync<ArgumentException>(() => Upload(imports, [], "empty"));
        using (var disconnected = new InterruptedStream(false)) await Assert.ThrowsAsync<IOException>(() => imports.UploadAsync(disconnected, "disconnect", Ct));
        using (var stop = new CancellationTokenSource())
        using (var cancelled = new InterruptedStream(true, stop)) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => imports.UploadAsync(cancelled, "cancel", stop.Token));
        using var lowSpace = new DatasetImports(store, http, availableSpace: _ => 0);
        await Assert.ThrowsAsync<IOException>(() => Upload(lowSpace, [1], "space"));
        Assert.Empty(store.DatasetJobs()); Assert.Empty(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
    }
    [Fact]
    public async Task FailedPreparationAndDownloadRemoveOnlyUnacceptedAttemptFiles()
    {
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(dir.Path);
        using var imports = new DatasetImports(store, http);
        var job = await Upload(imports, [1, 2, 3], "invalid"); await imports.StepAsync(Ct);
        var failed = Assert.Single(store.DatasetJobs()); Assert.Equal("failed", failed.State);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir.Path, "datasets"), "stage-*"));
        var preview = await imports.PreviewCleanupAsync("failed-preview", Ct); Assert.Empty(preview.Entries);
        await Assert.ThrowsAsync<PlayConflictException>(() => imports.CleanupAsync(new(preview.Id, preview.Sha256), "failed-cleanup", Ct));
        imports.Control(job.Id, "cancel", new(failed.Revision), "cancel-failed");
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(false)) }));
        using var downloadHttp = new HttpClient(handler); using var downloader = new DatasetImports(store, downloadHttp);
        downloader.Create(new("2026-10-27"), "download"); await downloader.StepAsync(Ct);
        Assert.Equal("failed", store.DatasetJobs()[0].State);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
    }
    [Fact]
    public async Task CompletedStageCleanupRetainsPackageDatabaseAndIdempotentReceiptAcrossRestart()
    {
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(dir.Path);
        using var imports = new DatasetImports(store, http);
        var package = await File.ReadAllBytesAsync(DumpPackageTests.Archive(dir.Path), Ct);
        await Upload(imports, package, "valid");
        for (var i = 0; i < 12 && store.DatasetJobs()[0].State != "completed"; i++) await imports.StepAsync(Ct);
        Assert.Equal("completed", store.DatasetJobs()[0].State);
        var before = JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options);
        var preview = await imports.PreviewCleanupAsync("completed-preview", Ct);
        Assert.Equal("completed_stage", Assert.Single(preview.Entries).Kind);
        Assert.Equal(preview.Sha256, (await imports.PreviewCleanupAsync("completed-preview", Ct)).Sha256);
        using var restarted = new DatasetImports(new(dir.Path), http);
        var result = await restarted.CleanupAsync(new(preview.Id, preview.Sha256), "completed-cleanup", Ct);
        Assert.Equal(result, await restarted.CleanupAsync(new(preview.Id, preview.Sha256), "completed-cleanup", Ct));
        Assert.Equal(1, result.Removed); Assert.True(result.ReleasedBytes > 0);
        Assert.Equal(before, JsonSerializer.Serialize(store.Get("v17"), VndbJson.Options));
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir.Path, "datasets"), "stage-*"));
        Assert.Equal(0, restarted.MaintenanceStatus().StageBytes);
        Assert.Empty((await restarted.PreviewCleanupAsync("after-cleanup", Ct)).Entries);
        Assert.Equal("completed", (await Upload(restarted, package, "repeat-completed")).State);
        Assert.Single(Directory.GetFiles(Path.Combine(dir.Path, "datasets"), "*.zst"));
    }
    [Fact]
    public async Task OrphanPreviewRejectsChangesAndActiveJobAndNeverIncludesUnrelatedFiles()
    {
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(dir.Path);
        using var imports = new DatasetImports(store, http); var root = Directory.CreateDirectory(Path.Combine(dir.Path, "datasets")).FullName;
        var orphan = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zst"); File.WriteAllBytes(orphan, [1, 2, 3]);
        File.WriteAllText(Path.Combine(root, "important-notes.txt"), "keep");
        var preview = await imports.PreviewCleanupAsync("orphan", Ct); Assert.Single(preview.Entries);
        File.WriteAllBytes(orphan, [4, 5, 6]);
        await Assert.ThrowsAsync<PlayConflictException>(() => imports.CleanupAsync(new(preview.Id, preview.Sha256), "changed", Ct));
        Assert.True(File.Exists(orphan));
        var fresh = await imports.PreviewCleanupAsync("fresh", Ct);
        var job = imports.Create(new("2026-10-27"), "job");
        await Assert.ThrowsAsync<PlayConflictException>(() => imports.CleanupAsync(new(fresh.Id, fresh.Sha256), "blocked", Ct));
        imports.Control(job.Id, "cancel", new(job.Revision), "cancel");
        Assert.Equal(1, (await imports.CleanupAsync(new(fresh.Id, fresh.Sha256), "blocked", Ct)).Removed);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "important-notes.txt")));
        File.WriteAllBytes(orphan, [7, 8, 9]); // a later file must not invalidate an already committed operation receipt
        Assert.Equal(1, (await imports.CleanupAsync(new(fresh.Id, fresh.Sha256), "blocked", Ct)).Removed);
        Assert.True(File.Exists(orphan));
    }
    [Fact]
    public async Task InterruptedCleanupCanResumeItsOriginalPreviewWithoutFollowingNewFiles()
    {
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(dir.Path);
        using var imports = new DatasetImports(store, http); var root = Directory.CreateDirectory(Path.Combine(dir.Path, "datasets")).FullName;
        var stage = Directory.CreateDirectory(Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(stage, "README.txt"), "partial preparation"); File.WriteAllText(Path.Combine(stage, "TIMESTAMP"), "synthetic");
        var preview = await imports.PreviewCleanupAsync("interrupt-preview", Ct);
        File.WriteAllText(Path.Combine(dir.Path, "dataset-cleanups", preview.Id + ".json.intent"), preview.Sha256);
        File.Delete(Path.Combine(stage, "README.txt")); // crash after the first unlink, before the receipt transaction commits
        using var restarted = new DatasetImports(new(dir.Path), http);
        Assert.Equal(1, (await restarted.CleanupAsync(new(preview.Id, preview.Sha256), "resume-cleanup", Ct)).Removed);
        Assert.False(Directory.Exists(stage));
    }
    private sealed class Factory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder b) => b.UseSetting("Library:DataDirectory", path)
            .UseSetting("Library:WriteKey", "test-only-library-key").UseSetting("CatalogJobs:Enabled", "false");
    }
    [Fact]
    public async Task ReparseStageCannotAuthorizeDeletingAnOutsideDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = new TestDirectory(); using var http = NoNetwork(); var store = new SqliteLibraryStore(Path.Combine(dir.Path, "data"));
        using var imports = new DatasetImports(store, http);
        var root = Directory.CreateDirectory(Path.Combine(store.DataDirectory, "datasets")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(dir.Path, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "README.txt"), "preserve");
        var link = Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"));
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync(Ct); Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => imports.PreviewCleanupAsync("link-preview", Ct));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(outside, "README.txt")));
        }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task MaintenanceApiRequiresWriteKeyAndExposesNoServerPaths()
    {
        using var dir = new TestDirectory(); await using var factory = new Factory(dir.Path); using var http = factory.CreateClient();
        using var denied = await http.PostAsync("/api/v1/datasets/maintenance/preview", null, Ct); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var api = new WebLibraryClient(http, http.BaseAddress!, "test-only-library-key", TimeSpan.FromSeconds(10));
        Assert.Contains("dataset_maintenance", (await api.OrganizationInfoAsync(Ct)).Capabilities);
        var status = await api.DatasetMaintenanceAsync(Ct); Assert.True(status.AvailableBytes >= 0);
        var preview = await api.PreviewDatasetCleanupAsync("api-preview", Ct);
        var wire = JsonSerializer.Serialize(new { status, preview }, VndbJson.Options);
        Assert.DoesNotContain(dir.Path, wire); Assert.DoesNotContain("source_file", wire);
        Assert.Equal(0, (await api.CleanupDatasetAsync(new(preview.Id, preview.Sha256), "api-cleanup", Ct)).Removed);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/datasets/upload");
        request.Headers.Add("X-Library-Request", "1"); request.Headers.Add("X-Library-Key", "test-only-library-key"); request.Headers.Add("Idempotency-Key", "oversized");
        request.Content = new ByteArrayContent([1]); request.Content.Headers.ContentType = new("application/octet-stream"); request.Content.Headers.ContentLength = DumpPackage.MaximumCompressedBytes + 1;
        using var response = await http.SendAsync(request, Ct); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
