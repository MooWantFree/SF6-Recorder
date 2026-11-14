namespace Sf6Prototype;

public readonly record struct MatchFrameObservation(bool BattleHud, double ResultScore)
{
    public string? ResultPattern {get;init;}
}

// Try the game's localized result headers, independently of the recorder language,
// player side and outcome. A battle HUD gate prevents startup on a stale result.
public sealed class MatchEndDetection
{
    public const int FrameWidth=640, FrameHeight=64;
    public const double ResultThreshold=0.79;
    public const double StrongResultThreshold=0.94;
    public const string Version="result-header-v2";
    private readonly byte[] _ink=new byte[FrameWidth*FrameHeight];
    private static readonly double[] Scales=[0.90,0.95,1.0,1.05,1.10];
    private readonly record struct ProbePattern(string Name,(int Offset,byte Value)[] Points);
    private static readonly ProbePattern[] Patterns=CreatePatterns();
    private int _battleFrames,_resultFrames;
    private bool _armed;
    private DateTimeOffset? _lastBattle;
    public bool Armed=>_armed;
    public void Reset() { _battleFrames=_resultFrames=0;_armed=false;_lastBattle=null; }
    public void Suspend() { _battleFrames=_resultFrames=0; }
    public bool Observe(MatchFrameObservation observation,DateTimeOffset now)
    {
        if(_lastBattle is { } last && now-last>TimeSpan.FromMinutes(5)) Reset();
        if(observation.BattleHud && observation.ResultScore<0.6)
        {
            _lastBattle=now;_resultFrames=0;
            if(++_battleFrames>=3) _armed=true;
            return false;
        }
        _battleFrames=0;
        if(!_armed || observation.ResultScore<ResultThreshold) { _resultFrames=0;return false; }
        // A very strong header needs only one sample: a quick rematch can remove
        // it before the next sample. Colored result backgrounds can also satisfy
        // the coarse HUD heuristic, so let the stronger header evidence win.
        if(++_resultFrames<2 && observation.ResultScore<StrongResultThreshold) return false;
        Reset();return true;
    }
    public MatchFrameObservation Analyze(ReadOnlySpan<byte> bgra)
    {
        if(bgra.Length!=FrameWidth*FrameHeight*4) throw new ArgumentException("Unexpected frame size.",nameof(bgra));
        var timer=0;var left=0;var right=0;var resultInk=0;
        for(var y=0;y<FrameHeight;y++)
            for(var x=0;x<FrameWidth;x++)
            {
                var p=(y*FrameWidth+x)*4;var b=bgra[p];var g=bgra[p+1];var r=bgra[p+2];
                var neon=r>=140 && b>=130 && g>=65 && r-g>=18 && b-g>=12;
                _ink[y*FrameWidth+x]=(byte)(neon ? 1 : 0);
                if(neon && x<140 && y<52) resultInk++;
                if(neon && x is >=303 and <=337 && y is >=9 and <=45) timer++;
                // Health/drive UI occupies long, saturated strips on both sides.
                var colored=Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))>65 && Math.Max(r,Math.Max(g,b))>130;
                if(colored && y is >=18 and <=43)
                {
                    if(x is >=130 and <=291) left++;
                    if(x is >=349 and <=510) right++;
                }
            }
        var hud=timer>=35 && left>=140 && right>=140;
        if(resultInk<75) return new(hud,0);
        var best=0d;string? bestPattern=null;
        // The label moves vertically between cabinet/ranked result layouts.
        // Sparse positive/negative probes avoid OCR, models and full-frame searches.
        foreach(var pattern in Patterns)
            for(var y=3;y<=29;y++)
                for(var x=10;x<=24;x++)
                {
                    var correct=0;
                    var origin=y*FrameWidth+x;
                    foreach(var point in pattern.Points)
                    {
                        if(_ink[origin+point.Offset]==point.Value) correct++;
                    }
                    var score=(double)correct/pattern.Points.Length;
                    if(score>best) {best=score;bestPattern=pattern.Name;}
                }
        return new(hud,best) {ResultPattern=bestPattern};
    }
    private static ProbePattern[] CreatePatterns() =>
        CreatePatterns("result_latin",MatchEndPattern.Rows)
            .Concat(CreatePatterns("result_zh_hans",MatchEndPattern.ChineseRows)).ToArray();
    private static IEnumerable<ProbePattern> CreatePatterns(string name,string source)
    {
        var rows=source.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var positive=new List<(int,int,bool)>();var negative=new List<(int,int,bool)>();
        for(var y=0;y<rows.Length;y++)
            for(var x=0;x<rows[y].Length;x++)
            {
                if(rows[y][x]=='1') positive.Add((x,y,true));
                else if(rows[y][x]=='0') negative.Add((x,y,false));
            }
        static IEnumerable<(int,int,bool)> Sample(List<(int,int,bool)> values) =>
            Enumerable.Range(0,Math.Min(144,values.Count)).Select(i=>values[i*values.Count/Math.Min(144,values.Count)]);
        var points=Sample(positive).Concat(Sample(negative)).ToArray();
        return Scales.Select(scale=>new ProbePattern(name,points.Select(point=>
            ((int)Math.Round(point.Item2*scale)*FrameWidth+(int)Math.Round(point.Item1*scale),(byte)(point.Item3 ? 1 : 0))).ToArray()));
    }
}

// One pending refresh per signed-in account, with a bounded settlement retry.
public sealed class MatchEndRefreshSchedule
{
    public string? Owner {get;private set;}
    public DateTimeOffset? DueAt {get;private set;}
    public DateTimeOffset? ExpiresAt {get;private set;}
    public int Attempts {get;private set;}
    public int Version {get;private set;}
    private int _retrySeconds;
    public void Queue(string owner,DateTimeOffset now,int waitSeconds=5,int retrySeconds=8)
    {
        if(waitSeconds is <0 or >3600 || retrySeconds is <1 or >3600) throw new ArgumentOutOfRangeException(nameof(waitSeconds));
        Owner=owner;DueAt=now.AddSeconds(waitSeconds);
        ExpiresAt=now.AddSeconds(waitSeconds+retrySeconds+120);Attempts=0;_retrySeconds=retrySeconds;Version++;
    }
    public void Clear() {Owner=null;DueAt=ExpiresAt=null;Attempts=0;Version++;}
    public bool IsDue(string? owner,DateTimeOffset now)
    {
        if(Owner==null) return false;
        // A refresh already in progress may advance its old snapshot's frontier
        // without including the match that caused this newer detection.
        if(owner!=Owner || now>ExpiresAt) {Clear();return false;}
        return DueAt is { } due && now>=due;
    }
    public int Begin() {Attempts++;DueAt=null;return Version;}
    public bool Complete(int version,bool success,bool newMatches,DateTimeOffset now)
    {
        // Only the task captured at refresh start can be completed. Queue merges
        // subsequent detections into one newer task with its own wait and retry.
        if(Owner==null || version!=Version) return false;
        if(!success || newMatches || Attempts>=2 || now>ExpiresAt) Clear();
        else DueAt=now.AddSeconds(_retrySeconds);
        return true;
    }
}
