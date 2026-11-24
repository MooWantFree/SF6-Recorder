namespace Sf6Prototype;

public enum PlayerLoadIntent { Initial, Refresh, More, ScoreRange }
public enum RefreshPage { History, Favorites, Scores }
public enum RefreshRoute { Skip, ReloadLocal, CachedPage, Fetch }

public sealed record PlayerRefreshRequest(PlayerLoadIntent Intent, RefreshPage Page, string HistoryCategory, ScoreRange Range)
{
    public bool Reset => Intent == PlayerLoadIntent.Refresh;
    public string Category => Page == RefreshPage.Scores ? "rank" : HistoryCategory;

    public RefreshRoute Route(BattlePager pager, IReadOnlyList<Battle> filtered)
    {
        if (Page == RefreshPage.Favorites) return Reset ? RefreshRoute.ReloadLocal : RefreshRoute.Skip;
        return Intent switch
        {
            PlayerLoadIntent.ScoreRange when Page != RefreshPage.Scores || !pager.NeedsScoreRange(Range) => RefreshRoute.Skip,
            PlayerLoadIntent.Initial when !pager.NeedsInitial(Category) => RefreshRoute.Skip,
            PlayerLoadIntent.More when !pager.MoreNeedsNetwork(filtered, Category) => RefreshRoute.CachedPage,
            _ => RefreshRoute.Fetch
        };
    }

    // The pager belongs to the player selected after sign-in and account validation.
    public RefreshPlan CreatePlan(BattlePager pager, string signedInId, IReadOnlyList<Battle> filtered, DailyProfileRefresh? profile)
    {
        var ownArchive = pager.Owner == signedInId;
        var mode = (Intent, Page) switch
        {
            (PlayerLoadIntent.Refresh, _) when ownArchive => RefreshMode.Account,
            (PlayerLoadIntent.Initial, RefreshPage.History) when ownArchive && Category.Length == 0 => RefreshMode.Account,
            (_, RefreshPage.Scores) => RefreshMode.Scores,
            (PlayerLoadIntent.More, _) => RefreshMode.More,
            _ => RefreshMode.Initial
        };
        return new RefreshPlan(pager, mode, Category, Range, Reset, filtered, profile);
    }
}

// A player can be visited twice during one operation. Only the originating visit
// may publish into the displayed archive, even when the player ID matches again.
public readonly record struct RefreshViewTarget(string Owner, int NavigationVersion)
{
    public bool CanPublish(RefreshViewTarget? current, CancellationToken token) =>
        !token.IsCancellationRequested && this == current;

    public async Task PublishPageAsync(Func<RefreshViewTarget?> current, Func<string, CancellationToken, Task> prepare,
        Action publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanPublish(current(), token)) return;
        await prepare(Owner, token);
        token.ThrowIfCancellationRequested();
        if (CanPublish(current(), token)) publish();
    }
}
