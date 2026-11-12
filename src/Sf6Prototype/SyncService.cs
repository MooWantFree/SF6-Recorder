using System.IO;

namespace Sf6Prototype;

public record SyncProgress(string Category, int Page, int TotalPages, int Inserted, string Message);
public record SyncResult(int Inserted, int Pages, int Requests);
public sealed class SyncService(BattleStore store, IBattleSource source)
{
    public async Task<SyncResult> RunAsync(string owner, IProgress<SyncProgress>? progress, CancellationToken token, bool full = false, Func<int, Task>? pageSaved = null)
    {
        var run = store.BeginRun(owner); var inserted = 0; var pages = 0;
        try
        {
            foreach (var category in BattleCategory.All)
            {
                var frontier = full ? null : store.Frontier(owner, category.Key);
                string? head = null;
                var signatures = new HashSet<string>();
                var newer = new Dictionary<int,string>();
                for (var page = 1; ; page++)
                {
                    token.ThrowIfCancellationRequested();
                    var data = await source.FetchPageAsync(owner, category.Key, page, token); pages++;
                    if (data.CurrentPage != page) throw new InvalidDataException(Strings.DuplicatePageStopped);
                    if (page == 1) head = data.Battles.FirstOrDefault()?.ReplayId;
                    var signature = string.Join('|', data.Battles.Select(b => b.ReplayId));
                    if (data.Battles.Count > 0 && !signatures.Add(signature)) throw new InvalidDataException(Strings.DuplicateContentStopped);
                    // Commit the entire boundary page. Advance the checkpoint only when this category finishes.
                    var added = store.SavePage(data.Battles); inserted += added;
                    if (category.Key == "rank") store.SaveRankLinks(owner, data.Battles, newer);
                    // Await the page notification so the first page can be rendered before fetching more.
                    if (pageSaved != null && added > 0) await pageSaved(added);
                    var reached = frontier != null && data.Battles.Any(b => b.ReplayId == frontier);
                    progress?.Report(new(category.Name, page, data.TotalPages, inserted, reached ? Strings.SyncBoundaryReached : Strings.SavingRecords));
                    if (reached || data.Battles.Count == 0 || page >= data.TotalPages) break;
                }
                store.CompleteCategory(owner, category.Key, head);
            }
            store.FinishRun(run, "completed", source.Requests, inserted, $"{pages} pages");
            return new(inserted, pages, source.Requests);
        }
        catch (Exception ex)
        {
            store.FinishRun(run, ex is OperationCanceledException ? "cancelled" : "failed", source.Requests, inserted, ex.GetType().Name);
            throw;
        }
    }
}
