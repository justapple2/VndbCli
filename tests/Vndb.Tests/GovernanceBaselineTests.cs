using System.Text.Json;
using Vndb.Client;
using Vndb.Contracts;
using Vndb.Core;
using Vndb.Local;
using Xunit;

namespace Vndb.Tests;

public sealed class GovernanceBaselineTests
{
    [Fact]
    public void QueryAndLegacyCacheEnvelopeKeepTheirWireShape()
    {
        const string queryJson = """{"kind":"vn_get","request":{"query":"","producer_id":null,"page":1,"page_size":10,"mode":"local_first"},"id":"v17"}""";
        var compact = new JsonSerializerOptions(VndbJson.Options) { WriteIndented = false };
        var query = JsonSerializer.Deserialize<ClientQuery>(queryJson, VndbJson.Options)!;
        Assert.Equal("v17", query.Validate().Id);
        Assert.Equal(queryJson, JsonSerializer.Serialize(query, compact));
        var response = QueryRules.Response([], "library", query.Request, 0, false);
        Assert.Equal("""{"items":[],"source":"library","fetched_at":null,"page":1,"page_size":10,"total":0,"more":false,"partial":false,"needs_remote":false,"pending_import":false,"verification_status":"vndb_metadata","warnings":null}""",
            JsonSerializer.Serialize(response, compact));
    }

    [Fact]
    public void PresenceOperationHasFixedPortableAndLegacyWindowsHashes()
    {
        var batch = new PresenceBatch("test-operation", "test-service", "test-device", 7, [new("test-item", true)]);
        const string portable = "ed5e21cf26235e2e2314947684db1e0b327275f282b94784347f561de5131534";
        const string windows = "e49b55d2acf45602fd8d55ea566b22012a282e4a3f7da0df5a2c3b626229845f";
        Assert.Equal(portable, ArchiveFormat.Hash(batch));
        Assert.Equal(windows, ArchiveFormat.LegacyWindowsHash(batch));
        Assert.True(ArchiveFormat.MatchesHash(batch, portable));
        Assert.True(ArchiveFormat.MatchesHash(batch, windows));
        Assert.False(ArchiveFormat.MatchesHash(batch with { ExpectedRevision = 8 }, portable));
    }

    [Fact]
    public void HistoricalShelfSnapshotRemainsReadableWithoutChangingItsBytes()
    {
        const string json = """{"origin":"http://library.test/","items":[],"plays":[],"installations":[],"fetched_at":"2026-01-01T00:00:00+00:00"}""";
        var compact = new JsonSerializerOptions(VndbJson.Options) { WriteIndented = false };
        var snapshot = JsonSerializer.Deserialize<ShelfSnapshot>(json, VndbJson.Options)!;
        Assert.Equal("http://library.test/", snapshot.Origin);
        Assert.Empty(snapshot.Installations);
        Assert.Equal(json, JsonSerializer.Serialize(snapshot, compact));
    }
}
