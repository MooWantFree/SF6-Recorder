using System.Collections;
using System.Text.Json;

namespace Sf6Prototype;

public sealed record BattleStatistics(int Count,int Wins,int Losses,int Draws,bool HasTags);
// SQL-backed, ordered list. Indexing and Take load only bounded pages; count and
// result totals are aggregates. The dispatcher replaces it when the archive changes.
public sealed class BattleReadView(BattleStore store,BattleFilter filter,BattleStatistics statistics) : IReadOnlyList<Battle>
{
    private const int PageSize=40;
    private readonly Dictionary<int,List<Battle>> _pages=[];
    public BattleFilter Filter {get;}=filter;
    public BattleStatistics Statistics {get;}=statistics;
    public int Count=>Statistics.Count;
    public Battle this[int index]
    {
        get
        {
            if(index<0 || index>=Count) throw new ArgumentOutOfRangeException(nameof(index));
            var page=index/PageSize;
            if(!_pages.TryGetValue(page,out var rows))
            {
                if(_pages.Count>=8) _pages.Clear();
                _pages[page]=rows=store.QuerySummaryPage(Filter,page*PageSize,PageSize);
            }
            return rows[index%PageSize];
        }
    }
    public IReadOnlyList<Battle> Find(IEnumerable<string> ids)=>store.QuerySummaryIds(Filter,ids);
    public IEnumerator<Battle> GetEnumerator() {for(var i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
public sealed partial class BattleStore
{
    private readonly Dictionary<string,BattleReadView> _readViews=[];
    private long _viewRevision=-1;
    public BattleReadView ReadView(BattleFilter filter)
    {
        lock(_readGate)
        {
            if(_viewRevision!=_readRevision) {_readViews.Clear();_viewRevision=_readRevision;}
            var key=JsonSerializer.Serialize(filter);
            if(_readViews.TryGetValue(key,out var view)) return view;
            using var db=Open();using var cmd=db.CreateCommand();
            cmd.CommandText="""
                SELECT COUNT(*),COALESCE(SUM(outcome='胜'),0),COALESCE(SUM(outcome='负'),0),COALESCE(SUM(outcome='平'),0),
                COALESCE(MAX(EXISTS(SELECT 1 FROM battle_tags t WHERE t.owner_id=battles.owner_id AND t.replay_id=battles.replay_id)),0)
                FROM battles WHERE
                """+" "+FilterSql(cmd,filter);
            using var reader=cmd.ExecuteReader();reader.Read();
            view=new(this,filter,new(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),reader.GetInt32(4)!=0));
            if(_readViews.Count>=16) _readViews.Clear();
            _readViews[key]=view;return view;
        }
    }
    internal List<Battle> QuerySummaryIds(BattleFilter filter,IEnumerable<string> ids)
    {
        var selected=ids.Distinct().ToArray();if(selected.Length==0)return [];
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT json_remove(data_json,'$.RawJson') FROM battles WHERE "+FilterSql(cmd,filter)+
            " AND replay_id IN (SELECT value FROM json_each($ids)) ORDER BY played_at DESC,replay_id";
        Add(cmd,"$ids",JsonSerializer.Serialize(selected));
        using var reader=cmd.ExecuteReader();var result=new List<Battle>();
        while(reader.Read())result.Add(JsonSerializer.Deserialize<Battle>(reader.GetString(0))!);
        return result;
    }
}
