using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace Sf6Prototype;

// Serialize official data queries. Independent static image requests bypass this gate.
public sealed class RequestGate
{
    public static RequestGate Shared { get; } = new(TimeSpan.FromSeconds(ConnectionSettings.DefaultApiIntervalSeconds));
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private long _lastStarted,_intervalTicks;
    private long _cooldownUntilUtcTicks;
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
            var cooldownTicks = Interlocked.Read(ref _cooldownUntilUtcTicks) - DateTimeOffset.UtcNow.UtcTicks;
            return TimeSpan.FromTicks(Math.Max(0, Math.Max(remaining.Ticks, cooldownTicks)));
        }
    }
    internal Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken token) =>
        RunAsync(async () =>
        {
            var response = await send();
            // Publish the deadline while still holding the gate, before any queued sender can run.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var now = DateTimeOffset.UtcNow;
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - now);
                var cooldown = delay is { } specified && specified > TimeSpan.Zero ? specified : DefaultCooldown;
                var availableTicks = DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks;
                Interlocked.Exchange(ref _cooldownUntilUtcTicks, now.UtcTicks + Math.Min(cooldown.Ticks, availableTicks));
            }
            return response;
        }, token);

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
