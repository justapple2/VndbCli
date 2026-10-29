using System.Text.Json;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public sealed record LaunchComponentChoice(string Id, string Title, string Kind, string RelativePath)
{
    public static string KindLabel(string code) => code switch { "game" => "本篇", "bonus" => "特典", "edition" => "版本／章节", "other" => "其他组件", _ => "待核对（" + code + "）" };
}

public partial class MainViewModel
{
    private bool _editingLaunchAssociation;
    public IReadOnlyList<LocalInstallationView> LaunchInstallations => _installations.Concat(SelectedInstallation is { } current ? [current] : [])
        .Where(i => i.Installation.RemovedAt is null).DistinctBy(i => i.Installation.Id).ToArray();
    public LocalInstallationView? SelectedLaunchInstallation
    {
        get => SelectedInstallation;
        set { if (value is not null && LaunchInstallations.Any(i => i.Installation.Id == value.Installation.Id)) SetLaunchInstallation(value); }
    }
    private void SetLaunchInstallation(LocalInstallationView? value)
    {
        _editingLaunchAssociation = true;
        try { SelectedInstallation = value; }
        finally { _editingLaunchAssociation = false; }
    }
    public IReadOnlyList<LaunchComponentChoice> LaunchComponents => SelectedInstallation?.Components.Select(c => new LaunchComponentChoice(c.Id,
        _books.Concat(_queryBooks).FirstOrDefault(b => b.Item?.Item.Id == c.Details.ItemId)?.Title ??
        _services?.LocalCatalog.Items.FirstOrDefault(i => i.Item.Id == c.Details.ItemId)?.Item.Details.Title ?? c.Details.Label ?? "作品名称待补全",
        LaunchComponentChoice.KindLabel(c.Details.Kind), c.Details.RelativePath)).ToArray() ?? [];
    public LaunchComponentChoice? SelectedLaunchComponent
    {
        get => LaunchComponents.FirstOrDefault(c => c.Id == ComponentId);
        set { if (value is not null && LaunchComponents.Any(c => c.Id == value.Id)) ComponentId = value.Id; }
    }
    partial void OnComponentIdChanged(string value) => OnPropertyChanged(nameof(SelectedLaunchComponent));
    public bool HasSavedLaunch => SelectedProfile is not null;
    public bool LaunchDraftChanged => SelectedProfile is { } p && (LaunchTitle != p.Title || Executable != p.Executable || WorkingDirectory != p.WorkingDirectory ||
        ComponentId != (p.ComponentId ?? "") || SelectedInstallation is { } installation && installation.Installation.Id != p.InstallationId ||
        ArgumentsJson != JsonSerializer.Serialize(p.Arguments) || _argumentsError is not null);
    public string LaunchDraftNotice => !HasSavedLaunch ? "新配置草稿：保存后才能预览命令或启动。" : LaunchDraftChanged
        ? "有未保存的配置修改；启动和命令预览仍使用已保存配置。请先保存以应用修改。"
        : "当前为已保存配置。可以直接启动，自动计时按需使用。";
}
