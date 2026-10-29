using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Library;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class MetadataReceiptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Origin = "http://library.test/";
    private static ImportBatchReceipt Valid(JsonElement payload)
    {
        var parsed = MetadataInput.Parse(payload, new());
        var hash = PlayRules.Hash(JsonSerializer.SerializeToUtf8Bytes(parsed.Items, VndbJson.Options));
        return new("import-" + hash, hash, parsed.ClientId, "queued", true, parsed.Items.Count, 0,
            parsed.Items.Select((i, n) => new ImportItemResult(n, i.OperationId, i.VndbId, i.Title, "preview")).ToArray(), DateTimeOffset.UnixEpoch, "synthetic-instance", parsed.RawSha);
    }
    private static ImportBatchReceipt Alter(ImportBatchReceipt r, string kind) => kind switch
    {
        "raw" => r with { RawSha256 = new string('a', 64) }, "legacy" => r with { RawSha256 = null },
        "empty" => r with { Total = 0, Items = [] }, "count" => r with { Total = 2 },
        "index" => r with { Items = [r.Items[0] with { Index = 1 }] },
        "identity" => r with { Items = [r.Items[0] with { VndbId = "v999" }] },
        "status" => r with { Items = [r.Items[0] with { Status = "unknown" }] },
        "error" => r with { Items = [r.Items[0] with { Status = "error" }] },
        "operation" => r with { Items = [r.Items[0] with { OperationId = new string('c', 64) }] },
        "state" => r with { State = "complete", Completed = 0 },
        "accepted" => r with { Accepted = false }, "payload" => r with { PayloadSha256 = new string('b', 64) },
        _ => throw new ArgumentException(kind)
    };
    private sealed class Routes : HttpMessageHandler
    {
        public Func<ImportBatchReceipt, ImportBatchReceipt> Change { get; set; } = r => r;
        public List<string> Keys { get; } = [];
        public bool LoseReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object result;
            if (request.Method == HttpMethod.Get) result = new ServiceInfo("synthetic-instance", [1], "test", 10, true, ["metadata_import"], [1], true);
            else
            {
                Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); result = Change(Valid(payload.RootElement));
                if (LoseReply) { LoseReply = false; throw new HttpRequestException("Synthetic lost reply"); }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: VndbJson.Options) };
        }
    }
    [Theory]
    [InlineData("raw")][InlineData("legacy")][InlineData("empty")][InlineData("count")][InlineData("index")][InlineData("identity")]
    [InlineData("status")][InlineData("error")][InlineData("operation")][InlineData("state")][InlineData("accepted")][InlineData("payload")]
    public async Task InvalidOrLegacyProofNeverAcknowledgesNewOrStoredReceipt(string kind)
    {
        using var root = new TestDirectory(); var scope = LocalQueryScope.Resolve(root.Path, new(Origin), environment: _ => null);
        var snapshots = new JsonSnapshotStore(scope.DirectoryPath, Origin); var q = ClientQuery.FromText("v17");
        await snapshots.SaveAsync(q, new([M5Tests.Item().Metadata!], "client_vndb", DateTimeOffset.UnixEpoch, 1, 10, 1, false, PendingImport: true), Ct);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "pending"))); var original = await File.ReadAllBytesAsync(path, Ct);
        using var routes = new Routes { Change = r => Alter(r, kind) }; using var http = new HttpClient(routes); var api = new WebLibraryClient(http, new(Origin), null, TimeSpan.FromSeconds(10));
        var sync = new PendingMetadataSync(api, Origin, scope.DirectoryPath);
        Assert.Equal("sync_receipt_invalid", (await Assert.ThrowsAsync<ClientApiException>(() => sync.SubmitAsync(Ct))).Code);
        Assert.False(Directory.Exists(Path.Combine(scope.DirectoryPath, "receipts")));
        Assert.Single(Directory.GetFiles(Path.Combine(scope.DirectoryPath, "metadata-operations", "rejected"), "*.json", SearchOption.AllDirectories));
        Assert.True((await snapshots.ReadAsync(q, Ct))!.PendingImport);
        // Old on-disk receipts follow the same proof rules; they remain intact for review.
        using var json = JsonDocument.Parse(original); var bad = Alter(Valid(JsonSerializer.SerializeToElement(json.RootElement, VndbJson.Options)), kind);
        var receiptPath = Path.Combine(scope.DirectoryPath, "receipts", bad.ClientBatchId + ".json");
        await JsonSnapshotStore.AtomicWriteAsync(receiptPath, new ClientBatchReceipt(bad.ClientBatchId!, bad.PayloadSha256, Origin, bad, DateTimeOffset.UnixEpoch, PlayRules.Hash(original)), false, Ct);
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath, Ct);
        Assert.True((await snapshots.ReadAsync(q, Ct))!.PendingImport);
        Assert.Equal("unconfirmed", Assert.Single((await ImportStatus.ReadAsync(api, scope, Path.Combine(root.Path, "no-activity"), Ct)).Results).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.SubmitAsync(Ct)); Assert.Single(routes.Keys);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct)); Assert.Equal(receiptBytes, await File.ReadAllBytesAsync(receiptPath, Ct));
    }
    [Fact]
    public async Task LostOrInvalidReplyRetriesExactOriginalOperationAndOnlyValidProofAcknowledges()
    {
        using var root = new TestDirectory(); var snapshots = new JsonSnapshotStore(root.Path, Origin); var q = ClientQuery.FromText("v17");
        await snapshots.SaveAsync(q, new([M5Tests.Item().Metadata!], "client_vndb", DateTimeOffset.UnixEpoch, 1, 10, 1, false, PendingImport: true), Ct);
        using var routes = new Routes { LoseReply = true }; using var http = new HttpClient(routes); var api = new WebLibraryClient(http, new(Origin), null, TimeSpan.FromSeconds(10));
        var sync = new PendingMetadataSync(api, Origin, root.Path);
        await Assert.ThrowsAnyAsync<Exception>(() => sync.SubmitAsync(Ct)); Assert.True((await snapshots.ReadAsync(q, Ct))!.PendingImport);
        routes.Change = r => Alter(r, "raw"); await Assert.ThrowsAsync<ClientApiException>(() => sync.SubmitAsync(Ct));
        routes.Change = r => r; var accepted = Assert.Single(await sync.SubmitAsync(Ct)); Assert.Equal("queued", accepted.Receipt.State);
        Assert.False((await snapshots.ReadAsync(q, Ct))!.PendingImport); Assert.Single(await sync.SubmitAsync(Ct));
        Assert.Single(routes.Keys.Distinct()); Assert.All(routes.Keys, k => Assert.StartsWith("sync-", k));
    }
}
