namespace Sf6Prototype;

public sealed record BattleLoadProgress(string Stage,double? Fraction,int Pages,int? RemainingPages,string Detail);
public enum RequestPhase { Waiting, Sending, Retrying, Completed }
public sealed record RequestActivity(RequestPhase Phase,double DurationSeconds=0,bool Success=false);

// Presentation state is separate from the transport. Percentages never advance on a timer.
public sealed class RefreshProgressState
{
    public string Stage { get; private set; }=Strings.PreparingRefresh;
    public string Detail { get; private set; }="";
    public double? Fraction { get; private set; }
    public int? RemainingRequests { get; private set; }
    public RequestPhase Network { get; private set; }=RequestPhase.Completed;
    public double MeanResponseSeconds { get; private set; }
    private int _responses,_metadataDone,_metadataTotal=2;
    private bool _metadata;
    public void Begin(string? stage=null)
    {Stage=stage ?? Strings.PreparingRefresh;Detail="";Fraction=null;RemainingRequests=null;Network=RequestPhase.Completed;_metadata=false;_metadataDone=0;_responses=0;MeanResponseSeconds=0;}
    public void SetStage(string stage) {Stage=stage;}
    private void Advance(double value) => Fraction=Math.Clamp(Math.Max(Fraction ?? 0,value),0,1);
    public void Load(BattleLoadProgress progress,int metadataRequests)
    {
        Stage=progress.Stage;Detail=progress.Detail;
        if(progress.Fraction is { } fraction) Advance(.08+.82*Math.Clamp(fraction,0,1));
        RemainingRequests=progress.RemainingPages is { } pages ? Math.Max(0,pages)+metadataRequests : null;
    }
    public void Metadata(int requests=2)
    {Stage=Strings.UpdatingCharacterProfiles;Detail=Strings.RecordsSavedImagesBackground;_metadata=true;_metadataDone=0;_metadataTotal=Math.Max(1,requests);RemainingRequests=_metadataTotal;Advance(.92);}
    public void AddMetadataRequests(int count)
    {
        if(!_metadata || count<=0) return;
        _metadataTotal+=count;RemainingRequests=_metadataTotal-_metadataDone;
    }
    public void Observe(RequestActivity activity)
    {
        Network=activity.Phase;
        if(activity.Phase!=RequestPhase.Completed) return;
        MeanResponseSeconds=(MeanResponseSeconds*_responses+Math.Max(0,activity.DurationSeconds))/(++_responses);
        if(_metadata && activity.Success) {_metadataDone=Math.Min(_metadataTotal,_metadataDone+1);RemainingRequests=_metadataTotal-_metadataDone;Advance(.92+.07*_metadataDone/_metadataTotal);}
    }
    public void Finish(bool success,bool cancelled=false)
    {Stage=success ? Strings.RefreshComplete : cancelled ? Strings.RefreshCancelled : Strings.RefreshIncomplete;if(success) Fraction=1;RemainingRequests=0;Network=RequestPhase.Completed;}
    public string Tooltip(TimeSpan elapsed,TimeSpan interval,TimeSpan delay,TimeSpan activeRequestElapsed)
    {
        var lines=new List<string> {Fraction is { } fraction ? $"{Stage} · {(int)Math.Floor(fraction*100)}%" : Stage};if(Detail.Length>0) lines.Add(Detail);
        if(Network==RequestPhase.Waiting) lines.Add(delay>TimeSpan.Zero ? Localization.Format(Strings.NextRequestIn, delay.TotalSeconds) : Strings.WaitingToSend);
        else if(Network==RequestPhase.Sending) lines.Add(Strings.WaitingForResponse);
        else if(Network==RequestPhase.Retrying) lines.Add(Strings.ResponseRetrying);
        var duration=Localization.Format(Strings.ElapsedDuration, Duration(elapsed.TotalSeconds));
        var canEstimate=Network!=RequestPhase.Retrying && !(Network==RequestPhase.Sending && activeRequestElapsed.TotalSeconds>Math.Max(5,MeanResponseSeconds*2));
        if(canEstimate && RemainingRequests is { } count && count>0)
        {
            var response=Math.Max(.3,MeanResponseSeconds);var spacing=Math.Max(interval.TotalSeconds,response);
            var remaining=(count-1)*spacing+(Network==RequestPhase.Sending ? Math.Max(1,response-activeRequestElapsed.TotalSeconds) : delay.TotalSeconds+response);
            duration+=Localization.Format(Strings.RemainingEstimateSuffix, Duration(remaining));
        }
        else if(RemainingRequests==null || !canEstimate) duration+=Strings.RemainingUnknownSuffix;
        lines.Add(duration);
        if(canEstimate && RemainingRequests is >0) lines.Add(Strings.EstimateTimingHelp);
        return string.Join('\n',lines);
    }
    private static string Duration(double seconds)
    {
        var total=(int)Math.Ceiling(Math.Max(0,seconds));
        return total<60 ? Localization.Format(Strings.DurationSeconds, total) : Localization.Format(Strings.DurationMinutesSeconds, total/60, total%60);
    }
}
