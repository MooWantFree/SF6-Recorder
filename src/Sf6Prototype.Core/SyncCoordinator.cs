namespace Sf6Prototype;

public enum SyncOperation { ManualRefresh, AutomaticRefresh, LoadHistory, Login, PlayerLookup, CharacterNames }
public enum SyncOutcome { Incomplete, Completed, Cancelled, Failed }
public sealed record SyncRequest(SyncOperation Operation, bool Quiet = false, bool Background = false, bool SilentCancellation = false)
{
    public bool ShowsCompletion => Operation is SyncOperation.ManualRefresh or SyncOperation.AutomaticRefresh or SyncOperation.LoadHistory;
}
public sealed record SyncCompletion(SyncRequest Request, SyncOutcome Outcome);
public sealed record SyncProgressUpdate(BattleLoadProgress Progress, int MetadataRequests);

public sealed class SyncContext
{
    private readonly Action<SyncProgressUpdate> _report;
    private readonly Action<RequestActivity> _request;
    internal SyncContext(CancellationToken token, bool silent, Action<SyncProgressUpdate> report, Action<RequestActivity> request)
    { Token = token; SilentCancellation = silent; _report = report; _request = request; }
    public CancellationToken Token { get; }
    public bool SilentCancellation { get; internal set; }
    public bool Completed { get; private set; }
    public Exception? Error { get; private set; }
    public void Complete() => Completed = true;
    public void Fail(Exception error) => Error = error;
    public void Report(BattleLoadProgress progress, int metadataRequests = 0) => _report(new(progress, metadataRequests));
    public void Report(RequestActivity activity) => _request(activity);
}

// Called from the UI dispatcher. Owns the single foreground operation, including work
// that completes synchronously. The completion task is installed before invoking callers.
public sealed class SyncCoordinator
{
    private CancellationTokenSource? _cancellation;
    public SyncContext? Active { get; private set; }
    public bool IsBusy => Active != null;
    public Task PendingWork { get; private set; } = Task.CompletedTask;
    public event Action<SyncRequest>? Started;
    public event Action<SyncCompletion>? Finished;
    public event Action<SyncProgressUpdate>? ProgressChanged;
    public event Action<RequestActivity>? RequestChanged;

    public Task RunAsync(SyncRequest request, Func<SyncContext, Task> run) =>
        RunAsync(request, async context => { await run(context); return true; });

    public Task<T> RunAsync<T>(SyncRequest request, Func<SyncContext, Task<T>> run)
    {
        if (IsBusy) throw new InvalidOperationException("A foreground operation is already running.");
        var cancellation = new CancellationTokenSource();
        var context = new SyncContext(cancellation.Token, request.SilentCancellation, update =>
        {
            if (ReferenceEquals(_cancellation, cancellation) && !cancellation.IsCancellationRequested)
                ProgressChanged?.Invoke(update);
        }, activity =>
        {
            if (ReferenceEquals(_cancellation, cancellation) && !cancellation.IsCancellationRequested)
                RequestChanged?.Invoke(activity);
        });
        var completion = new TaskCompletionSource<T>();
        _cancellation = cancellation; Active = context; PendingWork = completion.Task;
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            using var diagnostic = Diagnostics.Begin(request.Operation.ToString());
            T result = default!; Exception? failure = null;
            try { Started?.Invoke(request); result = await run(context); }
            catch (Exception ex) { failure = ex; context.Fail(ex); }
            var outcome = context.Token.IsCancellationRequested ? SyncOutcome.Cancelled :
                context.Error != null ? SyncOutcome.Failed : context.Completed ? SyncOutcome.Completed : SyncOutcome.Incomplete;
            Diagnostics.Write(request.Operation.ToString(), outcome.ToString(), context.Error);
            Active = null; _cancellation = null; cancellation.Dispose();
            try { Finished?.Invoke(new(request, outcome)); }
            catch (Exception ex) { failure ??= ex; Diagnostics.Write("sync", "completion_callback", ex); }
            if (failure is OperationCanceledException && context.Token.IsCancellationRequested)
                completion.TrySetCanceled(context.Token);
            else if (failure != null) completion.TrySetException(failure);
            else completion.TrySetResult(result);
        }
    }
    public void Cancel(bool silent = true)
    {
        if (Active != null) Active.SilentCancellation = silent;
        _cancellation?.Cancel();
    }
    public async Task CancelAndDrainAsync(Func<Task> drain, bool silent = true)
    {
        var work = PendingWork;
        Cancel(silent);
        try { await work; }
        finally { await drain(); }
    }
}
