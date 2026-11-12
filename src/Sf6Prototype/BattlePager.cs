using System.IO;

namespace Sf6Prototype;

public sealed record ScoreRange(int? CharacterId,int Count,long? From=null,long? Until=null)
{
    public ScoreRange Resolve(IReadOnlyList<Battle> ranked) => CharacterId!=null ? this :
        this with {CharacterId=ranked.GroupBy(b=>b.CharacterId).OrderByDescending(g=>g.Count()).FirstOrDefault()?.Key};
    public bool Contains(long time) => (From==null || time>=From) && (Until==null || time<Until);
    public bool HasEnough(IReadOnlyList<Battle> ranked) => Count>0 && CharacterId!=null && ranked.Count(b=>b.CharacterId==CharacterId && Contains(b.PlayedAt))>=Count;
}

public sealed class BattlePager
{
    private sealed class Window
    {
        public int Limit=20, NextPage=1;
        public int? TotalPages;
        public bool Initialized, Complete;
        public string? Boundary, Head;
        public long Oldest=long.MaxValue;
        public readonly HashSet<string> Signatures=[];
        public readonly Dictionary<int,string> Newer=[];
    }
    private readonly BattleStore _store;
    private readonly Dictionary<string,Window> _windows=[];
    public string Owner { get; }
    public bool RankHeadFetched { get; private set; }
    public bool IsPreparingInitial { get; private set; }
    public event Action<BattleLoadProgress>? ProgressChanged;
    public BattlePager(BattleStore store,string owner)
    {
        _store=store; Owner=owner;
        foreach(var category in BattleCategory.All)
            _windows[category.Key]=new() {Complete=store.HasCategorySnapshot(owner,category.Key)};
    }
    private static IEnumerable<string> Categories(string category) => string.IsNullOrEmpty(category) ? BattleCategory.All.Select(c=>c.Key) : [category];
    private static List<Battle> Ordered(IReadOnlyList<Battle> rows,string category) => rows.Where(b=>b.Category==category).OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
    private static bool LocalPageAvailable(IReadOnlyList<Battle> rows,Window window) => rows.Count>window.Limit && (window.Complete || rows[window.Limit].PlayedAt>window.Oldest);
    public bool NeedsInitial(string category) => Categories(category).Any(c=>!_windows[c].Initialized && !_windows[c].Complete);
    public void Reset(string category)
    {
        foreach(var key in Categories(category)) _windows[key]=new() {Boundary=_store.Frontier(Owner,key)};
    }
    public IReadOnlyList<Battle> Visible(IReadOnlyList<Battle> filtered,string category,bool orderedPrefix=true,IReadOnlyList<Battle>? retained=null,bool prependOnly=false)
    {
        // An unseen stream may contain newer games than every stream already fetched.
        // Wait for its head before committing any globally ordered rows to the screen.
        var ready=!(orderedPrefix && IsPreparingInitial && NeedsInitial(category));
        var rows=new List<Battle>(); var cutoff=long.MinValue;
        foreach(var key in Categories(category))
        {
            var window=_windows[key]; var games=Ordered(filtered,key);
            var visible=games.Take(window.Limit).ToList(); rows.AddRange(visible);
            // Only expose the chronological prefix known across every stream. Loading
            // another category must not insert newer matches above the scrolling position.
            if(games.Count>window.Limit && visible.Count>0) cutoff=Math.Max(cutoff,games[window.Limit].PlayedAt);
            if(!window.Complete && window.Initialized) cutoff=Math.Max(cutoff,window.Oldest);
        }
        // A page can split matches with the same timestamp. Keep that boundary hidden
        // until the next page establishes their complete ordering.
        var prefix=rows.Where(b=>ready && (!orderedPrefix || b.PlayedAt>cutoff));
        // Refreshing a partial cache must not retract rows the user already saw.
        // Their visual position is anchored by replay ID while newer gaps are filled.
        var kept=retained?.Where(b=>b.OwnerId==Owner).Select(b=>b.ReplayId).ToHashSet() ?? [];
        if(prependOnly && retained?.FirstOrDefault(b=>b.OwnerId==Owner) is { } head)
            prefix=prefix.Where(b=>b.PlayedAt>head.PlayedAt || b.PlayedAt==head.PlayedAt && StringComparer.Ordinal.Compare(b.ReplayId,head.ReplayId)<0);
        return prefix.Concat(filtered.Where(b=>kept.Contains(b.ReplayId))).DistinctBy(b=>b.ReplayId)
            .OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId, StringComparer.Ordinal).ToList();
    }
    private string? NextCategory(IReadOnlyList<Battle> filtered,string category)
    {
        var candidates=new List<(string Key,long Time)>();
        foreach(var key in Categories(category))
        {
            var window=_windows[key]; var games=Ordered(filtered,key);
            if(games.Count>window.Limit || !window.Complete)
                candidates.Add((key,Math.Max(games.Count>window.Limit ? games[window.Limit].PlayedAt : long.MinValue,window.Complete ? long.MinValue : window.Oldest)));
        }
        return candidates.OrderByDescending(c=>c.Time).Select(c=>c.Key).FirstOrDefault();
    }
    public bool HasMore(IReadOnlyList<Battle> filtered,string category) => NextCategory(filtered,category)!=null;
    public bool MoreNeedsNetwork(IReadOnlyList<Battle> filtered,string category)
    {
        var key=NextCategory(filtered,category);
        return key!=null && !LocalPageAvailable(Ordered(filtered,key),_windows[key]);
    }
    public bool NeedsScoreRange(ScoreRange range)
    {
        if(range.Count<0) throw new ArgumentOutOfRangeException(nameof(range));
        if(_windows["rank"].Complete) return false;
        if(range.From is { } start && _windows["rank"].Initialized && _windows["rank"].Oldest<start) return false;
        var ranked=_store.Query(new(Owner,Category:"rank"));
        return !range.Resolve(ranked).HasEnough(ranked);
    }
    public async Task<SyncResult> LoadScoreRangeAsync(IBattleSource source,ScoreRange range,CancellationToken token,Func<Task> pageSaved,bool forceHead=false)
    {
        token.ThrowIfCancellationRequested();
        if(range.Count<0) throw new ArgumentOutOfRangeException(nameof(range));
        var window=_windows["rank"];
        range=range.Resolve(_store.Query(new(Owner,Category:"rank")));
        RankHeadFetched=false;
        if(!forceHead && !NeedsScoreRange(range)) return new(0,0,source.Requests);
        var run=_store.BeginRun(Owner); var added=0; var pages=0;
        var initialMatches=_store.Query(new(Owner,Category:"rank")).Count(b=>b.CharacterId==range.CharacterId && range.Contains(b.PlayedAt));
        void Report(bool finished=false)
        {
            var matches=_store.Query(new(Owner,Category:"rank")).Count(b=>b.CharacterId==range.CharacterId && range.Contains(b.PlayedAt));
            double? fraction=finished ? 1 : forceHead ? 0 : range.Count>0 ? Math.Min(1,(double)matches/range.Count) :
                window.TotalPages is >0 ? (double)(window.NextPage-1)/window.TotalPages.Value : null;
            int? remaining=range.Count==0 && window.TotalPages is { } total ? Math.Max(0,total-window.NextPage+1) : null;
            // Estimate only after observing the selected character/date density, not all matches.
            if(range.Count>0 && pages>0 && matches>initialMatches)
            {
                var estimate=(int)Math.Ceiling(Math.Max(0,range.Count-matches)*pages/(double)(matches-initialMatches));
                remaining=window.TotalPages is { } limit ? Math.Min(Math.Max(0,limit-window.NextPage+1),estimate) : estimate;
            }
            var detail=range.Count>0 ? $"已准备 {Math.Min(matches,range.Count)} / {range.Count} 场" : $"已准备 {matches} 场";
            detail+=$" · 已读取 {pages} 页";
            if(finished && range.Count>0 && matches<range.Count) detail+=" · 已到可查范围";
            ProgressChanged?.Invoke(new(finished ? "分数记录已准备" : $"读取排位 · 第 {window.NextPage} 页",fraction,pages,finished ? 0 : remaining,detail));
        }
        try
        {
            while(forceHead || NeedsScoreRange(range))
            {
                token.ThrowIfCancellationRequested();
                Report();
                if(window.NextPage==1) window.Boundary=_store.Frontier(Owner,"rank");
                window.Limit=Math.Max(window.Limit,window.NextPage*10);
                added+=await FetchOneAsync(source,"rank",token,pageSaved); pages++;
                forceHead=false;
                // With no previous games, choose the same first character as the score view,
                // then keep it fixed while later pages contain other characters.
                range=range.Resolve(_store.Query(new(Owner,Category:"rank")));
            }
            token.ThrowIfCancellationRequested();Report(finished:true);
            _store.FinishRun(run,_windows.Values.All(w=>w.Complete) ? "completed" : "paged",source.Requests,added,$"{pages} pages");
            return new(added,pages,source.Requests);
        }
        catch(Exception ex)
        { _store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name); throw; }
    }
    public async Task<SyncResult> LoadInitialAsync(IBattleSource source,string category,CancellationToken token,Func<Task> pageSaved)
    {
        var keys=Categories(category).ToArray(); var run=_store.BeginRun(Owner); var added=0; var pages=0;
        RankHeadFetched=false;
        IsPreparingInitial=true;
        try
        {
            var pending=keys.Where(key=>!_windows[key].Initialized && !_windows[key].Complete).ToArray();
            var done=pending.ToDictionary(key=>key,_=>0);
            void Report(string stage)
            {
                var completed=done.Values.Sum();var total=pending.Length*2;
                ProgressChanged?.Invoke(new(stage,total==0 ? 1 : (double)completed/total,pages,total-completed,$"已读取 {pages} 页 · {done.Count(p=>p.Value==2)} / {pending.Length} 类完成"));
            }
            // Discover all stream heads first, then fetch their second pages.
            for(var i=0;i<2;i++) foreach(var key in pending)
            {
                var window=_windows[key];
                if(window.Complete) continue;
                Report($"读取{BattleCategory.All.Single(c=>c.Key==key).Name} · 第 {window.NextPage} 页");
                if(i==0) window.Boundary=_store.Frontier(Owner,key);
                added+=await FetchOneAsync(source,key,token,pageSaved); pages++;
                done[key]=window.Complete ? 2 : done[key]+1;
            }
            token.ThrowIfCancellationRequested();Report("战绩已保存");
            _store.FinishRun(run,_windows.Values.All(w=>w.Complete) ? "completed" : "paged",source.Requests,added,$"{pages} pages");
            return new(added,pages,source.Requests);
        }
        catch(Exception ex) { _store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name); throw; }
        finally { IsPreparingInitial=false; }
    }
    public async Task<SyncResult> LoadMoreAsync(IBattleSource? source,string category,IReadOnlyList<Battle> filtered,CancellationToken token,Func<Task> pageSaved)
    {
        var key=NextCategory(filtered,category); if(key==null) return new(0,0,0);
        var window=_windows[key];
        if(LocalPageAvailable(Ordered(filtered,key),window))
        { window.Limit+=10; await pageSaved(); return new(0,0,0); }
        if(source==null) throw new InvalidOperationException("需要登录后继续加载。");
        var run=_store.BeginRun(Owner); var added=0;
        try
        {
            window.Limit+=10;
            added=await FetchOneAsync(source,key,token,pageSaved);
            _store.FinishRun(run,_windows.Values.All(w=>w.Complete) ? "completed" : "paged",source.Requests,added,"1 page");
            return new(added,1,source.Requests);
        }
        catch(Exception ex) { _store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name); throw; }
    }
    private async Task<int> FetchOneAsync(IBattleSource source,string category,CancellationToken token,Func<Task> pageSaved)
    {
        var window=_windows[category]; var page=await source.FetchPageAsync(Owner,category,window.NextPage,token);
        token.ThrowIfCancellationRequested();
        if(page.CurrentPage!=window.NextPage) throw new InvalidDataException("官网返回了重复页码。");
        if(page.Battles.Any(b=>b.OwnerId!=Owner || b.Category!=category)) throw new InvalidDataException("战绩与所选玩家或分类不一致。");
        var signature=string.Join('|',page.Battles.Select(b=>b.ReplayId));
        if(page.Battles.Count>0 && !window.Signatures.Add(signature)) throw new InvalidDataException("官网返回了重复内容。");
        if(window.NextPage==1) window.Head=page.Battles.FirstOrDefault()?.ReplayId;
        if(category=="rank" && window.NextPage==1) RankHeadFetched=true;
        var added=_store.SavePage(page.Battles);
        if(category=="rank") _store.SaveRankLinks(Owner,page.Battles,window.Newer);
        window.Initialized=true; window.NextPage++;
        window.TotalPages=page.TotalPages;
        if(page.Battles.Count>0) window.Oldest=page.Battles.Min(b=>b.PlayedAt);
        window.Complete=page.Battles.Count==0 || page.CurrentPage>=page.TotalPages || window.Boundary!=null && page.Battles.Any(b=>b.ReplayId==window.Boundary);
        if(window.Complete) _store.CompleteCategory(Owner,category,window.Head);
        await pageSaved();
        return added;
    }
}
