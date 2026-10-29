using System.Text.Json;
using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Library;

public sealed class ActivityImports(SqliteLibraryStore store, MetadataImports metadata, ImportLimits? limits = null)
{
    private readonly object _gate = new();
    public ActivityReceipt Submit(ActivityBatch batch, string instanceId)
    {
        Validate(batch);
        lock (_gate)
        {
            var hash = ArchiveFormat.Hash(batch); store.RegisterActivityBatch(batch, hash);
            if (store.ActivityReceipt(batch.BatchId) is { Complete: true } accepted) return accepted;
            if (batch.Metadata.Count > 0)
            {
                var payload = JsonSerializer.SerializeToElement(new MetadataBatch(batch.Metadata, DateTimeOffset.UnixEpoch, "activity-" + batch.BatchId), VndbJson.Options);
                _ = metadata.Sync(payload, "metadata-" + batch.BatchId);
                for (var i = 0; i < batch.Metadata.Count && store.ProcessMetadataItem(); i++) { }
            }
            var results = new List<ActivityResult>();
            if (!store.Devices().Any(d => d.Id == batch.DeviceId)) store.SaveDevice(new(batch.DeviceId, batch.Device), "device-" + batch.DeviceId);
            string Resolve(string kind, string reference) => !reference.StartsWith("client:", StringComparison.Ordinal) ? reference :
                store.ActivityMapping(batch.DeviceId, kind, reference[7..]) ?? throw new DependencyException();
            void Seed<T>(string kind, string clientId, T payload, Func<string> create)
            {
                try
                {
                    var sha = ArchiveFormat.Hash(payload); var legacy = ArchiveFormat.LegacyWindowsHash(payload); var previous = store.ActivityMapping(batch.DeviceId, kind, clientId, sha, legacy);
                    var id = previous ?? create(); store.SaveActivityMapping(batch.DeviceId, kind, clientId, sha, id, legacy);
                    results.Add(new(clientId, kind, previous is null ? "applied" : "duplicate", id));
                }
                catch (Exception e) when (e is ArgumentException or PlayConflictException or DependencyException)
                { results.Add(new(clientId, kind, e is DependencyException ? "dependency_pending" : "conflict", Message: e is DependencyException ? "依赖尚未收录。" : e.Message)); }
            }
            foreach (var seed in batch.Items) Seed("items", seed.ClientId, seed, () =>
            {
                if (seed.Details.NovelId is { } vn && store.Get(vn) is null) throw new DependencyException();
                return store.SaveItem(new(seed.Details), null, SqliteLibraryStore.ActivityKey(batch.DeviceId, "item", seed.ClientId)).Id;
            });
            foreach (var seed in batch.Installations) Seed("installations", seed.ClientId, seed, () =>
            {
                if (seed.Details.DeviceId != batch.DeviceId) throw new ArgumentException("安装种子设备不符。");
                var components = seed.Components.Select(c => c with { ItemId = Resolve("items", c.ItemId) }).ToArray();
                var installation = store.SaveInstallation(new(seed.Details), null, SqliteLibraryStore.ActivityKey(batch.DeviceId, "installation", seed.ClientId));
                for (var i = 0; i < components.Length; i++)
                {
                    var component = store.SaveComponent(installation.Id, new(components[i]), null, SqliteLibraryStore.ActivityKey(batch.DeviceId, "component", seed.ClientId + "." + i));
                    store.SaveActivityMapping(batch.DeviceId, "components", seed.ClientId + "." + i, ArchiveFormat.Hash(seed.Components[i]), component.Id, ArchiveFormat.LegacyWindowsHash(seed.Components[i]));
                }
                return installation.Id;
            });
            foreach (var seed in batch.Plays) Seed("plays", seed.ClientId, seed, () => store.SavePersonalPlay(new(seed.Details, Resolve("items", seed.ItemRef)), null,
                SqliteLibraryStore.ActivityKey(batch.DeviceId, "play", seed.ClientId)).Id);
            foreach (var sample in batch.PlaySessions.OrderBy(s => s.StartedAt).ThenBy(s => s.Sequence))
            {
                try
                {
                    var saved = store.UpsertSession(sample with { PlayId = Resolve("plays", sample.PlayId), InstallationId = sample.InstallationId is { } i ? Resolve("installations", i) : null,
                        ComponentId = sample.ComponentId is { } c ? Resolve("components", c) : null });
                    results.Add(new(sample.Id + ":" + sample.Sequence, "play_sessions", "applied", saved.Sample.Id, saved.ReviewState == "pending" ? "会话秒数待人工核对，不自动合计。" : null));
                }
                catch (Exception e) when (e is DependencyException or ArgumentException or PlayConflictException)
                { results.Add(new(sample.Id + ":" + sample.Sequence, "play_sessions", e is DependencyException ? "dependency_pending" : "conflict", Message: e.Message)); }
            }
            foreach (var edit in batch.PlayEdits)
            {
                try { results.Add(store.ApplyActivityEdit(batch.DeviceId, edit, Resolve("plays", edit.PlayRef))); }
                catch (Exception e) when (e is DependencyException or ArgumentException or PlayConflictException)
                { results.Add(new(edit.Id, "play_edits", e is DependencyException ? "dependency_pending" : "conflict", Message: e.Message)); }
            }
            var receipt = new ActivityReceipt(batch.BatchId, hash, instanceId, results, results.All(r => r.Status is "applied" or "duplicate"), DateTimeOffset.UtcNow);
            store.SaveActivityReceipt(receipt); return receipt;
        }
    }
    public void Step(string instanceId) { if (store.PendingActivityBatch() is { } batch) _ = Submit(batch, instanceId); }
    private sealed class DependencyException : Exception;
    private void Validate(ActivityBatch batch)
    {
        ImportValidation.Activity(batch, limits ?? new());
        if (batch.Format != "vn-library-activity" || batch.FormatVersion != 1 || batch.Kind is not ("play_activity" or "play_sessions" or "play_edits") ||
            !Guid.TryParseExact(batch.BatchId, "D", out _) || !Guid.TryParseExact(batch.DeviceId, "D", out _) || batch.Device is null ||
            batch.Items is null || batch.Installations is null || batch.Plays is null || batch.PlaySessions is null || batch.PlayEdits is null || batch.Metadata is null ||
            batch.Items.Count + batch.Installations.Count + batch.Plays.Count + batch.PlaySessions.Count + batch.PlayEdits.Count + batch.Metadata.Count < 1)
            throw new ArgumentException("活动批次格式／ID无效或没有条目。");
        if (batch.Items.Any(i => i is null || i.Details is null) || batch.Installations.Any(i => i is null || i.Details is null || i.Components is null || i.Components.Any(c => c is null)) ||
            batch.Plays.Any(p => p is null || p.Details is null || string.IsNullOrEmpty(p.ItemRef)) || batch.PlayEdits.Any(e => e is null || e.Details is null || string.IsNullOrEmpty(e.PlayRef)) ||
            batch.PlaySessions.Any(s => s is null || string.IsNullOrEmpty(s.PlayId)) || batch.Metadata.Any(m => m is null)) throw new ArgumentException("活动条目或依赖不能为空。");
        var keys = batch.Items.Select(i => ("items", i.ClientId)).Concat(batch.Installations.Select(i => ("installations", i.ClientId))).Concat(batch.Plays.Select(p => ("plays", p.ClientId)))
            .Concat(batch.PlayEdits.Select(e => ("play_edits", e.Id))).Concat(batch.PlaySessions.Select(s => ("play_sessions", s.Id + ":" + s.Sequence))).ToArray();
        if (keys.Distinct().Count() != keys.Length) throw new ArgumentException("批次包含重复操作键。");
        foreach (var id in batch.Items.Select(i => i.ClientId).Concat(batch.Installations.Select(i => i.ClientId)).Concat(batch.Plays.Select(p => p.ClientId)).Concat(batch.PlayEdits.Select(p => p.Id)))
            if (string.IsNullOrEmpty(id) || id.Length > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_'))) throw new ArgumentException("无效客户端创建键。");
        if (batch.PlaySessions.Any(s => s.DeviceId != batch.DeviceId)) throw new ArgumentException("会话设备与批次不符。");
    }
}
