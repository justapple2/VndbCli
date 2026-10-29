namespace Vndb.Local;

public sealed record LocalProfileSnapshot(LocalClientSettings Settings, ClientDeviceIdentity Identity, LocalLibrary Library);

/// <summary>Local initialization only; no UI, transport or game launch.</summary>
public sealed class LocalClientProfile(LocalClientSettingsStore settingsStore, LocalLauncher launcher, LocalLibraryStore library)
{
    public async Task<LocalProfileSnapshot> LoadAsync(string? defaultServer, CancellationToken ct)
    {
        if (Path.GetFullPath(settingsStore.DirectoryPath).Equals(Path.GetFullPath(LocalDataRoot.Default), StringComparison.OrdinalIgnoreCase))
            await LocalDataRoot.MigrateAsync(LocalDataRoot.Legacy, settingsStore.DirectoryPath, ct);
        var catalog = await library.ReadAsync(ct);
        var settings = await settingsStore.LoadAsync(ct);
        if (settings.DefaultLocale is null)
        {
            var locales = (await launcher.ReadAsync(ct)).Where(p => p.Locale is not null).Select(p => p.Locale!).Distinct().ToArray();
            if (locales.Length == 1 && (string.IsNullOrWhiteSpace(settings.LocaleEmulatorPath) ||
                Path.GetFullPath(settings.LocaleEmulatorPath).Equals(locales[0].ToolPath, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var verified = await LocaleEmulator.CaptureAsync(new(locales[0].ToolPath, locales[0].ProfileId), ct);
                    if (verified == locales[0]) { settings = settings with { LocaleEmulatorPath = verified.ToolPath, DefaultLocale = verified }; await settingsStore.SaveAsync(settings, ct); }
                }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
            }
        }
        if (defaultServer is not null) settings = settings with { Server = LocalQueryScope.Normalize(defaultServer) };
        return new(settings, await LocalDeviceIdentity.LoadOrCreateAsync(settingsStore.DirectoryPath, ct), catalog);
    }
}
