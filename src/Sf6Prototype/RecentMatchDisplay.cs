namespace Sf6Prototype;

public sealed record RecentMatchDisplay(string Character, string Unit, string Highest, string Lowest, string Change,
    string ChangeBrush, string WinRate, IReadOnlyList<ChartPoint> Points, string EmptyText, bool IsEmpty)
{
    public RecentMatchSummary? Summary { get; private init; }
    public string CurrentScore { get; private init; } = "—";

    public RecentMatchDisplay WithLimit(int limit)
    {
        if (Summary == null || limit <= 0 || limit >= Summary.Matches.Count) return this;
        return From(RecentMatchScores.LastMatches(Summary,limit,Unit == "MR"),Character,Unit == "MR");
    }

    public static RecentMatchDisplay From(RecentMatchSummary summary,string character,bool mr)
    {
        var chart=new List<ChartPoint>();var previousSettled=false;
        if(summary.InitialScore is { } initial && summary.Samples.FirstOrDefault() is { } first)
        {
            chart.Add(new("$session-start",first.Point.Time[..16],initial,false,true,"neutral",Strings.SessionBaseline));
            previousSettled=true;
        }
        foreach(var sample in summary.Samples)
        {
            var p=sample.Point;var b=sample.Battle;var delta=mr ? p.MrDelta : p.LpDelta;
            var change=delta is { } value ? value.ToString("+0;−0;0") : Strings.SettlementUnknown;
            var estimate=delta!=null && p.Estimated ? "\n"+ScoreDisplay.EstimateExplanation : "";
            chart.Add(new(b.ReplayId,p.Time[..16],sample.Value,sample.Settled && previousSettled,sample.Settled,Kinds.Of(b.Outcome),
                $"{Localization.Outcome(b.Outcome)} {b.Score}  ·  {change}  ·  {b.Opponent}{estimate}"));
            previousSettled=sample.Settled;
        }
        var latest = summary.Samples.LastOrDefault();
        var hasLatest = latest != null && latest.Battle.ReplayId == summary.Matches.LastOrDefault()?.ReplayId;
        var rangeChange = summary.Change is { } net
            ? net.ToString("+0;−0;0") + (summary.Pending || summary.Estimated ? "*" : "") : "—";
        var emptyText = summary.Matches.Count == 0 ? Strings.NoRecentRankedMatches : mr ? Strings.NoMrForMatches : Strings.NoScores;
        return new(character,mr ? "MR" : "LP",summary.Highest?.ToString("N0") ?? "—",summary.Lowest?.ToString("N0") ?? "—",
            rangeChange,summary.Change > 0 ? "Win" : summary.Change < 0 ? "Loss" : "Ink",
            summary.WinRate is { } winRate ? $"{winRate:F1}%" : "—",chart,emptyText,summary.Matches.Count == 0) {
            Summary = summary, CurrentScore = hasLatest ? latest!.Value.ToString("N0") + (latest.Settled && !latest.Point.Estimated ? "" : "*") : "—" };
    }
}
