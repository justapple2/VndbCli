using CommunityToolkit.Mvvm.Input;
using Vndb.Local;

namespace Vndb.Desktop.ViewModels;

public partial class MainViewModel
{
    [RelayCommand] private void RegisterBook(BookViewModel? book)
    {
        if (book is null) { Status = "请先选择作品。"; return; }
        SelectedBook = book;
        Registration.Select(new(book.Title, book.Item, book.Metadata, book.GroupCompany));
        Section = 7;
    }
    [RelayCommand] private void RegisterCandidate()
    {
        if (Organization.SelectedCandidate is not { } candidate) { Status = "请先选择扫描候选。"; return; }
        Registration.RootPath = candidate.Path;
        if (SelectedBook is { } book) Registration.Select(new(book.Title, book.Item, book.Metadata, book.GroupCompany));
        if (_installations.SingleOrDefault(i => i.Installation.Id == candidate.InstallationId) is { } known)
        { Registration.ContainerTitle = known.Installation.Details.Title; Registration.Company = known.Installation.Details.CompanyGroup ?? ""; }
        Section = 7;
    }
    private async Task RegistrationSavedAsync(RegistrationResult result, CancellationToken ct)
    {
        var services = Services(); var local = await services.LocalLibrary.ReadAsync(ct); services.Library.Seed(local);
        _installations = local.Installations;
        _packages = local.Packages ?? [];
        var books = _books.ToList();
        foreach (var item in result.Items)
            if (books.All(b => b.Id != item.Item.Id)) books.Add(new(item, item.Metadata));
        _books = books.ToArray();
        foreach (var book in _books.Concat(_queryBooks)) UpdateOwnership(book);
        Shelf.SetLibrary(_books);
        SelectedBook = _books.FirstOrDefault(b => result.Items.Any(i => i.Item.Id == b.Id));
        Installations.Clear(); foreach (var installation in _installations.Where(i => SelectedBook is null || MatchesBook(i, SelectedBook))) Installations.Add(installation);
        SelectedInstallation = Installations.Single(i => i.Installation.Id == result.Installation.Installation.Id);
        services.ActivityHost?.SignalSync();
    }
}
