namespace Sf6Prototype;

public interface IRankSource
{
    Task<string> FetchPlayAsync(string owner, CancellationToken token);
    Task<string> FetchSeasonRanksAsync(string owner, int season, bool peakMr, CancellationToken token);
}

public readonly record struct RankQuery(int Season, bool PeakMr = false)
{
    public bool HighestLp => Season == -1 && !PeakMr;
    public void Validate()
    {
        if (Season < -1 || PeakMr && Season <= 0) throw new ArgumentException("无效的赛季筛选。");
    }
}

public sealed record RankQueryResult(IReadOnlyList<CharacterRank> Characters, DateTimeOffset FetchedAt);

// One instance per window. No query results are written to disk or shared across players.
public sealed class RankQuerySession(IRankSource source, string owner)
{
    private readonly Dictionary<RankQuery, RankQueryResult> _results = [];
    public OpponentProfile? Profile { get; private set; }
    public async Task<OpponentProfile> LoadProfileAsync(CancellationToken token, bool refresh = false)
    {
        token.ThrowIfCancellationRequested();
        if (Profile != null && !refresh) return Profile;
        var profile = RankProfiles.ParseProfile(await source.FetchPlayAsync(owner, token), owner);
        token.ThrowIfCancellationRequested();
        if (refresh) _results.Clear();
        Profile = profile;
        _results[new(profile.CurrentSeason)] = new(profile.Characters, DateTimeOffset.UtcNow);
        return profile;
    }
    public async Task<RankQueryResult> GetAsync(RankQuery query, CancellationToken token)
    {
        query.Validate(); token.ThrowIfCancellationRequested();
        if (_results.TryGetValue(query, out var cached)) return cached;
        var rows = RankProfiles.ParseSeason(await source.FetchSeasonRanksAsync(owner, query.Season, query.PeakMr, token));
        token.ThrowIfCancellationRequested();
        // The website's highest-LP view is not a lifetime peak-MR record.
        if (query.HighestLp) rows = rows.Select(r => r with { Mr = null, MasterLeague = null, MasterRatingRanking = null }).ToList();
        var result = new RankQueryResult(rows, DateTimeOffset.UtcNow);
        _results[query] = result;
        return result;
    }
    public void Clear() { _results.Clear(); Profile = null; }
}
