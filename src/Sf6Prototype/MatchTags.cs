using System.Text;

namespace Sf6Prototype;

public sealed record MatchTag(string Name,int Color);

public static class TagNames
{
    public static string Normalize(string value)
    {
        var name=value.Trim().Normalize(NormalizationForm.FormC);
        if(name.Length is 0 or >32 || name.Any(char.IsControl)) throw new ArgumentException("标签名称须为 1–32 个字符。");
        return name;
    }
    public static IReadOnlyList<MatchTag> Suggest(IEnumerable<MatchTag> tags,string prefix) => tags
        .Where(t=>t.Name.StartsWith(prefix.Trim(),StringComparison.OrdinalIgnoreCase)).OrderBy(t=>t.Name,StringComparer.CurrentCultureIgnoreCase).ToArray();
}

public sealed partial class BattleStore
{
    public IReadOnlyList<MatchTag> AllTags()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT name,color FROM tags ORDER BY name COLLATE NOCASE";
        using var reader=cmd.ExecuteReader();var tags=new List<MatchTag>();
        while(reader.Read()) tags.Add(new(reader.GetString(0),reader.GetInt32(1)));return tags;
    }
    public Dictionary<string,IReadOnlyList<MatchTag>> TagsFor(string owner)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT t.replay_id,c.name,c.color FROM battle_tags t JOIN tags c ON c.name=t.tag_name WHERE t.owner_id=$owner ORDER BY c.name COLLATE NOCASE";
        Add(cmd,"$owner",owner);using var reader=cmd.ExecuteReader();var result=new Dictionary<string,List<MatchTag>>();
        while(reader.Read())
        {
            var id=reader.GetString(0);if(!result.TryGetValue(id,out var tags)) result[id]=tags=[];
            tags.Add(new(reader.GetString(1),reader.GetInt32(2)));
        }
        return result.ToDictionary(p=>p.Key,p=>(IReadOnlyList<MatchTag>)p.Value);
    }
    public void AddTag(string owner,string replay,string name)
    {
        name=TagNames.Normalize(name);
        using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT OR IGNORE INTO tags(name,color) SELECT $name,COALESCE(MAX(color)+1,0) FROM tags";
        Add(cmd,"$name",name);cmd.ExecuteNonQuery();
        cmd.CommandText="INSERT OR IGNORE INTO battle_tags(owner_id,replay_id,tag_name) VALUES($owner,$replay,$name)";
        Add(cmd,"$owner",owner);Add(cmd,"$replay",replay);cmd.ExecuteNonQuery();tx.Commit();
    }
    public void RemoveTag(string owner,string replay,string name)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="DELETE FROM battle_tags WHERE owner_id=$owner AND replay_id=$replay AND tag_name=$name";
        Add(cmd,"$owner",owner);Add(cmd,"$replay",replay);Add(cmd,"$name",name);cmd.ExecuteNonQuery();
        // Keep the vocabulary and its color for future autocomplete, even after the last use.
    }
}
