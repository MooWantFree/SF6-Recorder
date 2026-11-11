using System.IO;
using System.Text.Json;

namespace Sf6Prototype;

public record CharacterRank(int CharacterId,string Name,int? Lp,int? Mr,int? LeagueRank,int? MasterLeague)
{
    public bool Ranked => (LeagueRank is >=1 and <=37 && Lp >=0) || Mr >0;
    public string LpText => Ranked && Lp>=0 ? Lp.Value.ToString("N0") : "—";
    public string MrText => Mr>0 ? Mr.Value.ToString("N0") : "—";
    public string RankName
    {
        get
        {
            if(!Ranked) return "未定级";
            if(MasterLeague==37 || LeagueRank==37) return "Legend";
            if(LeagueRank>=36 || Mr>0) return "Master";
            var tiers=new[]{"Rookie","Iron","Bronze","Silver","Gold","Platinum","Diamond"};
            return LeagueRank is >=1 and <=35 ? $"{tiers[(LeagueRank.Value-1)/5]} {(LeagueRank.Value-1)%5+1}" : "—";
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
            return new CharacterRank(c.GetProperty("character_id").GetInt32(),MatchParser.Text(c,"character_name"),MatchParser.Int(info,"league_point"),MatchParser.Int(info,"master_rating"),MatchParser.Int(info,"league_rank"),MatchParser.Int(info,"master_league"));
        }).ToList();
    }
}
