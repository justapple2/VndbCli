using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public static class MetadataExport
{
    public static Task WriteAsync(MetadataResponse result, string file, CancellationToken ct)
    {
        var batch = new MetadataBatch(result.Items, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("D"));
        if (WirePrivacy.ContainsPrivateData(JsonSerializer.SerializeToElement(batch, VndbJson.Options)))
            throw new InvalidDataException("公开资料导出不能包含本机目录或程序信息。");
        return JsonSnapshotStore.AtomicWriteAsync(file, batch, false, ct);
    }
}
