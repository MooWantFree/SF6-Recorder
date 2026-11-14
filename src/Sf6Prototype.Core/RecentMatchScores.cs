namespace Sf6Prototype;

public sealed record RecentScoreSample(Battle Battle,ScorePoint Point,int Value,bool Settled);
public sealed record RecentMatchSummary(Battle? Latest,IReadOnlyList<Battle> Matches,IReadOnlyList<RecentScoreSample> Samples,
    int? InitialScore,int? Highest,int? Lowest,int? Change,bool Pending,bool Estimated,double? WinRate);

public static class RecentMatchScores
{
    public static RecentMatchSummary Build(IEnumerable<Battle> records,IReadOnlyDictionary<string,ScorePoint> points,
        string owner,DateTimeOffset startedAt,bool showMr)
    {
        // Follow the newest refreshed match's character, including non-ranked modes.
        Battle? latest=null;var session=new List<Battle>();var boundary=startedAt.ToUnixTimeSeconds();
        foreach(var battle in records)
        {
            if(battle.OwnerId!=owner) continue;
            if(latest==null || battle.PlayedAt>latest.PlayedAt || battle.PlayedAt==latest.PlayedAt &&
                StringComparer.Ordinal.Compare(battle.ReplayId,latest.ReplayId)<0) latest=battle;
            if(battle.Category=="rank" && battle.PlayedAt>=boundary) session.Add(battle);
        }
        var matches=session.Where(b=>b.CharacterId==latest?.CharacterId)
            .OrderBy(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToArray();
        var samples=new List<RecentScoreSample>();var deltas=new List<int>();
        foreach(var battle in matches)
        {
            if(!points.TryGetValue(battle.ReplayId,out var point)) continue;
            var after=showMr ? point.MrAfter : point.LpAfter;
            var value=after ?? (showMr ? point.Mr is >0 ? point.Mr : null : point.Lp);
            if(value==null || value<0) continue;
            samples.Add(new(battle,point,value.Value,after!=null));
            if((showMr ? point.MrDelta : point.LpDelta) is { } delta) deltas.Add(delta);
        }
        int? initial=samples.Count==0 ? null : showMr ? samples[0].Point.Mr : samples[0].Point.Lp;
        var values=samples.Select(s=>s.Value).ToList();if(initial is >=0)values.Add(initial.Value);
        var decided=matches.Count(b=>b.Outcome is "胜" or "负" or "平");
        var crossesSeason=showMr && samples.Select(s=>s.Battle.Season).Distinct().Count()>1;
        return new(latest,matches,samples,initial,values.Count==0 ? null : values.Max(),values.Count==0 ? null : values.Min(),
            deltas.Count==0 || crossesSeason ? null : deltas.Sum(),deltas.Count<matches.Length || crossesSeason,
            samples.Any(s=>s.Point.Estimated),decided==0 ? null : 100d*matches.Count(b=>b.Outcome=="胜")/decided);
    }
}
