namespace Sf6Prototype;

// Per-archive incremental settlements. Only changed replays, their previous match,
// changed continuity links and current-score estimates need recalculation.
public sealed class ScoreProjection
{
    private static readonly IComparer<Battle> Order=Comparer<Battle>.Create((a,b)=>
    {var time=b.PlayedAt.CompareTo(a.PlayedAt);return time!=0 ? time : Comparer<string>.Default.Compare(b.ReplayId,a.ReplayId);});
    private readonly object _gate=new();
    private readonly Dictionary<int,List<Battle>> _characters=[];
    private readonly Dictionary<string,Battle> _rows=[];
    private Dictionary<string,string> _links=[];
    private Dictionary<string,ScorePoint> _points=[];
    private CurrentScores? _current;
    private string? _owner;
    public Dictionary<string,ScorePoint> Update(string owner,IReadOnlyList<Battle> rows,CurrentScores? current,IReadOnlyDictionary<string,string> links)
    {
        lock(_gate)
        {
            if(_owner!=owner) {_owner=owner;_rows.Clear();_characters.Clear();_links.Clear();_points=[];_current=null;}
            var affected=new HashSet<string>();var present=new HashSet<string>();
            void Remove(Battle old)
            {
                var list=_characters[old.CharacterId];var index=list.BinarySearch(old,Order);
                if(index+1<list.Count) affected.Add(list[index+1].ReplayId);
                list.RemoveAt(index);_rows.Remove(old.ReplayId);affected.Add(old.ReplayId);
            }
            foreach(var row in rows)
            {
                if(row.Category!="rank")continue;
                present.Add(row.ReplayId);
                if(_rows.TryGetValue(row.ReplayId,out var old))
                {if(ReferenceEquals(old,row))continue;Remove(old);}
                if(!_characters.TryGetValue(row.CharacterId,out var list))_characters[row.CharacterId]=list=[];
                var index=~list.BinarySearch(row,Order);list.Insert(index,row);_rows[row.ReplayId]=row;
                affected.Add(row.ReplayId);
                if(index+1<list.Count)affected.Add(list[index+1].ReplayId);
            }
            foreach(var old in _rows.Values.Where(b=>!present.Contains(b.ReplayId)).ToArray())Remove(old);
            foreach(var link in links)
                if(!_links.TryGetValue(link.Key,out var previous) || previous!=link.Value)affected.Add(link.Key);
            foreach(var link in _links)
                if(!links.ContainsKey(link.Key))affected.Add(link.Key);
            var scoresChanged=_current?.FetchedAt!=current?.FetchedAt || _current?.Season!=current?.Season ||
                !(_current?.Characters ?? []).SequenceEqual(current?.Characters ?? []);
            if(scoresChanged) foreach(var list in _characters.Values)if(list.Count>0)affected.Add(list[0].ReplayId);
            if(affected.Count>0)
            {
                var points=new Dictionary<string,ScorePoint>(_points);
                foreach(var id in affected)
                {
                    if(!_rows.TryGetValue(id,out var row)) {points.Remove(id);continue;}
                    var list=_characters[row.CharacterId];var index=list.BinarySearch(row,Order);
                    var next=index>0 ? list[index-1] : null;
                    var linked=next!=null && links.TryGetValue(id,out var known) && known==next.ReplayId;
                    points[id]=ScoreCalculator.Point(row,next,linked,current);
                }
                _points=points;_links=new(links);
            }
            _current=current;return _points;
        }
    }
}
