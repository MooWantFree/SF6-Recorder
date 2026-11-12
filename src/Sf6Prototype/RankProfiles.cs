using System.IO;
using System.Text.Json;

namespace Sf6Prototype;

public record CharacterRank(int CharacterId,string Name,int? Lp,int? Mr,int? LeagueRank,int? MasterLeague,int? MasterRatingRanking = null)
{
    public static int? LeagueFromLp(int? lp) => lp switch
    {
        null or <0 => null,
        <1000 => 1+lp/200,
        <3000 => 6+(lp-1000)/400,
        <5000 => 11+(lp-3000)/400,
        <9000 => 16+(lp-5000)/800,
        <13000 => 21+(lp-9000)/800,
        <19000 => 26+(lp-13000)/1200,
        <25000 => 31+(lp-19000)/1200,
        _ => 36
    };
    // Preserve explicit unranked sentinels; only missing league fields use LP.
    public int? ResolvedLeagueRank => LeagueRank ?? (Mr>0 ? 36 : LeagueFromLp(Lp));
    public bool Ranked => Lp is not <0 && ((ResolvedLeagueRank is >=1 and <=37 && Lp >=0) || Mr >0);
    public int MasterTier => LeagueRank==37 || MasterLeague==37 || MasterRatingRanking is >=1 and <=500 ? 37 : Mr switch
    {
        >=1800 => 42, >=1700 => 41, >=1600 => 40, >0 => 36,
        _ => MasterLeague is 40 or 41 or 42 ? MasterLeague.Value : 36
    };
    public string LpText => Lp<0 ? ScoreDisplay.NewChallenger : Ranked && Lp>=0 ? Lp.Value.ToString("N0") : "—";
    public string MrText => Mr>0 ? Mr.Value.ToString("N0") : "—";
    public int? BadgeId => !Ranked ? null : ResolvedLeagueRank>=36 || Mr>0 ? MasterTier : ResolvedLeagueRank;
    public string RankName
    {
        get
        {
            if(!Ranked) return Lp<0 ? ScoreDisplay.NewChallenger : LeagueRank==null ? "—" : "未定级";
            if(ResolvedLeagueRank>=36 || Mr>0) return MasterTier switch {37=>"Legend",40=>"High Master",41=>"Grand Master",42=>"Ultimate Master",_=>"Master"};
            var tiers=new[]{"Rookie","Iron","Bronze","Silver","Gold","Platinum","Diamond"};
            var stars=new[]{"I","II","III","IV","V"};
            return ResolvedLeagueRank is >=1 and <=35 ? $"{tiers[(ResolvedLeagueRank.Value-1)/5]} {stars[(ResolvedLeagueRank.Value-1)%5]}" : "—";
        }
    }
}
public record OpponentProfile(string OwnerId,string Name,int CurrentSeason,IReadOnlyList<int> Seasons,IReadOnlyList<CharacterRank> Characters);
public static class RankProfiles
{
    public static OpponentProfile ParseProfile(string json,string expectedOwner)
    {
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        if(root.TryGetProperty("props",out var props)) root=props;
        var page=root.GetProperty("pageProps");
        var identity=page.GetProperty("fighter_banner_info").GetProperty("personal_info");
        if(MatchParser.Id(identity,"short_id")!=expectedOwner) throw new InvalidDataException("玩家资料与所选对手不一致。");
        var play=page.GetProperty("play"); var season=play.GetProperty("current_season_id").GetInt32();
        var seasons=play.GetProperty("season_ids").EnumerateArray().Select(s=>s.GetInt32()).Append(season).Where(s=>s>=0).Distinct().OrderDescending().ToList();
        return new(expectedOwner,MatchParser.Text(identity,"fighter_id"),season,seasons,ParseRows(play.GetProperty("character_league_infos")));
    }
    public static IReadOnlyList<CharacterRank> ParseSeason(string json)
    {
        using var doc=JsonDocument.Parse(json);
        if(!doc.RootElement.TryGetProperty("response",out var response) || response.ValueKind!=JsonValueKind.Object || !response.TryGetProperty("character_league_infos",out var rows))
            throw new InvalidDataException("该赛季的数据暂时无法读取。");
        return ParseRows(rows);
    }
    private static List<CharacterRank> ParseRows(JsonElement rows)
    {
        if(rows.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("官网段位数据格式已改变。");
        return rows.EnumerateArray().Select(c=>
        {
            var info=c.GetProperty("league_info");
            return new CharacterRank(c.GetProperty("character_id").GetInt32(),MatchParser.Text(c,"character_name"),MatchParser.Int(info,"league_point"),MatchParser.Int(info,"master_rating"),MatchParser.Int(info,"league_rank"),MatchParser.Int(info,"master_league"),MatchParser.Int(info,"master_rating_ranking"));
        }).ToList();
    }
}
