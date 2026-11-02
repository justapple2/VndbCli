using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

/// <summary>One bounded metadata pass. No watcher, network, game-content reads or file mutations.</summary>
public sealed class LocalObservation(LocalLibraryStore library, string deviceId, IObservationFiles? files = null, TimeProvider? time = null)
{
    private readonly IObservationFiles _files = files ?? new ObservationFiles();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public static readonly TimeSpan NegativeDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CandidateDelay = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> Content = new(StringComparer.OrdinalIgnoreCase) { ".dat", ".xp3", ".arc", ".pak", ".bin", ".pfs", ".int", ".npa", ".rpa", ".wolf", ".rgss3a", ".g00", ".pck" };
    private JsonStateFile<ObservationSnapshot> State() => new(library.DirectoryPath, "local-observations.json", 32L * 1024 * 1024);
    public async Task<ObservationSnapshot> ReadAsync(CancellationToken ct = default)
    {
        var value = await State().ReadAsync(ct) ?? new(deviceId, [], [], []);
        Validate(value); return value;
    }
    private void Validate(ObservationSnapshot value)
    {
        if (value.Format != "vn-local-observations" || value.FormatVersion != 1 || value.DeviceId != deviceId || value.Installations is null || value.Packages is null || value.Candidates is null ||
            value.Installations.Count > 10000 || value.Packages.Count > 5000 || value.Candidates.Count > 5000 || value.Installations.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || r.Anchors is null || r.Anchors.Count > 32 || (r.ReadyProfiles?.Count ?? 0) > 16) || value.Packages.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id)) || value.Candidates.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id)) ||
            value.Installations.Select(i => i.Id).Distinct().Count() != value.Installations.Count || value.Packages.Select(i => i.Id).Distinct().Count() != value.Packages.Count || value.Candidates.Select(i => i.Id).Distinct().Count() != value.Candidates.Count)
            throw new InvalidDataException("本机观测JSON无效或绑定其他设备，原件保留。");
    }
    public async Task IgnoreAsync(string id, CancellationToken ct = default)
    {
        using var operation = await LocalFileLease.AcquireAsync(library.DirectoryPath, ObservationResourceIntents.Resource, ct);
        var state = State(); var snapshot = await state.ReadAsync(ct) ?? throw new InvalidOperationException("没有候选快照。");
        if (snapshot.DeviceId != deviceId || snapshot.FormatVersion != 1 || snapshot.Format != "vn-local-observations") throw new InvalidDataException("候选快照身份无效。");
        if (!snapshot.Candidates.Any(c => c.Id == id)) throw new ArgumentException("候选已变化，请刷新。");
        await state.SaveAsync(snapshot with { Candidates = snapshot.Candidates.Select(c => c.Id == id ? c with { State = "ignored" } : c).ToArray() }, ct);
    }
    public async Task<ObservationResult> CheckAsync(ObservationSettings settings, IReadOnlyList<LaunchProfile> profiles,
        IReadOnlyList<string>? dirtyPaths = null, bool discover = true, CancellationToken ct = default)
    {
        new ObservationSettingsStore(library.DirectoryPath).Validate(settings);
        var now = _time.GetUtcNow(); var state = State();
        using var operation = LocalFileLease.TryAcquire(library.DirectoryPath, ObservationResourceIntents.Resource);
        var prior = await state.ReadAsync(ct) ?? new(deviceId, [], [], []); Validate(prior);
        if (operation is null) return new(prior, new(0, 0, 0, 0, 0, 0, 0, 0), [], true, true, now);
        _files.BeginPass();
        await ObservationResourceIntents.SeedAsync(library.DirectoryPath, ct);
        var activeIntents = await ObservationResourceIntents.ActiveIdsAsync(library.DirectoryPath, ct);
        var local = await library.ReadSummaryAsync(ct);
        var rootFacts = new Dictionary<string, bool>(StringComparer.Ordinal); int probes = 0, enumerations = 0, entries = 0;
        ObservationFacts Probe(string path) { ct.ThrowIfCancellationRequested(); probes++; return _files.Inspect(path); }
        IReadOnlyList<string> Children(string path, int limit) { enumerations++; var rows = _files.Children(path, limit, ct); entries += rows.Count; return rows; }
        foreach (var root in settings.Roots ?? [])
        {
            var found = Probe(root.Path); rootFacts[root.Id] = found.State == "present" && found.Directory && found.Identity == root.Identity;
        }
        bool Affected(string path) => dirtyPaths is null || dirtyPaths.Any(p => ObservationSettingsStore.Within(p, path) || ObservationSettingsStore.Within(path, p));
        var installations = prior.Installations.ToDictionary(i => i.Id, StringComparer.Ordinal); var packages = prior.Packages.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var proposed = new Dictionary<string, (LocalInstallationView Before, string Status)>(StringComparer.Ordinal);
        var packageChanges = new Dictionary<string, (LocalOwnedPackage Before, string Status)>(StringComparer.Ordinal);
        var activeRoots = settings.Roots ?? []; bool deferred = false, degraded = rootFacts.Values.Any(v => !v);
        var profilesByInstallation = profiles.ToLookup(p => p.InstallationId, StringComparer.Ordinal);
        foreach (var view in local.Installations.Where(v => v.Installation.Details.DeviceId == deviceId && v.Installation.RemovedAt is null && v.Installation.MergedIntoId is null))
        {
            var item = view.Installation; var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Details.RootPath));
            var root = activeRoots.SingleOrDefault(r => r.Kind == "games" && ObservationSettingsStore.Within(r.Path, path));
            if (root is null || !Affected(path)) continue;
            var old = installations.GetValueOrDefault(item.Id); if (old?.RootId != root.Id) old = null;
            LocalInstallationObservation row;
            if (activeIntents.Contains(item.Id))
            { deferred = true; row = new(item.Id, root.Id, path, old?.Identity, old?.Anchors ?? [], item.Status, "archive_pending", "unknown", Complete: old?.Complete ?? false, LaunchSignature: old?.LaunchSignature); }
            else if (!rootFacts[root.Id]) row = new(item.Id, root.Id, path, old?.Identity, old?.Anchors ?? [], "unknown", "root_unavailable", "unknown", Complete: old?.Complete ?? false, LaunchSignature: old?.LaunchSignature);
            else
            {
                try { row = InspectInstallation(view, root, old, profilesByInstallation[item.Id].ToArray(), now, Probe, Children); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
                { row = new(item.Id, root.Id, path, old?.Identity, old?.Anchors ?? [], "unknown", "unavailable", "unknown", Complete: old?.Complete ?? false, LaunchSignature: old?.LaunchSignature); }
            }
            installations[item.Id] = row;
            if (row.Reason != "archive_pending" && row.Status != item.Status) proposed[item.Id] = (view, row.Status);
            if (probes % 64 == 0) await Task.Yield();
        }
        foreach (var package in (local.Packages ?? []).Where(p => p.DeviceId == deviceId))
        {
            var root = activeRoots.SingleOrDefault(r => r.Kind == "packages" && ObservationSettingsStore.Within(r.Path, package.ScanRoot));
            if (root is null) continue;
            var path = OwnedPackages.FilePath(package.ScanRoot, package.RelativePath); if (!Affected(path)) continue;
            var old = packages.GetValueOrDefault(package.Id); if (old?.RootId != root.Id) old = null;
            PackageObservation row;
            if (!rootFacts[root.Id]) row = new(package.Id, root.Id, "unknown", "root_unavailable", Identity: old?.Identity);
            else
            {
                var fact = Probe(path);
                if (fact.State == "missing")
                { var since = package.Status == "present" && old?.Status == "missing" ? now : old?.NegativeSince ?? now; var confirmed = now - since >= NegativeDelay; row = new(package.Id, root.Id, confirmed ? "missing" : "unknown", confirmed ? "missing" : "confirming_missing", since, old?.Identity); }
                else if (fact.State != "present" || fact.Directory || fact.Length != package.Length || fact.LastWriteUtc != package.LastWriteUtc ||
                    old?.Identity is { } id && id != fact.Identity && !(old.Status == "unknown" && package.Status == "present")) row = new(package.Id, root.Id, "unknown", "file_changed", Identity: old?.Identity);
                else row = new(package.Id, root.Id, "present", "present", Identity: fact.Identity);
            }
            packages[package.Id] = row; if (row.Status != package.Status) packageChanges[package.Id] = (package, row.Status);
        }
        var watchPaths = new HashSet<string>(ObservationSettingsStore.Paths); var candidates = prior.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        Queue<string>? completedCandidates = null;
        bool CandidateRoom(string id)
        {
            if (candidates.ContainsKey(id) || candidates.Count < 5000) return true;
            completedCandidates ??= new(candidates.Values.Where(c => c.State is "registered" or "missing").OrderBy(c => c.StableSince).Select(c => c.Id));
            while (completedCandidates.TryDequeue(out var completed))
                if (candidates.TryGetValue(completed, out var row) && row.State is "registered" or "missing") { candidates.Remove(completed); return true; }
            return false; // Pending decisions and explicit ignores are never evicted.
        }
        var identities = local.Installations.Where(i => i.Installation.Details.DeviceId == deviceId && i.Installation.RemovedAt is null)
            .Select(i => (View: i, Identity: i.Installation.DirectoryIdentity ?? installations.GetValueOrDefault(i.Installation.Id)?.Identity)).Where(p => p.Identity is not null).ToLookup(p => p.Identity!);
        var packageIdentities = (local.Packages ?? []).Where(p => p.DeviceId == deviceId).Select(p => (Package: p, Identity: packages.GetValueOrDefault(p.Id)?.Identity))
            .Where(p => p.Identity is not null).ToLookup(p => p.Identity!);
        var incompleteDiscovery = new HashSet<string>(StringComparer.Ordinal);
        if (discover)
        {
            var knownDirectories = local.Installations.Where(i => i.Installation.Details.DeviceId == deviceId && i.Installation.RemovedAt is null)
                .Select(i => i.Installation.Details.RootPath).ToHashSet(ObservationSettingsStore.Paths);
            var knownPackages = (local.Packages ?? []).Where(p => p.DeviceId == deviceId).Select(p => OwnedPackages.FilePath(p.ScanRoot, p.RelativePath)).ToHashSet(ObservationSettingsStore.Paths);
            foreach (var root in activeRoots.Where(r => rootFacts[r.Id] && (dirtyPaths is null || dirtyPaths.Any(p => ObservationSettingsStore.Within(r.Path, p) || ObservationSettingsStore.Within(p, r.Path)))))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal); var complete = true;
                var scannedParents = new HashSet<string>(ObservationSettingsStore.Paths);
                bool UnderKnown(string path)
                {
                    for (var current = path; current is not null && ObservationSettingsStore.Within(root.Path, current); current = Path.GetDirectoryName(current))
                        if (knownDirectories.Contains(current)) return true;
                    return false;
                }
                void Walk(string parent, int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    if (enumerations >= 1024 || entries >= 10000) { complete = false; return; }
                    watchPaths.Add(parent);
                    try
                    {
                        scannedParents.Add(parent);
                        foreach (var path in Children(parent, Math.Min(2000, 10001 - entries)))
                        {
                            var facts = Probe(path); if (facts.State != "present" || facts.Identity is null) { complete = false; continue; }
                            if (facts.Directory && UnderKnown(path)) continue;
                            if (facts.Directory && depth + 1 < root.Depth) { if (Affected(path)) Walk(path, depth + 1); continue; }
                            var kind = root.Kind == "games" && facts.Directory ? "directory" : root.Kind == "packages" && !facts.Directory && OwnedPackages.Identify(Path.GetFileName(path)) is not null && !knownPackages.Contains(path) ? "package" : null;
                            if (kind is null) continue;
                            var relative = Path.GetRelativePath(root.Path, path).Replace('\\', '/'); var id = CandidateId(root.Id, relative); seen.Add(id);
                            var old = candidates.GetValueOrDefault(id); var same = old is not null && old.Identity == facts.Identity && old.Length == facts.Length && old.LastWriteUtc == facts.LastWriteUtc && old.State != "missing";
                            var since = same ? old!.StableSince : now;
                            var matches = kind == "directory" ? identities[facts.Identity].Where(p => !ObservationSettingsStore.Paths.Equals(p.View.Installation.Details.RootPath, path)).ToArray() : [];
                            var moved = matches.Length == 1 && Probe(matches[0].View.Installation.Details.RootPath).State == "missing" ? matches[0].View : null;
                            if (moved is not null && installations.TryGetValue(moved.Installation.Id, out var location))
                            {
                                installations[moved.Installation.Id] = location with { Status = "unknown", Reason = "location_changed", LaunchState = "unknown", NegativeSince = null, ReadyProfiles = null };
                                if (moved.Installation.Status != "unknown") proposed[moved.Installation.Id] = (moved, "unknown"); else proposed.Remove(moved.Installation.Id);
                            }
                            var packageMatches = kind == "package" ? packageIdentities[facts.Identity].Where(p => !ObservationSettingsStore.Paths.Equals(OwnedPackages.FilePath(p.Package.ScanRoot, p.Package.RelativePath), path)).ToArray() : [];
                            var movedPackage = packageMatches.Length == 1 && Probe(OwnedPackages.FilePath(packageMatches[0].Package.ScanRoot, packageMatches[0].Package.RelativePath)).State == "missing" ? packageMatches[0].Package : null;
                            if (movedPackage is not null && packages.TryGetValue(movedPackage.Id, out var packageLocation))
                            {
                                packages[movedPackage.Id] = packageLocation with { Status = "unknown", Reason = "location_changed", NegativeSince = null };
                                if (movedPackage.Status != "unknown") packageChanges[movedPackage.Id] = (movedPackage, "unknown"); else packageChanges.Remove(movedPackage.Id);
                            }
                            if (!CandidateRoom(id)) { complete = false; continue; }
                            candidates[id] = new(id, root.Id, relative, kind, facts.Identity, facts.Length, facts.LastWriteUtc, since,
                                same && old!.State == "ignored" ? "ignored" : now - since >= CandidateDelay ? "pending" : "checking", moved?.Installation.Id,
                                moved?.Installation.Details.RootPath ?? (movedPackage is null ? null : OwnedPackages.FilePath(movedPackage.ScanRoot, movedPackage.RelativePath)), movedPackage?.Id);
                        }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { complete = false; }
                }
                Walk(root.Path, 0);
                foreach (var old in candidates.Values.Where(c => c.RootId == root.Id && !seen.Contains(c.Id) && c.State != "ignored" && scannedParents.Contains(Path.GetDirectoryName(Path.Combine(root.Path, c.RelativePath.Replace('/', Path.DirectorySeparatorChar)))!)).ToArray())
                    {
                        var path = Path.Combine(root.Path, old.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        var registered = knownDirectories.Contains(path) || knownPackages.Contains(path);
                        if (registered || complete) candidates[old.Id] = old with { State = registered ? "registered" : "missing" };
                    }
                degraded |= !complete;
                if (!complete) incompleteDiscovery.Add(root.Id);
            }
        }
        foreach (var pair in proposed.Where(p => p.Value.Status == "missing" && (!discover || incompleteDiscovery.Contains(installations[p.Key].RootId))).ToArray())
        {
            installations[pair.Key] = installations[pair.Key] with { Status = "unknown", Reason = "insufficient_evidence" };
            if (pair.Value.Before.Installation.Status == "unknown") proposed.Remove(pair.Key); else proposed[pair.Key] = (pair.Value.Before, "unknown");
        }
        foreach (var pair in packageChanges.Where(p => p.Value.Status == "missing" && (!discover || incompleteDiscovery.Contains(packages[p.Key].RootId))).ToArray())
        {
            packages[pair.Key] = packages[pair.Key] with { Status = "unknown", Reason = "insufficient_evidence" };
            if (pair.Value.Before.Status == "unknown") packageChanges.Remove(pair.Key); else packageChanges[pair.Key] = (pair.Value.Before, "unknown");
        }
        int changed = 0, changedPackages = 0, skipped = 0, writes = 0;
        if (proposed.Count > 0 || packageChanges.Count > 0)
        {
            local = await library.UpdateAvailabilityAsync(current => current with
            {
                Installations = current.Installations.Select(v =>
                {
                    if (!proposed.TryGetValue(v.Installation.Id, out var change)) return v;
                    if (LocalArchiveFormat.Hash(v) != LocalArchiveFormat.Hash(change.Before))
                    { skipped++; installations[v.Installation.Id] = installations[v.Installation.Id] with { Status = "unknown", Reason = "concurrent_change", LaunchState = "unknown", NegativeSince = null, ReadyProfiles = null }; return v; }
                    changed++; return v with { Installation = v.Installation with { Status = change.Status, Revision = v.Installation.Revision + 1, ObservedAt = now, UpdatedAt = now } };
                }).ToArray(),
                Packages = current.Packages?.Select(p =>
                {
                    if (!packageChanges.TryGetValue(p.Id, out var change)) return p;
                    if (LocalArchiveFormat.Hash(p) != LocalArchiveFormat.Hash(change.Before))
                    { skipped++; packages[p.Id] = packages[p.Id] with { Status = "unknown", Reason = "concurrent_change", NegativeSince = null }; return p; }
                    changedPackages++; return p with { Status = change.Status, Revision = p.Revision + 1, ObservedAt = now };
                }).ToArray()
            }, ct);
            if (changed + changedPackages > 0) writes++;
        }
        var snapshot = new ObservationSnapshot(deviceId, installations.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), packages.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), candidates.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray());
        if (!prior.Installations.SequenceEqual(snapshot.Installations) || !prior.Packages.SequenceEqual(snapshot.Packages) || !prior.Candidates.SequenceEqual(snapshot.Candidates)) { await state.SaveAsync(snapshot, ct); writes++; }
        return new(snapshot, new(rootFacts.Count, probes, enumerations, entries, changed, changedPackages, skipped, writes), watchPaths.ToArray(), degraded, deferred, now, local);
    }
    private static string CandidateId(string rootId, string relative) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rootId + "\n" + relative)));
    private static LocalInstallationObservation InspectInstallation(LocalInstallationView view, ObservationRoot root, LocalInstallationObservation? old,
        IReadOnlyList<LaunchProfile> profiles, DateTimeOffset now, Func<string, ObservationFacts> probe, Func<string, int, IReadOnlyList<string>> children)
    {
        var item = view.Installation; var path = item.Details.RootPath; var directory = probe(path); var trusted = item.DirectoryIdentity ?? old?.Identity;
        if (directory.State == "missing")
        {
            var since = item.Status == "present" && old?.Status == "missing" ? now : old?.NegativeSince ?? now; var confirmed = now - since >= NegativeDelay;
            return new(item.Id, root.Id, path, trusted, old?.Anchors ?? [], confirmed ? "missing" : "unknown", confirmed ? "missing" : "confirming_missing", "entry_missing", since, old?.Complete ?? false, old?.LaunchSignature);
        }
        if (directory is not { State: "present", Directory: true, Identity: not null }) return new(item.Id, root.Id, path, trusted, old?.Anchors ?? [], "unknown", "unavailable", "unknown", Complete: old?.Complete ?? false, LaunchSignature: old?.LaunchSignature);
        if (trusted is not null && trusted != directory.Identity) return new(item.Id, root.Id, path, trusted, old?.Anchors ?? [], "unknown", "identity_changed", "changed", Complete: old?.Complete ?? false, LaunchSignature: old?.LaunchSignature);
        if (trusted is null && item.Status != "present") return new(item.Id, root.Id, path, null, [], "unknown", "insufficient_evidence", "unknown");
        var componentFolders = new List<string>();
        foreach (var component in view.Components.Take(8))
        {
            var relative = component.Details.RelativePath;
            if (relative != "." && (relative.Contains(':') || Path.IsPathRooted(relative) || relative.Replace('\\', '/').Split('/').Any(s => s is "" or "." or ".."))) throw new InvalidDataException("组件越界。");
            var folder = relative == "." ? path : Path.GetFullPath(Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!ObservationSettingsStore.Within(path, folder)) throw new InvalidDataException("组件越界。");
            var componentFact = relative == "." ? directory : probe(folder);
            if (componentFact is not { State: "present", Directory: true } || componentFact.Identity?.Volume != directory.Identity.Volume)
                return new(item.Id, root.Id, path, directory.Identity, old?.Anchors ?? [], "unknown", "insufficient_evidence", "unknown", LaunchSignature: old?.LaunchSignature);
            componentFolders.Add(folder);
        }
        if (view.Components.Count > 8) return new(item.Id, root.Id, path, directory.Identity, old?.Anchors ?? [], "unknown", "insufficient_evidence", "unknown", LaunchSignature: old?.LaunchSignature);
        var commands = profiles.Where(p => p.InstallationId == item.Id).ToArray();
        var programs = commands.SelectMany(p => new[] { p.Executable, p.TrackedExecutable }).OfType<string>().Distinct(ObservationSettingsStore.Paths).ToArray();
        if (programs.Length > 16) throw new InvalidDataException("启动入口过多，需人工核对。");
        var signature = LocalArchiveFormat.Hash(commands.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => new { p.Id, p.Executable, p.Sha256, p.TrackedExecutable, p.TrackedSha256 }).ToArray());
        var confirmedWithoutLauncher = commands.Length == 0 && item.Status == "present" && old is { Status: "unknown", Reason: "file_changed" };
        var anchors = old?.Identity == directory.Identity && old.LaunchSignature == signature && !confirmedWithoutLauncher ? old.Anchors : Array.Empty<ObservationFile>(); var complete = old?.Identity == directory.Identity && old.LaunchSignature == signature && !confirmedWithoutLauncher && old.Complete;
        if (anchors.Count == 0)
        {
            var paths = new HashSet<string>(programs.Where(p => ObservationSettingsStore.Within(path, p)), ObservationSettingsStore.Paths); complete = true;
            foreach (var folder in componentFolders.Distinct(ObservationSettingsStore.Paths))
            {
                try
                {
                    foreach (var file in children(folder, 128).Where(p => Content.Contains(Path.GetExtension(p))).Take(16)) paths.Add(file);
                }
                catch (IOException) { complete = false; }
            }
            if (view.Components.Count > 8) complete = false;
            if (paths.Count > 32) complete = false;
            anchors = paths.Select(p => (Path: p, Facts: probe(p))).Where(p => p.Facts is { State: "present", Directory: false, Identity: not null })
                .Select(p => new ObservationFile(Path.GetRelativePath(path, p.Path).Replace('\\', '/'), p.Facts.Identity!, p.Facts.Length, p.Facts.LastWriteUtc)).Take(32).ToArray();
        }
        var facts = anchors.ToDictionary(a => a.Path, a => probe(OwnedPackages.FilePath(path, a.Path)), StringComparer.Ordinal);
        if (anchors.Count > 0 && facts.Values.Any(f => f.State == "present") && !anchors.Any(a => facts[a.Path] is { State: "present" } f && f.Identity == a.Identity && f.Length == a.Length && f.LastWriteUtc == a.LastWriteUtc))
            return new(item.Id, root.Id, path, directory.Identity, anchors, "unknown", "file_changed", "changed", Complete: complete, LaunchSignature: signature);
        var presentContent = anchors.Any(a => Content.Contains(Path.GetExtension(a.Path)) && facts[a.Path].State == "present");
        var currentPrograms = programs.Select(p => (Path: p, Facts: probe(p))).ToArray();
        var entryMissing = programs.Length > 0 && currentPrograms.Any(p => p.Facts.State != "present" || p.Facts.Directory);
        var entryChanged = anchors.Any(a => programs.Any(p => ObservationSettingsStore.Paths.Equals(p, Path.Combine(path, a.Path.Replace('/', Path.DirectorySeparatorChar)))) &&
            facts[a.Path] is { State: "present" } f && (f.Identity != a.Identity || f.Length != a.Length || f.LastWriteUtc != a.LastWriteUtc));
        var launch = programs.Length == 0 ? "not_configured" : entryChanged ? "changed" : entryMissing ? "entry_missing" : "ready";
        IReadOnlyList<string> readyProfiles = commands.Where(p => new[] { p.Executable, p.TrackedExecutable }.OfType<string>().All(program =>
            currentPrograms.Any(v => ObservationSettingsStore.Paths.Equals(v.Path, program) && v.Facts is { State: "present", Directory: false }) &&
            !anchors.Any(a => ObservationSettingsStore.Paths.Equals(program, Path.Combine(path, a.Path.Replace('/', Path.DirectorySeparatorChar))) &&
                facts[a.Path] is { State: "present" } f && (f.Identity != a.Identity || f.Length != a.Length || f.LastWriteUtc != a.LastWriteUtc))))
            .Select(p => p.Id).ToArray();
        if (old?.ReadyProfiles is { } priorReady && priorReady.SequenceEqual(readyProfiles)) readyProfiles = priorReady;
        if (readyProfiles.Count > 0) launch = "ready";
        var actualPrograms = commands.Select(p => p.TrackedExecutable ?? p.Executable).ToHashSet(ObservationSettingsStore.Paths);
        var any = presentContent || currentPrograms.Any(p => actualPrograms.Contains(p.Path) && ObservationSettingsStore.Within(path, p.Path) && p.Facts.State == "present" && !p.Facts.Directory);
        if (any) return new(item.Id, root.Id, path, directory.Identity, anchors, "present", entryChanged ? "file_changed" : entryMissing ? "entry_missing" : "present", launch, Complete: complete, LaunchSignature: signature, ReadyProfiles: readyProfiles);
        if (anchors.Count > 0 && complete && facts.Values.All(f => f.State == "missing"))
        {
            bool Residual(string folder) => children(folder, 128).All(p =>
            {
                var name = Path.GetFileName(p).ToLowerInvariant();
                return name is "save" or "saves" or "savedata" or "log" or "logs" or "readme.txt" or "desktop.ini" || Path.GetExtension(name) is ".sav" or ".log";
            });
            if (!Residual(path) || view.Components.Where(c => c.Details.RelativePath != ".").Any(c => !Residual(Path.Combine(path, c.Details.RelativePath.Replace('/', Path.DirectorySeparatorChar)))) )
                return new(item.Id, root.Id, path, directory.Identity, anchors, "unknown", "insufficient_evidence", launch, Complete: complete, LaunchSignature: signature);
            var since = old?.NegativeSince ?? now; var confirmed = now - since >= NegativeDelay;
            return new(item.Id, root.Id, path, directory.Identity, anchors, confirmed ? "missing" : "unknown", confirmed ? "missing" : "confirming_missing", launch, since, complete, signature);
        }
        return new(item.Id, root.Id, path, directory.Identity, anchors, "unknown", "insufficient_evidence", launch, LaunchSignature: signature);
    }
}
