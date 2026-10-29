namespace Vndb.Local;

public sealed record LaunchConfigurationDraft(string Title, string Executable, IReadOnlyList<string> Arguments,
    string WorkingDirectory, LocalInstallationView? Installation, string ComponentId, LaunchProfile? Previous);

public sealed class LaunchConfiguration(LocalLauncher launcher)
{
    public Task<LaunchProfile> SaveAsync(LaunchConfigurationDraft draft, string? deviceId, CancellationToken ct)
    {
        if (draft.Installation is { } installation && installation.Installation.Details.DeviceId != deviceId)
            throw new ArgumentException("启动配置只能关联本机安装；离线独立配置可不关联安装。");
        if (draft.Installation is not null && draft.ComponentId.Length > 0 && !draft.Installation.Components.Any(c => c.Id == draft.ComponentId))
            throw new ArgumentException("组件ID不属于所选完整安装。");
        if (draft.Installation is null && draft.ComponentId != (draft.Previous?.ComponentId ?? ""))
            throw new ArgumentException("更改组件ID需要明确选择本机完整安装；原关联可离线保留。");
        return launcher.SaveAsync(draft.Title, draft.Executable, draft.Arguments, draft.WorkingDirectory,
            draft.Installation?.Installation.Id ?? draft.Previous?.InstallationId, string.IsNullOrEmpty(draft.ComponentId) ? null : draft.ComponentId,
            draft.Previous?.Id, ct);
    }
}
