using Vndb.Contracts;
using System.Text.Json;
using Vndb.Core;

namespace Vndb.Local;

public sealed record ClientDeviceIdentity(string Id, string Name, string Platform, DateTimeOffset CreatedAt);

public static class LocalDeviceIdentity
{
    public static async Task<ClientDeviceIdentity> LoadOrCreateAsync(string? directory = null, CancellationToken ct = default)
    {
        if (Path.GetFullPath(directory ?? ClientSettings.UserDirectory).Equals(Path.GetFullPath(LocalDataRoot.Default), StringComparison.OrdinalIgnoreCase))
            await LocalDataRoot.MigrateAsync(LocalDataRoot.Legacy, LocalDataRoot.Default, ct);
        var path = Path.Combine(Path.GetFullPath(directory ?? ClientSettings.UserDirectory), "device.json");
        using var lease = await LocalFileLease.AcquireAsync(Path.GetDirectoryName(path)!, "device-identity", ct);
        LocalFileLease.RequireOrdinaryAncestors(path);
        if (!File.Exists(path))
        {
            var created = new ClientDeviceIdentity(Guid.NewGuid().ToString("D"), Environment.MachineName,
                OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", DateTimeOffset.UtcNow);
            try { await JsonSnapshotStore.AtomicWriteAsync(path, created, false, ct); }
            catch (IOException) when (File.Exists(path)) { } // Another client in this profile won creation; read the durable identity.
        }
        ClientDeviceIdentity? identity;
        try { identity = JsonSerializer.Deserialize<ClientDeviceIdentity>(await File.ReadAllTextAsync(path, ct), VndbJson.Options); }
        catch (JsonException) { throw new InvalidDataException("设备身份文件无效，原文件保留。"); }
        if (identity is null || !Guid.TryParseExact(identity.Id, "D", out var id) || id.ToString("D") != identity.Id || string.IsNullOrWhiteSpace(identity.Name))
            throw new InvalidDataException("设备身份文件无效，原文件保留，请明确恢复而非自动换ID。");
        return identity;
    }
}
