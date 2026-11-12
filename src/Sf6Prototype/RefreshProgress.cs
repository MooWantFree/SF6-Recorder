namespace Sf6Prototype;

public sealed record BattleLoadProgress(string Stage,double? Fraction,int Pages,int? RemainingPages,string Detail);
public enum RequestPhase { Waiting, Sending, Retrying, Completed }
public sealed record RequestActivity(RequestPhase Phase,double DurationSeconds=0,bool Success=false);

// Presentation state is separate from the transport. Percentages never advance on a timer.
public sealed class RefreshProgressState
{
    public string Stage { get; private set; }="准备刷新";
    public string Detail { get; private set; }="";
    public double? Fraction { get; private set; }
    public int? RemainingRequests { get; private set; }
    public RequestPhase Network { get; private set; }=RequestPhase.Completed;
    public double MeanResponseSeconds { get; private set; }
    private int _responses,_metadataDone;
    private bool _metadata;
    public void Begin(string stage="准备刷新")
    {Stage=stage;Detail="";Fraction=null;RemainingRequests=null;Network=RequestPhase.Completed;_metadata=false;_metadataDone=0;_responses=0;MeanResponseSeconds=0;}
    public void SetStage(string stage) {Stage=stage;}
    private void Advance(double value) => Fraction=Math.Clamp(Math.Max(Fraction ?? 0,value),0,1);
    public void Load(BattleLoadProgress progress,int metadataRequests)
    {
        Stage=progress.Stage;Detail=progress.Detail;
        if(progress.Fraction is { } fraction) Advance(.08+.82*Math.Clamp(fraction,0,1));
        RemainingRequests=progress.RemainingPages is { } pages ? Math.Max(0,pages)+metadataRequests : null;
    }
    public void Metadata()
    {Stage="更新角色资料";Detail="战绩已保存，头像将在后台更新";_metadata=true;_metadataDone=0;RemainingRequests=2;Advance(.92);}
    public void Observe(RequestActivity activity)
    {
        Network=activity.Phase;
        if(activity.Phase!=RequestPhase.Completed) return;
        MeanResponseSeconds=(MeanResponseSeconds*_responses+Math.Max(0,activity.DurationSeconds))/(++_responses);
        if(_metadata && activity.Success) {_metadataDone=Math.Min(2,_metadataDone+1);RemainingRequests=2-_metadataDone;Advance(.92+.035*_metadataDone);}
    }
    public void Finish(bool success,bool cancelled=false)
    {Stage=success ? "刷新完成" : cancelled ? "刷新已取消" : "刷新未完成";if(success) Fraction=1;RemainingRequests=0;Network=RequestPhase.Completed;}
    public string Tooltip(TimeSpan elapsed,TimeSpan interval,TimeSpan delay,TimeSpan activeRequestElapsed)
    {
        var lines=new List<string> {Fraction is { } fraction ? $"{Stage} · {(int)Math.Floor(fraction*100)}%" : Stage};if(Detail.Length>0) lines.Add(Detail);
        if(Network==RequestPhase.Waiting) lines.Add(delay>TimeSpan.Zero ? $"下次请求：{delay.TotalSeconds:F1} 秒后" : "等待发送请求");
        else if(Network==RequestPhase.Sending) lines.Add("等待官网响应");
        else if(Network==RequestPhase.Retrying) lines.Add("响应暂未完成，稍后重试");
        var duration=$"已用 {Duration(elapsed.TotalSeconds)}";
        var canEstimate=Network!=RequestPhase.Retrying && !(Network==RequestPhase.Sending && activeRequestElapsed.TotalSeconds>Math.Max(5,MeanResponseSeconds*2));
        if(canEstimate && RemainingRequests is { } count && count>0)
        {
            var response=Math.Max(.3,MeanResponseSeconds);var spacing=Math.Max(interval.TotalSeconds,response);
            var remaining=(count-1)*spacing+(Network==RequestPhase.Sending ? Math.Max(1,response-activeRequestElapsed.TotalSeconds) : delay.TotalSeconds+response);
            duration+=$" · 预计还需约 {Duration(remaining)}";
        }
        else if(RemainingRequests==null || !canEstimate) duration+=" · 剩余时间待确定";
        lines.Add(duration);
        if(canEstimate && RemainingRequests is >0) lines.Add("预计时间按当前间隔和已用响应时间估算");
        return string.Join('\n',lines);
    }
    private static string Duration(double seconds)
    {
        var total=(int)Math.Ceiling(Math.Max(0,seconds));
        return total<60 ? $"{total} 秒" : $"{total/60} 分 {total%60} 秒";
    }
}
