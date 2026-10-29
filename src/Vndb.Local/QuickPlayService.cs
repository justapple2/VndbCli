using Vndb.Contracts;
using Vndb.Core;

namespace Vndb.Local;

public sealed record QuickPlayBook(string Id, string Title, LibraryItemView? Item, IReadOnlyList<PersonalPlayView> Plays, bool NeedsReview = false);

public interface IQuickPlayService
{
    Task<IReadOnlyList<PersonalPlayView>> LoadAsync(QuickPlayBook book, CancellationToken ct);
    Task<PersonalEditDraft?> PendingAsync(QuickPlayBook book, CancellationToken ct);
    Task<PersonalEditDraft> QueueAsync(QuickPlayBook book, PersonalPlayView? play, PersonalPlayData details, CancellationToken ct);
    Task<PersonalPlayView> SyncAsync(PersonalEditDraft draft, CancellationToken ct);
    Task WithdrawConflictAsync(PersonalEditDraft draft, CancellationToken ct);
}
public sealed class QuickPlayService(PersonalEditOutbox edits, Func<string?> server, Func<ISyncApi?> remote,
    Func<string, CancellationToken, Task<IReadOnlyList<PersonalPlayView>>> readPlays) : IQuickPlayService
{
    private string Origin => server() ?? throw new ArgumentException("请先配置Web地址；暂时不可达也可以保存离线编辑。");
    public async Task<IReadOnlyList<PersonalPlayView>> LoadAsync(QuickPlayBook book, CancellationToken ct)
    {
        if (book.Item is null) return book.Plays;
        if (LocalInstallationRegistration.IsPending(book.Item.Item.Id)) throw new ArgumentException("此作品的本机登记尚待同步，请先同步登记；离线计时及感想仍可在游玩会话中保存。");
        try
        {
            if (remote() is { } web)
            {
                var current = await web.ItemAsync(book.Item.Item.Id, ct);
                if (current.Item.Id != book.Item.Item.Id || current.Item.DeletedAt is not null || current.Item.Details.NovelId != book.Item.Item.Details.NovelId) throw new ArgumentException("书架作品关联已改变，请刷新书架后再编辑。");
            }
            return await readPlays(book.Item.Item.Id, ct);
        }
        catch (ClientApiException e) when (e.Status == 404) { throw new ArgumentException("此条目尚未收录到Web书架，请先确认作品关联。"); }
        catch (WebUnavailableException)
        {
            if (book.NeedsReview && book.Plays.Count == 0) throw new ArgumentException("个人记录关联待核对，请联网读取此条目的记录后再编辑。");
            return book.Plays;
        }
    }
    public async Task<PersonalEditDraft?> PendingAsync(QuickPlayBook book, CancellationToken ct) => server() is null ? null :
        (await edits.PendingAsync(Origin, ct)).FirstOrDefault(d => d.BookKey == book.Id);
    public Task<PersonalEditDraft> QueueAsync(QuickPlayBook book, PersonalPlayView? play, PersonalPlayData details, CancellationToken ct)
    {
        if (book.Item is { } item && LocalInstallationRegistration.IsPending(item.Item.Id))
            throw new ArgumentException("此作品的本机登记尚待同步，请先同步登记；离线计时及感想仍可在游玩会话中保存。");
        ShelfPlayResolver.RequireSelection(book.Item?.Item.Id, play);
        return edits.EnqueueAsync(book.Id, Origin, play?.Id, new(details, book.Item?.Item.Id, play?.Revision), ct);
    }
    public Task<PersonalPlayView> SyncAsync(PersonalEditDraft draft, CancellationToken ct) => edits.SyncOneAsync(
        remote() ?? throw new WebUnavailableException(), Origin, draft, ct);
    public Task WithdrawConflictAsync(PersonalEditDraft draft, CancellationToken ct) => edits.WithdrawConflictAsync(draft, ct);
}
