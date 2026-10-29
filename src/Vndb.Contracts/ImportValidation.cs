using System.Text;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Contracts;

public static class ImportValidation
{
    public static void Metadata(JsonElement payload, ImportLimits limits)
    {
        limits.RequireBytes(Encoding.UTF8.GetByteCount(payload.GetRawText()));
        if (payload.ValueKind != JsonValueKind.Object) return; // Full protocol validation belongs to Library.
        if (payload.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == "vn-library-activity")
            throw new ImportValidationException("import_kind_mismatch", 400, "这是活动JSON，请使用Web“会话”页的活动导入入口；metadata请使用“JSON导入”。");
        if (payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) limits.RequireItems(items.GetArrayLength());
        else if (payload.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array) limits.RequireItems(results.GetArrayLength());
    }
    public static void Activity(ActivityBatch batch, ImportLimits limits)
    {
        if (batch.Items is null || batch.Installations is null || batch.Plays is null || batch.PlaySessions is null || batch.PlayEdits is null || batch.Metadata is null)
            throw new ArgumentException("活动批次数组不能为空。");
        limits.RequireItems(batch.Items.Count + batch.Installations.Count + batch.Plays.Count + batch.PlaySessions.Count + batch.PlayEdits.Count + batch.Metadata.Count);
        // Count serialized bytes without building a second arbitrarily large in-memory payload.
        using var counter = new CountingStream(limits);
        using var writer = new Utf8JsonWriter(counter);
        JsonSerializer.Serialize(writer, batch, VndbJson.Options);
    }
    private sealed class CountingStream(ImportLimits limits) : Stream
    {
        private long _bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { _bytes += buffer.Length; limits.RequireBytes(_bytes); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
