namespace Sf6Prototype;

/// <summary>One quiet update queue shared by every player and request surface, owned by the UI dispatcher.</summary>
public sealed class CharacterNameUpdater(CharacterNames names,
    Func<(ICharacterNameSource Source,string Owner)?> connect,Func<CancellationToken,Task> waitUntilIdle,
    Action changed,Func<DateTimeOffset>? now=null)
{
    private readonly HashSet<int> _pending=[];
    private readonly Dictionary<int,DateTimeOffset> _retryAfter=[];
    private IReadOnlyList<(int Id,string Name,string ToolName)>? _roster;
    private CancellationTokenSource _cancellation=new();
    private bool _cancelling;
    public Task PendingWork {get;private set;}=Task.CompletedTask;
    public void Observe(CharacterObservation observation)
    {
        if(_cancelling) return;
        if(observation.Roster!=null) _roster=observation.Roster;
        var at=now?.Invoke() ?? DateTimeOffset.UtcNow;
        foreach(var id in observation.Ids.Where(id=>id>0 && !names.IsComplete(id)))
            if(!_retryAfter.TryGetValue(id,out var retry) || retry<=at) _pending.Add(id);
        if(_pending.Count>0 && PendingWork.IsCompleted) PendingWork=RunAsync(_cancellation.Token);
    }
    private async Task RunAsync(CancellationToken token)
    {
        // Let the triggering request return and its rows render before opening another client.
        await Task.Yield();
        try
        {
            while(_pending.Count>0)
            {
                await waitUntilIdle(token);token.ThrowIfCancellationRequested();
                var ids=_pending.Where(id=>!names.IsComplete(id)).ToArray();_pending.Clear();
                if(ids.Length==0) continue;
                var at=now?.Invoke() ?? DateTimeOffset.UtcNow;
                foreach(var id in ids) _retryAfter[id]=at.AddMinutes(5);
                var revision=names.Revision;
                try
                {
                    var connection=connect();
                    if(connection==null) continue;
                    try {await names.EnsureObservedAsync(connection.Value.Source,connection.Value.Owner,new(ids,_roster),token);}
                    finally {(connection.Value.Source as IDisposable)?.Dispose();}
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested)
                {
                    foreach(var id in ids) _retryAfter.Remove(id);
                    throw;
                }
                catch(Exception ex) when(ex is not OutOfMemoryException)
                { /* Preserve existing names and retry on a later observation after the cooldown. */ }
                finally {if(names.Revision!=revision && !token.IsCancellationRequested) changed();}
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
    }
    public async Task CancelAsync()
    {
        if(_cancelling) {await PendingWork;return;}
        _cancelling=true;_cancellation.Cancel();
        try {await PendingWork;}
        finally
        {
            _pending.Clear();_roster=null;
            _cancellation.Dispose();_cancellation=new();_cancelling=false;
        }
    }
}
