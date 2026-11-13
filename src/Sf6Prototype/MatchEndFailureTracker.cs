namespace Sf6Prototype;

public sealed record MatchEndFailure(DateTimeOffset At,long Sample,MatchFrameObservation Observation,bool Armed,
    double Frequency,string Reason,byte[] Pixels);

// Retain one small input frame in memory while a result looks plausible, or while
// an armed battle loses its HUD. The latter preserves unknown localized headers.
// Emit an image request if the candidate disappears or stays unconfirmed; a
// confirmed end cancels it. No per-frame encoding, file writes or image queue.
public sealed class MatchEndFailureTracker
{
    public const double SuspectThreshold=0.65;
    private byte[]? _pixels;
    private bool _pending,_saved,_armed,_headerCandidate,_confirmedEnd;
    private DateTimeOffset _started,_at,_lastAt;
    private long _sample;
    private MatchFrameObservation _observation;
    private double _frequency;
    public MatchEndFailure? Observe(DateTimeOffset at,long sample,MatchFrameObservation observation,bool armed,bool ended,
        double frequency,ReadOnlySpan<byte> pixels)
    {
        if(ended) {Reset();_confirmedEnd=true;return null;}
        // The confirmed result remains on screen after the detector disarms.
        // Do not label its following samples as failures, including after alt-tab.
        // A newly armed battle starts a new opportunity for diagnostic evidence.
        if(_confirmedEnd)
        {
            if(!armed) return null;
            _confirmedEnd=false;
        }
        if((observation.BattleHud || !armed) && observation.ResultScore<SuspectThreshold)
            return Interrupt("candidate_lost");
        if(!_pending)
        {_pending=true;_saved=false;_headerCandidate=false;_started=at;_observation=default;}
        _lastAt=at;_headerCandidate|=observation.ResultScore>=SuspectThreshold;
        if(!_saved && (_pixels==null || observation.ResultScore>=_observation.ResultScore))
        {
            if(pixels.Length!=MatchEndDetection.FrameWidth*MatchEndDetection.FrameHeight*4)
                throw new ArgumentException("Unexpected frame size.",nameof(pixels));
            _pixels??=new byte[pixels.Length];pixels.CopyTo(_pixels);
            _at=at;_sample=sample;_observation=observation;_armed=armed;_frequency=frequency;
        }
        if(!_saved && at-_started>=TimeSpan.FromSeconds(Math.Max(2,2.5/frequency)))
        {_saved=true;return Snapshot(_headerCandidate ? "confirmation_timeout" : "hud_missing_timeout");}
        return null;
    }
    public MatchEndFailure? Interrupt(string reason)
    {
        // Ignore brief HUD flicker (a hit effect, for example). A longer loss of
        // the HUD after a battle is worth one diagnostic image, even at score 0.
        var failure=_pending && !_saved && (_headerCandidate || _lastAt-_started>=TimeSpan.FromSeconds(1))
            ? Snapshot(_headerCandidate ? reason : "hud_missing_"+reason) : null;
        Reset();return failure;
    }
    private MatchEndFailure Snapshot(string reason)=>new(_at,_sample,_observation,_armed,_frequency,reason,_pixels!);
    private void Reset() {_pending=false;_saved=false;}
}
