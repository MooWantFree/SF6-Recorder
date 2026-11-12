using System.Text.Json;
using System.IO;

namespace Sf6Prototype;

public static class ScoreDisplay
{
    public const string NewChallenger = "New Challenger";
    public static string EstimateExplanation => Strings.ScoreEstimateExplanation;
    public static string Value(int? score) => score<0 ? NewChallenger : score?.ToString("N0") ?? "—";
    public static string WithUnit(int? score,string unit) => score<0 ? NewChallenger : $"{Value(score)} {unit}";
}

public record BattleCategory(string Key, string NameKey)
{
    public string Name => Localization.Get(NameKey);
    public string ShortName => Key switch { "rank" => Strings.RankedShort, "casual" => Strings.CasualShort, "custom" => Strings.CustomShort, "hub" => Strings.HubShort, _ => Strings.All };
    public static readonly BattleCategory[] All = [new("rank", nameof(Strings.RankedMatch)), new("casual", nameof(Strings.CasualMatch)), new("custom", nameof(Strings.CustomRoom)), new("hub", nameof(Strings.BattleHub))];
    public override string ToString() => Name;
}

public record Battle
{
    public string OwnerId { get; init; } = "";
    public string ReplayId { get; init; } = "";
    public string Category { get; init; } = "";
    public long PlayedAt { get; init; }
    public string PlayerName { get; init; } = "";
    public int CharacterId { get; init; }
    public string Character { get; init; } = "";
    public string OpponentId { get; init; } = "";
    public string Opponent { get; init; } = "";
    public string OpponentCharacter { get; init; } = "";
    public int? OpponentCharacterId { get; init; }
    public int? Lp { get; init; }
    public int? Mr { get; init; }
    public int? LeagueRank { get; init; }
    public int? OpponentLp { get; init; }
    public int? OpponentMr { get; init; }
    public int? OpponentLeagueRank { get; init; }
    public int? OpponentMasterLeague { get; init; }
    public int? MasterRatingRanking { get; init; }
    public int? OpponentMasterRatingRanking { get; init; }
    public int? MyRounds { get; init; }
    public int? OpponentRounds { get; init; }
    public int? InputType { get; init; }
    public int? MasterLeague { get; init; }
    public int? Season { get; init; }
    public string Outcome => MyRounds is null || OpponentRounds is null ? "待确认" : MyRounds > OpponentRounds ? "胜" : MyRounds < OpponentRounds ? "负" : "平";
    public string Score => MyRounds is null || OpponentRounds is null ? "—" : $"{MyRounds} : {OpponentRounds}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Mode => BattleCategory.All.FirstOrDefault(c => c.Key == Category)?.Name ?? Category;
    public string Time => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.FromUnixTimeSeconds(PlayedAt), "Taipei Standard Time").ToString("yyyy-MM-dd HH:mm:ss");
    public string LpText => ScoreDisplay.Value(Lp);
    public string MrText => Mr > 0 ? Mr.Value.ToString("N0") : "—";
}

public record BattlePage(int CurrentPage, int TotalPages, IReadOnlyList<Battle> Battles);
public record BattleFilter(string OwnerId, string? Category = null, string? Character = null, string? Outcome = null,
    string Search = "", long? From = null, long? Until = null, string? OpponentCharacter=null,
    bool FavoritesOnly=false,IReadOnlyList<string>? Tags=null,bool MatchAllTags=false);
public record CharacterSummary(string Character, int CharacterId, int Count, int Wins, int Losses, int Draws, int? Lp, int? Mr)
{
    public string WinRate => Wins + Losses + Draws == 0 ? "—" : $"{100d * Wins / (Wins + Losses + Draws):F1}%";
    public string LpText => ScoreDisplay.Value(Lp);
    public string MrText => Mr > 0 ? Mr.Value.ToString("N0") : "—";
}
// Lp/Mr are the official pre-match values; deltas are this match's settlement.
public record ScorePoint(long PlayedAt, string Time, int? Lp, int? Mr, int? LpDelta, int? MrDelta, string ReplayId, bool Estimated = false)
{
    public int? LpAfter => Lp + LpDelta;
    public int? MrAfter => Mr > 0 ? Mr + MrDelta : null;
    public string LpChange => LpDelta?.ToString("+0;−0;0") ?? "—";
    public string MrChange => MrDelta?.ToString("+0;−0;0") ?? "—";
    public string MrText => Mr > 0 ? Mr.Value.ToString("N0") : "—";
}
public record CurrentScores(DateTimeOffset FetchedAt, int Season, IReadOnlyList<CharacterRank> Characters);

public static class MatchParser
{
    public static BattlePage Parse(string json, string owner, string category)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement.GetProperty("pageProps");
        var list = p.GetProperty("replay_list");
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException(Strings.ReplayFormatChanged);
        var records = new List<Battle>();
        foreach (var r in list.EnumerateArray())
        {
            var one = r.GetProperty("player1_info"); var two = r.GetProperty("player2_info");
            var oneId = Id(one.GetProperty("player"), "short_id"); var twoId = Id(two.GetProperty("player"), "short_id");
            if (oneId != owner && twoId != owner) throw new InvalidDataException(Strings.WrongPlayerRecords);
            var mine = oneId == owner ? one : two; var other = oneId == owner ? two : one;
            var replayId = Text(r, "replay_id");
            if (string.IsNullOrWhiteSpace(replayId)) throw new InvalidDataException(Strings.MissingReplayId);
            var seconds = r.GetProperty("uploaded_at").GetInt64();
            if (seconds > 100_000_000_000) seconds /= 1000;
            records.Add(new Battle
            {
                OwnerId = owner, ReplayId = replayId, Category = category, PlayedAt = seconds,
                PlayerName = Text(mine.GetProperty("player"), "fighter_id"),
                CharacterId = Int(mine, "character_id") ?? throw new InvalidDataException(Strings.MissingCharacterId),
                Character = Text(mine, "character_name"), OpponentId = Id(other.GetProperty("player"), "short_id"),
                Opponent = Text(other.GetProperty("player"), "fighter_id"), OpponentCharacter = Text(other, "character_name"),
                OpponentCharacterId=Int(other,"character_id"),
                Lp = Int(mine, "league_point"), Mr = Int(mine, "master_rating"), LeagueRank = Int(mine, "league_rank"),
                OpponentLp = Int(other, "league_point"), OpponentMr = Int(other, "master_rating"),
                OpponentLeagueRank = Int(other,"league_rank"), OpponentMasterLeague = Int(other,"master_league"),
                MasterRatingRanking = Int(mine,"master_rating_ranking"), OpponentMasterRatingRanking = Int(other,"master_rating_ranking"),
                InputType = Int(mine, "battle_input_type"), MasterLeague = Int(mine, "master_league"),
                // master_league is a rank badge, not a season ID. Most replay responses omit season_id.
                Season = Int(r, "season_id"),
                MyRounds = Rounds(mine), OpponentRounds = Rounds(other)
            });
        }
        return new(Int(p, "current_page") ?? 1, Int(p, "total_page") ?? throw new InvalidDataException(Strings.MissingPagingData), records);
    }
    public static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
    public static string Id(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetDecimal().ToString("0", System.Globalization.CultureInfo.InvariantCulture) : Text(e, key);
    public static int? Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static int? Rounds(JsonElement e)
    {
        if (!e.TryGetProperty("round_results", out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() == 0) return null;
        var values = r.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        return values.Count(v => v > 0);
    }
}
