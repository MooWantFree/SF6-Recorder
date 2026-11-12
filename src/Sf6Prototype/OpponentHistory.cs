namespace Sf6Prototype;

public sealed record OpponentHistory(int Total, int Wins, int Losses, int Draws, int Unknown, IReadOnlyList<Battle> Matches)
{
    public string WinRate => Wins+Losses+Draws==0 ? "—" : $"{100d*Wins/(Wins+Losses+Draws):F1}%";
    public string Summary => $"{Total} 场 · {Wins} 胜 {Losses} 负" + (Draws>0 ? $" {Draws} 平" : "") + (Unknown>0 ? $" · {Unknown} 场待确认" : "");
    public static OpponentHistory From(IEnumerable<Battle> archive, string owner, string opponent)
    {
        // Names may change or collide; only stable IDs identify a head-to-head history.
        var games=string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(opponent) ? [] : archive
            .Where(b=>b.OwnerId==owner && b.OpponentId==opponent)
            .DistinctBy(b=>b.ReplayId).OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
        return new(games.Count,games.Count(b=>b.Outcome=="胜"),games.Count(b=>b.Outcome=="负"),
            games.Count(b=>b.Outcome=="平"),games.Count(b=>b.Outcome=="待确认"),
            games);
    }
}
