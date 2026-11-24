using Sf6Prototype;

internal static class PlayerRefreshChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        const string account = "1234567890", opponent = "9999999999";
        var root = Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"));
        var store = new BattleStore(Path.Combine(root, "refresh.sqlite"));
        var home = new BattlePager(store, account);
        var away = new BattlePager(store, opponent);
        var history = new PlayerRefreshRequest(PlayerLoadIntent.Initial, RefreshPage.History, "", new(null, 20));
        RefreshPlan Plan(PlayerRefreshRequest request, BattlePager pager) => request.CreatePlan(pager, account, [], null);
        Check(Plan(history, home).Mode == RefreshMode.Account && Plan(history, away).Mode == RefreshMode.Initial,
            "The same history action binds to the final player's pager after sign-in");
        var ranked = history with { HistoryCategory = "rank" };
        Check(Plan(ranked, home).Mode == RefreshMode.Initial && Plan(ranked with { Intent = PlayerLoadIntent.More }, home).Mode == RefreshMode.More,
            "Opening and extending a category use its own history window");
        var scores = history with { Page = RefreshPage.Scores, Intent = PlayerLoadIntent.ScoreRange };
        Check(Plan(scores, home) is { Mode: RefreshMode.Scores, Category: "rank", ForceHead: false } &&
            Plan(scores, home).Range == scores.Range, "Requesting a score range preserves its bounds");
        var refresh = scores with { Intent = PlayerLoadIntent.Refresh };
        Check(Plan(refresh, home) is { Mode: RefreshMode.Account, ForceHead: true } &&
            Plan(refresh, away) is { Mode: RefreshMode.Scores, ForceHead: true },
            "Manual refresh on the score tab refreshes the account or the viewed opponent's scores");
        var favorites = history with { Page = RefreshPage.Favorites };
        Check((favorites with { Intent = PlayerLoadIntent.Refresh }).Route(home, []) == RefreshRoute.ReloadLocal &&
            (favorites with { Intent = PlayerLoadIntent.More }).Route(home, []) == RefreshRoute.Skip,
            "Favorites refresh locally and never request another page");
        var more = history with { Intent = PlayerLoadIntent.More };
        Check(more.Route(home, []) == RefreshRoute.Fetch, "An incomplete empty archive can request more history");
        store.CompleteCategory(account, "", null);
        var cached = new BattlePager(store, account);
        Check(more.Route(cached, []) == RefreshRoute.CachedPage && history.Route(cached, []) == RefreshRoute.Skip,
            "A complete local history is handled without a session");

        var target = new RefreshViewTarget(account, 1);
        var current = new RefreshViewTarget(opponent, 2);
        var prepared = 0;
        var published = 0;
        Task Prepare(string owner, CancellationToken token)
        {
            Check(owner == account, "Archive preparation always uses the operation's owner");
            prepared++;
            return Task.CompletedTask;
        }
        Task Deliver() => target.PublishPageAsync(() => current, Prepare, () => published++, CancellationToken.None);
        await Deliver();
        Check(prepared == 0 && published == 0, "A background account refresh leaves the opponent's page alone");
        current = target;
        await Deliver();
        Check(prepared == 1 && published == 1, "A committed page reaches the active visit");
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = target.PublishPageAsync(() => current, (_, _) => resume.Task, () => published++, CancellationToken.None);
        current = new(opponent, 2);
        current = new(account, 3);
        resume.SetResult();
        await pending;
        Check(published == 1, "A late callback cannot repaint a later visit to the same account");
        using var stop = new CancellationTokenSource();
        try
        {
            await target.PublishPageAsync(() => target, (_, _) => { stop.Cancel(); return Task.CompletedTask; },
                () => published++, stop.Token);
            throw new Exception("Expected cancellation during archive preparation");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Check(published == 1, "Stopping after preparation keeps the saved data without publishing stale UI state");
        return count;
    }
}
