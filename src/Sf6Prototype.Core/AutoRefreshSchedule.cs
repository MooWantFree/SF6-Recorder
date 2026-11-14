namespace Sf6Prototype;

// A single countdown, without queued/catch-up runs after sleep or a busy download.
public sealed class AutoRefreshSchedule
{
    public int Seconds { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public bool Paused { get; private set; }
    public void Configure(int seconds,DateTimeOffset now)
    {
        if(seconds<0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Seconds=seconds; Resume(now);
    }
    public void Restart(DateTimeOffset now) => DueAt=Seconds>0 ? now.AddSeconds(Seconds) : null;
    public void Pause() => Paused=true;
    public void Resume(DateTimeOffset now) { Paused=false; Restart(now); }
    public bool IsDue(DateTimeOffset now) => !Paused && DueAt is { } due && now>=due;
    public bool TryBegin(DateTimeOffset now,bool available)
    {
        if(!available || !IsDue(now)) return false;
        Restart(now); return true;
    }
}
