using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<LocalInstallationView>? _resourceInstallationSource;
    private IReadOnlyList<LocalOwnedPackage>? _resourcePackageSource;
    private IReadOnlyList<LocalInstallationObservation>? _resourceObservationSource;
    private ILookup<string, LocalInstallationView>? _resourceInstallations;
    private ILookup<string, LocalOwnedPackage>? _resourcePackages;
    private IReadOnlyDictionary<string, LocalInstallationObservation> _resourceObservations = new Dictionary<string, LocalInstallationObservation>();
    private void IndexResources()
    {
        if (!ReferenceEquals(_resourceInstallationSource, _installations))
        {
            _resourceInstallationSource = _installations;
            _resourceInstallations = _installations.Where(i => i.Installation.RemovedAt is null)
                .SelectMany(i => i.Components.Select(c => (c.Details.ItemId, Installation: i)))
                .DistinctBy(p => (p.ItemId, p.Installation.Installation.Id)).ToLookup(p => p.ItemId, p => p.Installation, StringComparer.Ordinal);
        }
        if (!ReferenceEquals(_resourcePackageSource, _packages))
        { _resourcePackageSource = _packages; _resourcePackages = _packages.ToLookup(p => p.ItemId, StringComparer.Ordinal); }
        var observations = _services?.ObservationResult?.Snapshot.Installations;
        if (!ReferenceEquals(_resourceObservationSource, observations))
        { _resourceObservationSource = observations; _resourceObservations = (observations ?? []).ToDictionary(r => r.Id, StringComparer.Ordinal); }
    }
    [RelayCommand] private Task RegisterPackageBookAsync(BookViewModel? book) => GuardAsync(async ct =>
    {
        if (book is null) throw new ArgumentException("请先选择作品。");
        await RequireBookDetailsAsync(book, ct);
        Packages.Select(new(book.Title, book.Item, book.Metadata, book.GroupCompany));
        Section = 4;
        Status = "作品已选定。请在目录整理页的安装包登记中选择／核对文件，预览后确认。";
    });
    private void UpdateOwnership(BookViewModel book)
    {
        IndexResources();
        // A metadata-only result has no exact personal ItemId. Never borrow variant ownership.
        var records = book.Item is { } item ? _resourcePackages![item.Item.Id].ToArray() : [];
        var installations = book.Item is { } known ? _resourceInstallations![known.Item.Id].ToArray() : [];
        book.SetOwnership(installations.Any(i => i.Installation.Status == "present"),
            records.Where(p => p.Status == "present").Select(p => p.RelativePath).ToArray(),
            records.Select(p => p.RelativePath + " · " + (p.Status == "present" ? "存在" : p.Status == "missing" ? "缺失" : "未知／待核对")).ToArray());
        var ids = installations.Select(i => i.Installation.Id).ToHashSet(StringComparer.Ordinal);
        var components = installations.SelectMany(i => BookComponents(i, book)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var profiles = Profiles.Where(p => p.ComponentId is not null && components.Contains(p.ComponentId)).ToArray();
        book.SetResourceState(installations.Length > 0, installations.Any(i => i.Installation.Status == "missing") || records.Any(p => p.Status == "missing"),
            installations.Any(i => i.Installation.Status is not ("present" or "missing")) || records.Any(p => p.Status is not ("present" or "missing")), profiles.Any(p => ids.Contains(p.InstallationId ?? "")));
        var managed = (_services?.ObservationSettings.Roots ?? []).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        book.SetObservation(ids.Select(id => _resourceObservations.GetValueOrDefault(id)).OfType<LocalInstallationObservation>().Where(r => managed.Contains(r.RootId)).ToArray(), profiles);
    }
    private async Task PackagesSavedAsync(CancellationToken ct)
    {
        var services = Services(); var local = await services.LocalLibrary.ReadAsync(ct); services.Library.Seed(local);
        _packages = local.Packages ?? [];
        _books = _books.Concat(local.Items.Where(i => i.Item.DeletedAt is null && LocalInstallationRegistration.IsPending(i.Item.Id) && _books.All(b => b.Id != i.Item.Id)).Select(i => new BookViewModel(i, i.Metadata))).ToArray();
        foreach (var book in _books.Concat(_queryBooks)) UpdateOwnership(book);
        Shelf.SetLibrary(_books); UpdateBookDetails(); services.ActivityHost?.SignalSync();
    }
}
