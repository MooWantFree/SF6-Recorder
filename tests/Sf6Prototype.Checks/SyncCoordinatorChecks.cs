using Sf6Prototype;

public static class SyncCoordinatorChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var sync = new SyncCoordinator();
        SyncContext? oldContext = null; var progress = 0; var requests = 0;
        sync.ProgressChanged += _ => progress++;
        sync.RequestChanged += _ => requests++;
        var outcomes = new List<SyncOutcome>();
        sync.Started += _ => Check(sync.IsBusy && !sync.PendingWork.IsCompleted, "Task ownership is established before start notifications");
        sync.Finished += result => { Check(!sync.IsBusy, "Ownership is released before completion notifications"); outcomes.Add(result.Outcome); };
        var synchronous = sync.RunAsync(new(SyncOperation.ManualRefresh), context =>
        {
            oldContext=context;context.Report(new BattleLoadProgress("page",0,0,1,""));context.Report(new RequestActivity(RequestPhase.Sending));
            context.Complete();return Task.FromResult(42);
        });
        Check(await synchronous == 42 && sync.PendingWork == synchronous && outcomes.Last() == SyncOutcome.Completed,
            "Synchronous completion retains the correct task and result");
        oldContext!.Report(new BattleLoadProgress("late",1,1,0,""));oldContext.Report(new RequestActivity(RequestPhase.Completed));
        Check(progress==1 && requests==1,"Late page and request callbacks cannot change a finished operation's progress");
        var held = new TaskCompletionSource(); var entered = new TaskCompletionSource();
        var token = CancellationToken.None;
        var pending = sync.RunAsync(new(SyncOperation.AutomaticRefresh, SilentCancellation: true), async context =>
        {
            token = context.Token; entered.SetResult(); await held.Task;
            Check(!context.SilentCancellation, "An explicit stop overrides quiet cancellation before work resumes");
            context.Token.ThrowIfCancellationRequested();
        });
        await entered.Task;
        try { _ = sync.RunAsync(new(SyncOperation.Login), _ => Task.CompletedTask); throw new Exception("Expected exclusion"); }
        catch (InvalidOperationException) { count++; }
        var drained = false;
        var stop = sync.CancelAndDrainAsync(() => { drained = true; return Task.CompletedTask; }, silent: false);
        Check(token.IsCancellationRequested && !stop.IsCompleted && !drained && sync.IsBusy,
            "Stopping requests cancellation and waits for the operation before draining dependent writers");
        held.SetResult();
        try { await stop; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { count++; }
        Check(drained && outcomes.Last() == SyncOutcome.Cancelled && pending.IsCanceled, "Cancellation releases ownership and drains dependent work");
        var failed = sync.RunAsync(new(SyncOperation.LoadHistory), _ => Task.FromException(new IOException("synthetic")));
        drained = false;
        try { await sync.CancelAndDrainAsync(() => { drained = true; return Task.CompletedTask; }); throw new Exception("Expected failure"); }
        catch (IOException) { count++; }
        Check(drained && failed.IsFaulted && outcomes.Last() == SyncOutcome.Failed, "A failed operation still drains writers and remains observable");
        await sync.RunAsync(new(SyncOperation.Login), context => { context.Complete(); return Task.CompletedTask; });
        Check(!sync.IsBusy && outcomes.Last() == SyncOutcome.Completed, "A new operation succeeds after failure");
        return count;
    }
}
