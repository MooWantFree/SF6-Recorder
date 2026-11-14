using System.Diagnostics;

namespace Sf6Prototype;

// Serialize official data queries. Independent static image requests bypass this gate.
public sealed class RequestGate
{
    public static RequestGate Shared { get; } = new(TimeSpan.FromSeconds(ConnectionSettings.DefaultApiIntervalSeconds));
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private long _lastStarted,_intervalTicks;
    public RequestGate(TimeSpan interval) => Interval=interval;
    public TimeSpan Interval
    {
        get=>TimeSpan.FromTicks(Interlocked.Read(ref _intervalTicks));
        set {if(value<TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));Interlocked.Exchange(ref _intervalTicks,value.Ticks);}
    }
    public TimeSpan RemainingDelay
    {
        get
        {
            var last=Interlocked.Read(ref _lastStarted);
            var remaining=last==0 ? TimeSpan.Zero : Interval-Stopwatch.GetElapsedTime(last);
            return remaining>TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public async Task<T> RunAsync<T>(Func<Task<T>> request, CancellationToken token)
    {
        await _mutex.WaitAsync(token);
        try
        {
            while(RemainingDelay is { } delay && delay>TimeSpan.Zero)
            {
                // Re-read saved settings while waiting, without blocking the UI or sending traffic.
                await Task.Delay(delay<TimeSpan.FromMilliseconds(200) ? delay : TimeSpan.FromMilliseconds(200),token);
            }
            token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _lastStarted,Stopwatch.GetTimestamp());
            return await request();
        }
        finally { _mutex.Release(); }
    }
}
