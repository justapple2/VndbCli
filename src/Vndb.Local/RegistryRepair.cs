using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vndb.Local;

public sealed record RegistryLocation(string Hive, int View, string Key, string Name);
public sealed record RegistryText(string Data, int Kind = 1);
public sealed record RegistryEntry(RegistryLocation Location, RegistryText Value);
public sealed record RegistryChange(RegistryLocation Location, RegistryText? Before, RegistryText After);
public sealed record RegistryRepairPlan(string Id, string Source, string Target, IReadOnlyList<RegistryChange> Changes);
public sealed record RegistrySearch(IReadOnlyList<RegistryEntry> Entries, IReadOnlyList<string> Warnings);
public interface IGameRegistry
{
    RegistrySearch Find(string term, CancellationToken ct);
    RegistryText? Read(RegistryLocation location);
    void Write(RegistryLocation location, RegistryText? value);
}

/// <summary>Only vendor Software string paths. No executable .reg import, elevation or Web transport.</summary>
public sealed partial class RegistryRepair(IGameRegistry registry, string directory)
{
    public static void Validate(RegistryLocation location)
    {
        var parts = location.Key.Split('\\');
        if (location.Hive is not ("HKCU" or "HKLM") || location.View is not (32 or 64) || parts.Length < 2 ||
            !parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.Length == 0 || p is "." or "..") ||
            new[] { "Microsoft", "Classes", "Policies", "WOW6432Node", "Windows" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase) ||
            location.Key.Length > 512 || location.Name.Length > 512 || location.Key.Any(char.IsControl) || location.Name.Any(char.IsControl))
            throw new ArgumentException("仅支持HKCU／HKLM的独立厂商Software键；系统、Classes和策略键不处理。32／64位视图单独选择。");
    }
    public static string? MapPath(string value, string source, string target)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)); target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (source.Length < 4 || target.Length < 4) throw new ArgumentException("需要完整游戏目录，不能使用磁盘根目录。");
        if (value.Equals(source, StringComparison.OrdinalIgnoreCase)) return target;
        if (value.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var canonical = LocalFiles.Full(value);
                if (!LocalFiles.Within(source, canonical)) return null;
                return target + value[source.Length..];
            }
            catch (ArgumentException) { return null; }
        }
        return null;
    }
    public RegistryRepairPlan Preview(IEnumerable<RegistryEntry> entries, string source, string target)
    {
        source = LocalFiles.Full(source); target = LocalFiles.Full(target); LocalFiles.CheckAncestors(target);
        if (!Directory.Exists(target)) throw new IOException("目标游戏目录不存在。");
        var changes = new List<RegistryChange>();
        foreach (var entry in entries)
        {
            Validate(entry.Location);
            if (entry.Value.Kind is not (1 or 2) || entry.Value.Data.Length > 32768 || entry.Value.Data.Contains('\0')) throw new ArgumentException("仅支持有效的字符串和可扩展字符串。");
            var mapped = MapPath(entry.Value.Data, source, target);
            if (mapped is null) continue;
            var before = registry.Read(entry.Location); var after = entry.Value with { Data = mapped };
            if (before is not null && before != after && MapPath(before.Data, source, target) is null)
                throw new IOException("现有注册值不属于指定旧游戏目录，停止覆盖。请核对游戏和键值。");
            if (before != after) changes.Add(new(entry.Location, before, after));
        }
        if (changes.Count == 0 || changes.Count > 200 || changes.Select(c => $"{c.Location.Hive}:{c.Location.View}:{c.Location.Key}:{c.Location.Name}").Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count)
            throw new ArgumentException("没有可更新的目录路径，或候选重复／超过200项。不会导入无关值。");
        return new(Guid.NewGuid().ToString("D"), source, target, changes);
    }
    public async Task<string> ApplyAsync(RegistryRepairPlan plan, CancellationToken ct)
    {
        ValidatePlan(plan);
        foreach (var change in plan.Changes)
            if (registry.Read(change.Location) != change.Before) throw new IOException("注册信息已改变，请重新预览。尚未写入。");
        var run = Path.Combine(directory, "registry-repairs", plan.Id); LocalFiles.CheckAncestors(directory);
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(run, "intent.json"), plan, false, ct);
        foreach (var change in plan.Changes)
        {
            ct.ThrowIfCancellationRequested();
            var current = registry.Read(change.Location);
            // Some HKCU vendor keys are shared between WOW64 views. An earlier change may already have set this exact value.
            if (current == change.After) continue;
            if (current != change.Before) throw new IOException("应用中注册信息发生变化，已保存恢复记录，请核对。");
            registry.Write(change.Location, change.After);
            if (registry.Read(change.Location) != change.After) throw new IOException("注册信息写后核对失败，恢复记录保留。");
        }
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(run, "applied.json"), new { at = DateTimeOffset.UtcNow }, false, ct);
        return run;
    }
    public async Task RestoreAsync(RegistryRepairPlan plan, CancellationToken ct)
    {
        ValidatePlan(plan);
        foreach (var change in plan.Changes)
        {
            var current = registry.Read(change.Location);
            if (current != change.After && current != change.Before) throw new IOException("注册信息已被其他程序改变，停止恢复以保留新值。");
        }
        foreach (var change in plan.Changes.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var current = registry.Read(change.Location);
            if (current == change.Before) continue;
            if (current != change.After) throw new IOException("恢复中发现外部变更，停止。");
            registry.Write(change.Location, change.Before);
            if (registry.Read(change.Location) != change.Before) throw new IOException("恢复后核对失败。");
        }
        await JsonSnapshotStore.AtomicWriteAsync(Path.Combine(directory, "registry-repairs", plan.Id, "restored-" + Guid.NewGuid().ToString("N") + ".json"), new { at = DateTimeOffset.UtcNow }, false, ct);
    }
    private static void ValidatePlan(RegistryRepairPlan plan)
    {
        if (!Guid.TryParseExact(plan.Id, "D", out _) || plan.Changes is null || plan.Changes.Count is < 1 or > 200) throw new ArgumentException("修复计划无效。");
        foreach (var change in plan.Changes)
        {
            if (change is null || change.Location is null || change.After?.Data is null || change.Before is { Data: null }) throw new ArgumentException("修复记录不完整。");
            Validate(change.Location);
            if (change.After.Kind is not (1 or 2) || change.Before is { Kind: not (1 or 2) } || MapPath(change.After.Data, plan.Target, plan.Source) is null)
                throw new ArgumentException("修复计划包含非目录字符串。");
        }
    }
    public IReadOnlyList<string> Runs() => Directory.Exists(Path.Combine(directory, "registry-repairs"))
        ? Directory.EnumerateDirectories(Path.Combine(directory, "registry-repairs")).Where(p => Guid.TryParseExact(Path.GetFileName(p), "D", out _))
            .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "intent.json"))).Take(100).Select(p => Path.GetFileName(p)!).ToArray() : [];
    public async Task<RegistryRepairPlan> ReadRunAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("请选择本机恢复记录。");
        var file = Path.Combine(directory, "registry-repairs", id, "intent.json"); LocalFiles.CheckAncestors(file);
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("恢复记录超过2MiB。");
        var plan = System.Text.Json.JsonSerializer.Deserialize<RegistryRepairPlan>(await File.ReadAllTextAsync(file, ct), Vndb.Core.VndbJson.Options) ?? throw new InvalidDataException("恢复记录无效。");
        ValidatePlan(plan); if (plan.Id != id) throw new InvalidDataException("恢复记录ID不符。"); return plan;
    }
    public static RegistrySearch ParseReg(string text, int view)
    {
        var header = text.TrimStart('\uFEFF').Split('\n', 2)[0].Trim();
        if (text.Length > 2 * 1024 * 1024 || header is not ("Windows Registry Editor Version 5.00" or "REGEDIT4"))
            throw new InvalidDataException("仅支持不超过2MiB的Windows Registry Editor Version 5.00／REGEDIT4文本。");
        var entries = new List<RegistryEntry>(); var warnings = new List<string>(); RegistryLocation? key = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("Windows Registry Editor") || line == "REGEDIT4") continue;
            if (line.StartsWith('['))
            {
                key = null;
                var match = KeyLine().Match(line);
                if (!match.Success) { warnings.Add("跳过删除键或不支持的键头。"); continue; }
                var proposed = new RegistryLocation(match.Groups[1].Value == "HKEY_CURRENT_USER" ? "HKCU" : "HKLM", view, match.Groups[2].Value, "");
                const string redirected = "Software\\WOW6432Node\\";
                if (proposed.Hive == "HKLM" && proposed.Key.StartsWith(redirected, StringComparison.OrdinalIgnoreCase))
                {
                    proposed = proposed with { View = 32, Key = "Software\\" + proposed.Key[redirected.Length..] };
                    warnings.Add("文件中的HKLM WOW6432Node厂商路径已映射到显式32位视图。");
                }
                try { Validate(proposed); key = proposed; } catch (ArgumentException) { warnings.Add("跳过系统或不支持的注册表键。"); }
                continue;
            }
            if (key is null) continue;
            var value = StringLine().Match(line);
            if (!value.Success) { warnings.Add("跳过非字符串／删除值／二进制或多行值；未执行任何内容。"); continue; }
            entries.Add(new(key with { Name = value.Groups[1].Success ? Decode(value.Groups[1].Value) : "" }, new(Decode(value.Groups[2].Value))));
            if (entries.Count > 200) throw new InvalidDataException("注册信息超过200项，请缩小范围。");
        }
        return new(entries, warnings.Distinct().ToArray());
    }
    public static async Task<string> ReadTextAsync(string file, string encoding, CancellationToken ct)
    {
        LocalFiles.CheckAncestors(file);
        if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new IOException("注册信息文件超过2MiB。");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var selected = encoding switch
        {
            "日文 Shift-JIS" => Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            "简体中文 GBK" => Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
            _ => new UTF8Encoding(false, true)
        };
        using var reader = new StreamReader(file, selected, true);
        try
        {
            var text = await reader.ReadToEndAsync(ct);
            if (text.Contains('\uFFFD')) throw new InvalidDataException("REG文本含编码替换字符，请核对原文件编码。");
            return text;
        }
        catch (DecoderFallbackException e) { throw new InvalidDataException("REG文本编码不符，请选择原文件编码后重读。不会使用乱码修复路径。", e); }
    }
    private static string Decode(string value) => Escapes().Replace(value, m => m.Value[1].ToString());
    [GeneratedRegex("^\\[(HKEY_CURRENT_USER|HKEY_LOCAL_MACHINE)\\\\([^]\\r\\n]+)\\]$", RegexOptions.CultureInvariant)] private static partial Regex KeyLine();
    [GeneratedRegex("^(?:\"((?:[^\"\\\\]|\\\\[\\\\\"])*)\"|@)\\s*=\\s*\"((?:[^\"\\\\]|\\\\[\\\\\"])*)\"$", RegexOptions.CultureInvariant)] private static partial Regex StringLine();
    [GeneratedRegex("\\\\[\\\\\"]", RegexOptions.CultureInvariant)] private static partial Regex Escapes();
}

[SupportedOSPlatform("windows")]
public sealed class WindowsGameRegistry : IGameRegistry
{
    public RegistrySearch Find(string term, CancellationToken ct)
    {
        if (term.Trim().Length < 2) throw new ArgumentException("请输入至少2字的会社或游戏名关键词。");
        var results = new List<RegistryEntry>(); var warnings = new List<string>(); var seen = 0;
        foreach (var hive in new[] { "HKCU", "HKLM" })
        foreach (var view in new[] { 32, 64 })
        {
            using var root = Open(hive, view); var queue = new Queue<(string Key, int Depth)>(); queue.Enqueue(("Software", 0));
            while (queue.TryDequeue(out var row))
            {
                ct.ThrowIfCancellationRequested();
                if (++seen > 30000 || results.Count >= 200) return new(results, ["搜索达到边界，请缩小关键词；结果可能不完整。"]);
                try
                {
                    using var key = root.OpenSubKey(row.Key, false); if (key is null) continue;
                    var location = new RegistryLocation(hive, view, row.Key, "");
                    var allowed = false; try { RegistryRepair.Validate(location); allowed = true; } catch (ArgumentException) { }
                    if (allowed && row.Key.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase))
                        foreach (var name in key.GetValueNames())
                        {
                            if (key.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
                            var entry = location with { Name = name }; var value = Read(entry);
                            if (value is not null) results.Add(new(entry, value));
                        }
                    if (row.Depth >= 6) continue;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        if (row.Depth == 0 && new[] { "Microsoft", "Classes", "Policies", "WOW6432Node", "Windows" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        queue.Enqueue((row.Key + "\\" + name, row.Depth + 1));
                    }
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { warnings.Add("部分键无权读取，已跳过。"); }
            }
        }
        return new(results, warnings.Distinct().ToArray());
    }
    public RegistryText? Read(RegistryLocation location)
    {
        RegistryRepair.Validate(location); using var root = Open(location.Hive, location.View); using var key = root.OpenSubKey(location.Key, false);
        if (key is null || !key.GetValueNames().Contains(location.Name, StringComparer.OrdinalIgnoreCase)) return null;
        var kind = key.GetValueKind(location.Name);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) throw new IOException("现有值不是路径字符串，停止处理。");
        return new((string)key.GetValue(location.Name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)!, (int)kind);
    }
    public void Write(RegistryLocation location, RegistryText? value)
    {
        RegistryRepair.Validate(location); using var root = Open(location.Hive, location.View);
        using var key = value is null ? root.OpenSubKey(location.Key, true) : root.CreateSubKey(location.Key, true);
        if (value is null) key?.DeleteValue(location.Name, false);
        else { if (value.Kind is not (1 or 2)) throw new ArgumentException("仅支持路径字符串。"); key!.SetValue(location.Name, value.Data, (RegistryValueKind)value.Kind); }
    }
    private static RegistryKey Open(string hive, int view) => RegistryKey.OpenBaseKey(hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, view == 32 ? RegistryView.Registry32 : RegistryView.Registry64);
}
