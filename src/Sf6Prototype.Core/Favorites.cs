using System.Text.Json;

namespace Sf6Prototype;

public sealed record FavoriteRecord(Battle Battle,IReadOnlyList<MatchTag> Tags);

public static class Favorites
{
    public static Battle FromPlayer(Battle battle,string player)
    {
        if(battle.OwnerId==player) return battle;
        if(battle.OpponentId!=player) throw new ArgumentException(Strings.PlayerNotInMatch);
        return battle with
        {
            OwnerId=battle.OpponentId,PlayerName=battle.Opponent,OpponentId=battle.OwnerId,Opponent=battle.PlayerName,
            CharacterId=battle.OpponentCharacterId ?? -1,Character=battle.OpponentCharacter,OpponentCharacter=battle.Character,OpponentCharacterId=battle.CharacterId,
            Lp=battle.OpponentLp,Mr=battle.OpponentMr,OpponentLp=battle.Lp,OpponentMr=battle.Mr,
            LeagueRank=battle.OpponentLeagueRank,MasterLeague=battle.OpponentMasterLeague,
            OpponentLeagueRank=battle.LeagueRank,OpponentMasterLeague=battle.MasterLeague,
            MasterRatingRanking=battle.OpponentMasterRatingRanking,OpponentMasterRatingRanking=battle.MasterRatingRanking,
            MyRounds=battle.OpponentRounds,OpponentRounds=battle.MyRounds,InputType=null,
            RoundResults=battle.OpponentRoundResults,OpponentRoundResults=battle.RoundResults
        };
    }
    public static IReadOnlyList<FavoriteRecord> ForPlayer(IEnumerable<FavoriteRecord> records,string player) => records
        .Where(r=>r.Battle.OwnerId==player || r.Battle.OpponentId==player).GroupBy(r=>r.Battle.ReplayId)
        .Select(g=>new FavoriteRecord(FromPlayer(g.OrderByDescending(r=>r.Battle.OwnerId==player).First().Battle,player),
            g.SelectMany(r=>r.Tags).DistinctBy(t=>t.Name,StringComparer.OrdinalIgnoreCase).OrderBy(t=>t.Name).ToArray()))
        .OrderByDescending(r=>r.Battle.PlayedAt).ThenBy(r=>r.Battle.ReplayId,StringComparer.Ordinal).ToArray();
    public static IReadOnlyList<PlayerIdentity> Players(IEnumerable<FavoriteRecord> records,PlayerIdentity signedIn,string search="")
    {
        var players=records.OrderByDescending(r=>r.Battle.PlayedAt).SelectMany(r=>new[] {new PlayerIdentity(r.Battle.OwnerId,r.Battle.PlayerName),new PlayerIdentity(r.Battle.OpponentId,r.Battle.Opponent)})
            .Where(p=>p.IsValid).DistinctBy(p=>p.Id).Where(p=>p.Id!=signedIn.Id).OrderBy(p=>p.DisplayName).Prepend(signedIn).Where(p=>p.IsValid);
        var query=search.Trim();return players.Where(p=>p.Id.Contains(query,StringComparison.OrdinalIgnoreCase) || p.DisplayName.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public static List<Battle> Filter(IEnumerable<FavoriteRecord> records,BattleFilter filter) => records.Where(r=>
        r.Battle.OwnerId==filter.OwnerId &&
        (filter.Character==null || r.Battle.Character==filter.Character) && (filter.OpponentCharacter==null || r.Battle.OpponentCharacter==filter.OpponentCharacter) &&
        (filter.Outcome==null || r.Battle.Outcome==filter.Outcome) && (filter.From==null || r.Battle.PlayedAt>=filter.From) && (filter.Until==null || r.Battle.PlayedAt<filter.Until) &&
        (string.IsNullOrWhiteSpace(filter.Search) || new[]{r.Battle.Opponent,r.Battle.OpponentId,r.Battle.ReplayId}.Any(s=>s.Contains(filter.Search.Trim(),StringComparison.OrdinalIgnoreCase))) &&
        (filter.Tags is not {Count:>0} || (filter.MatchAllTags ? filter.Tags.All(t=>r.Tags.Any(x=>x.Name.Equals(t,StringComparison.OrdinalIgnoreCase))) : filter.Tags.Any(t=>r.Tags.Any(x=>x.Name.Equals(t,StringComparison.OrdinalIgnoreCase))))))
        .Select(r=>r.Battle).OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
}

public sealed partial class BattleStore
{
    private IReadOnlyList<FavoriteRecord>? _favoriteSummaries;
    private long _favoriteRevision=-1;
    public IReadOnlyList<FavoriteRecord> FavoriteRecords(bool summariesOnly=false)
    {
        if(summariesOnly && _favoriteSummaries!=null && _favoriteRevision==ReadRevision) return _favoriteSummaries;
        using var db=Open();using var cmd=db.CreateCommand();
        var payload=summariesOnly ? "json_remove(b.data_json,'$.RawJson')" : "b.data_json";
        cmd.CommandText="SELECT b.owner_id,b.replay_id,"+payload+",c.name,c.color FROM battle_tags t JOIN battles b ON b.owner_id=t.owner_id AND b.replay_id=t.replay_id JOIN tags c ON c.name=t.tag_name ORDER BY b.played_at DESC,b.owner_id,b.replay_id,c.name";
        using var reader=cmd.ExecuteReader();var entries=new Dictionary<(string,string),(Battle Battle,List<MatchTag> Tags)>();
        while(reader.Read())
        {
            var key=(reader.GetString(0),reader.GetString(1));
            if(!entries.TryGetValue(key,out var entry)) entries[key]=entry=(JsonSerializer.Deserialize<Battle>(reader.GetString(2))!,[]);
            entry.Tags.Add(new(reader.GetString(3),reader.GetInt32(4)));
        }
        var result=entries.Values.Select(e=>new FavoriteRecord(e.Battle,e.Tags)).ToArray();
        if(summariesOnly) {_favoriteSummaries=result;_favoriteRevision=ReadRevision;}
        return result;
    }
    public void SetFavoriteTag(string player,string replay,string name,bool selected)
    {
        name=TagNames.Normalize(name);
        var sources=FavoriteRecords().Where(r=>r.Battle.ReplayId==replay && (r.Battle.OwnerId==player || r.Battle.OpponentId==player)).ToArray();
        if(sources.Length==0) throw new ArgumentException(Strings.FavoriteRemoved);
        if(selected) {AddTag(sources[0].Battle.OwnerId,replay,name);return;}
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="DELETE FROM battle_tags WHERE owner_id=$owner AND replay_id=$replay AND tag_name=$name";
        Add(cmd,"$replay",replay);Add(cmd,"$name",name);var owner=cmd.Parameters.Add("$owner",Microsoft.Data.Sqlite.SqliteType.Text);
        foreach(var source in sources) {owner.Value=source.Battle.OwnerId;cmd.ExecuteNonQuery();}tx.Commit();Changed();
    }
}
