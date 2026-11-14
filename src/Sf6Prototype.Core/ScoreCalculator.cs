namespace Sf6Prototype;

public static class ScoreCalculator
{
    public static List<ScorePoint> Calculate(IEnumerable<Battle> rows, CurrentScores? current = null,
        bool settlementsComplete = true, IReadOnlyDictionary<string,string>? links = null)
    {
        if(!settlementsComplete) return rows.Where(b=>b.Category=="rank").OrderBy(b=>b.PlayedAt).ThenBy(b=>b.ReplayId)
            .Select(b=>new ScorePoint(b.PlayedAt,b.Time,b.Lp,b.Mr,null,null,b.ReplayId)).ToList();
        var result=new List<ScorePoint>();
        foreach(var group in rows.Where(b=>b.Category=="rank").GroupBy(b=>b.CharacterId))
        {
            var ordered=group.OrderBy(b=>b.PlayedAt).ThenBy(b=>b.ReplayId).ToList();
            for(var i=0;i<ordered.Count;i++)
            {
                var b=ordered[i];var next=i+1<ordered.Count ? ordered[i+1] : null;
                var linked=next!=null && (links==null || links.TryGetValue(b.ReplayId,out var id) && id==next.ReplayId);
                result.Add(Point(b,next,linked,current));
            }
        }
        return result.OrderBy(p=>p.PlayedAt).ThenBy(p=>p.ReplayId).ToList();
    }
    internal static ScorePoint Point(Battle b,Battle? next,bool linked,CurrentScores? current)
    {
        if(next!=null) return new(b.PlayedAt,b.Time,b.Lp,b.Mr,linked ? LpDelta(b.Lp,next.Lp) : null,
            linked ? MrDelta(b,next.Mr,next.Season) : null,b.ReplayId);
        var now=current!=null && current.FetchedAt.ToUnixTimeSeconds()>b.PlayedAt
            ? current.Characters.FirstOrDefault(c=>c.CharacterId==b.CharacterId && c.Lp>=0) : null;
        int? lp=Plausible(b,LpDelta(b.Lp,now?.Lp)),mr=Plausible(b,now==null ? null : MrDelta(b,now.Mr,current!.Season));
        return new(b.PlayedAt,b.Time,b.Lp,b.Mr,lp,mr,b.ReplayId,lp!=null || mr!=null);
    }
    private static int? LpDelta(int? before,int? after) => before>=0 && after>=0 ? after-before : null;
    private static int? MrDelta(Battle b, int? nextMr, int? nextSeason) =>
        b.Mr > 0 && nextMr > 0 && b.Season != null && b.Season == nextSeason ? nextMr - b.Mr : null;
    // Rejects an estimate that disagrees with the result, e.g. when a newer match is not yet listed.
    private static int? Plausible(Battle b, int? delta) =>
        delta == null || b.Outcome switch { "胜" => delta > 0, "负" => delta <= 0, _ => true } ? delta : null;
}
