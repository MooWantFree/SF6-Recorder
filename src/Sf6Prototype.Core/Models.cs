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
    public int[]? RoundResults { get; init; }
    public int[]? OpponentRoundResults { get; init; }
    public JsonElement? RawJson { get; init; }
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
                OwnerId = owner, ReplayId = replayId, Category = category.Length>0 ? category : ReplayCategory(r), PlayedAt = seconds,
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
                MyRounds = Rounds(mine), OpponentRounds = Rounds(other),
                RoundResults=ReadRoundResults(mine),OpponentRoundResults=ReadRoundResults(other),RawJson=r.Clone()
            });
        }
        return new(Int(p, "current_page") ?? 1, Int(p, "total_page") ?? throw new InvalidDataException(Strings.MissingPagingData), records);
    }
    private static string ReplayCategory(JsonElement replay) => Int(replay,"replay_battle_type") switch
    {
        // The response's numeric type identifies the mode; its translated name is presentation HTML.
        1 => "rank", 2 => "casual", 3 => "custom", 4 => "hub",
        _ => throw new InvalidDataException(Strings.UnknownCategory)
    };
    public static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var v) ? v.ToString() : "";
    public static string Id(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetDecimal().ToString("0", System.Globalization.CultureInfo.InvariantCulture) : Text(e, key);
    public static int? Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static int? Rounds(JsonElement e)
    {
        return ReadRoundResults(e)?.Count(v=>v>0);
    }
    private static int[]? ReadRoundResults(JsonElement e)
    {
        if(!e.TryGetProperty("round_results",out var values) || values.ValueKind!=JsonValueKind.Array || values.GetArrayLength()==0) return null;
        var result=new List<int>();
        foreach(var value in values.EnumerateArray())
        {
            if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out var code)) return null;
            result.Add(code);
        }
        return result.ToArray();
    }
}

public sealed record RoundResult(int Number,int? OwnCode,int? OpponentCode)
{
    public string Label=>$"{Number}R";
    public string Own=>Symbol(OwnCode);
    public string Opponent=>Symbol(OpponentCode);
    public string OwnTip=>Description(OwnCode);
    public string OpponentTip=>Description(OpponentCode);
    public string OwnKind=>Kind(OwnCode);
    public string OpponentKind=>Kind(OpponentCode);
    // Buckler's round_results codes; preserve unknown values instead of guessing a finish.
    // Reference: https://sfstats.app/data/reference (round result codes).
    public static string Symbol(int? code)=>code switch {0=>"—",1=>"V",2=>"C",3=>"T",4=>"D",5=>"OD",6=>"SA",7=>"CA",8=>"Perfect",_=>"?"};
    public static string Kind(int? code)=>code switch {0=>"loss",1 or 2 or 3 or 5 or 6 or 7 or 8=>"win",_=>"other"};
    public static string Description(int? code)=>code switch
    {
        0=>Strings.RoundLost,1=>Strings.RoundVictory,2=>Strings.RoundChip,3=>Strings.RoundTime,
        4=>Strings.RoundDraw,5=>Strings.RoundOverdrive,6=>Strings.RoundSuperArt,7=>Strings.RoundCriticalArt,8=>Strings.RoundPerfect,
        null=>Strings.NoRoundInfo,_=>Localization.Format(Strings.UnknownRoundResult,code)
    };
    public static IReadOnlyList<RoundResult> From(Battle battle)=>Enumerable.Range(0,Math.Max(battle.RoundResults?.Length ?? 0,battle.OpponentRoundResults?.Length ?? 0))
        .Select(i=>new RoundResult(i+1,At(battle.RoundResults,i),At(battle.OpponentRoundResults,i))).ToArray();
    private static int? At(int[]? results,int index)=>results!=null && index<results.Length ? results[index] : null;
}
