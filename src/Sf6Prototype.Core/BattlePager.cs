using System.IO;

namespace Sf6Prototype;

public record SyncResult(int Inserted, int Pages, int Requests);

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
        public bool Initialized, Complete, ReachedBoundary, Prepared, Incremental;
        public bool BoundaryComplete=true;
        public string? Boundary, Head;
        public IReadOnlyList<string> PreviousPrefix=[];
        public List<string> Prefix=[];
        public long Oldest=long.MaxValue;
        public readonly HashSet<string> Signatures=[];
    }
    public const string AccountBoundary="all-sync";
    private readonly BattleStore _store;
    private readonly Dictionary<string,Window> _windows=[];
    public string Owner { get; }
    public bool RankHeadFetched { get; private set; }
    public bool IsPreparingInitial { get; private set; }
    public event Action<BattleLoadProgress>? ProgressChanged;
    public BattlePager(BattleStore store,string owner)
    {
        _store=store; Owner=owner;
        foreach(var key in BattleCategory.All.Select(c=>c.Key).Prepend(""))
        {
            var window=new Window {Complete=store.HasCategorySnapshot(owner,key),Boundary=store.Frontier(owner,key)};
            if(!window.Complete && store.Prefix(owner,key) is { } prefix)
            {
                window.Prefix=prefix.ReplayIds.ToList();window.Head=window.Prefix.FirstOrDefault();
                window.NextPage=prefix.NextPage;window.TotalPages=prefix.TotalPages;
                window.Initialized=true;window.Prepared=window.Prefix.Count>=20;
                SetOldest(window,key);
            }
            _windows[key]=window;
        }
        // Older archives may have full category snapshots without an all-stream snapshot.
        if(!store.IsCategoryPending(owner,"") && BattleCategory.All.All(c=>_windows[c.Key].Complete)) _windows[""].Complete=true;
    }
    private static IReadOnlyList<Battle> Ordered(IReadOnlyList<Battle> rows,string category) =>
        rows is BattleReadView view && (category.Length==0 || view.Filter.Category==category) ? view : rows
        .Where(b=>category.Length==0 || b.Category==category).OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
    private static bool LocalPageAvailable(IReadOnlyList<Battle> rows,Window window) => rows.Count>window.Limit &&
        (window.Complete || window.Initialized && rows[window.Limit].PlayedAt>=window.Oldest);
    public bool NeedsInitial(string category) => !_windows[category].Complete && !_windows[category].Prepared &&
        (_windows[category].Incremental ? !_windows[category].ReachedBoundary : _windows[category].NextPage<=2);
    public bool IsComplete(string category) => category.Length>0 ? _windows[category].Complete :
        _windows[""].Complete && BattleCategory.All.All(c=>_windows[c.Key].Complete);
    public void Reset(string category,bool usePrefix=true)
    {
        var prefix=usePrefix ? _store.Prefix(Owner,category) : null;
        _windows[category]=prefix is {ReplayIds.Count:>0} ? new()
        {Boundary=prefix.ReplayIds[0],BoundaryComplete=false,PreviousPrefix=prefix.ReplayIds,Incremental=true} :
            new() {Boundary=_store.Frontier(Owner,category)};
    }
    private List<Battle> PrefixRows(Window window,string category)
    {
        return _store.PrefixSummaries(Owner,window.Prefix).Where(b=>category.Length==0 || b.Category==category).ToList();
    }
    private void SetOldest(Window window,string category) => window.Oldest=PrefixRows(window,category).Select(b=>b.PlayedAt).DefaultIfEmpty(long.MaxValue).Min();
    private void SaveWindow(string category)
    {
        var window=_windows[category];
        if(window.Complete) _store.CompleteCategory(Owner,category,window.Head);
        else if(window.Prefix.Count>0 && !NeedsInitial(category))
            _store.SavePrefix(Owner,category,new(window.Prefix.ToArray(),window.NextPage,window.TotalPages ?? 0));
    }
    public IReadOnlyList<Battle> Visible(IReadOnlyList<Battle> filtered,string category,bool orderedPrefix=true,IReadOnlyList<Battle>? retained=null,bool prependOnly=false)
    {
        var window=_windows[category];
        var prefix=Ordered(filtered,category).Take(window.Limit).Where(b=>!orderedPrefix || !window.Initialized || window.Complete || b.PlayedAt>=window.Oldest);
        var kept=retained?.Where(b=>b.OwnerId==Owner).Select(b=>b.ReplayId).ToHashSet() ?? [];
        if(prependOnly && retained?.FirstOrDefault(b=>b.OwnerId==Owner) is { } head)
            prefix=prefix.Where(b=>b.PlayedAt>head.PlayedAt || b.PlayedAt==head.PlayedAt && StringComparer.Ordinal.Compare(b.ReplayId,head.ReplayId)<0);
        return prefix.Concat(filtered is BattleReadView readView ? readView.Find(kept) : filtered.Where(b=>kept.Contains(b.ReplayId))).DistinctBy(b=>b.ReplayId)
            .OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
    }
    public bool HasMore(IReadOnlyList<Battle> filtered,string category) => Ordered(filtered,category).Count>_windows[category].Limit || !_windows[category].Complete;
    public bool MoreNeedsNetwork(IReadOnlyList<Battle> filtered,string category) => HasMore(filtered,category) &&
        !LocalPageAvailable(Ordered(filtered,category),_windows[category]);
    private string Reading(string category)
    {
        var window=_windows[category];var name=category.Length==0 ? Strings.All : BattleCategory.All.Single(c=>c.Key==category).Name;
        return window.TotalPages is >0 ? Localization.Format(Strings.ReadingCategoryPageOfTotal,name,window.NextPage,window.TotalPages) :
            Localization.Format(Strings.ReadingCategoryPage,name,window.NextPage);
    }
    public bool NeedsScoreRange(ScoreRange range)
    {
        if(range.Count<0) throw new ArgumentOutOfRangeException(nameof(range));
        if(_windows["rank"].Complete) return false;
        if(range.From is { } start && _windows["rank"].Initialized && _windows["rank"].Oldest<start) return false;
        var ranked=_store.QuerySummaries(new(Owner,Category:"rank"));
        return !range.Resolve(ranked).HasEnough(ranked);
    }
    public async Task<SyncResult> LoadScoreRangeAsync(IBattleSource source,ScoreRange range,CancellationToken token,Func<Task> pageSaved,bool forceHead=false)
    {
        token.ThrowIfCancellationRequested();
        if(range.Count<0) throw new ArgumentOutOfRangeException(nameof(range));
        var window=_windows["rank"];
        range=range.Resolve(_store.QuerySummaries(new(Owner,Category:"rank")));
        RankHeadFetched=false;
        var refreshToBoundary=forceHead && window.Incremental;
        if(!forceHead && !NeedsScoreRange(range)) return new(0,0,source.Requests);
        var run=_store.BeginRun(Owner); var added=0; var pages=0;
        var initialMatches=_store.QuerySummaries(new(Owner,Category:"rank")).Count(b=>b.CharacterId==range.CharacterId && range.Contains(b.PlayedAt));
        void Report(bool finished=false)
        {
            var matches=_store.QuerySummaries(new(Owner,Category:"rank")).Count(b=>b.CharacterId==range.CharacterId && range.Contains(b.PlayedAt));
            double? fraction=finished ? 1 : forceHead ? 0 : range.Count>0 ? Math.Min(1,(double)matches/range.Count) :
                window.TotalPages is >0 ? (double)(window.NextPage-1)/window.TotalPages.Value : null;
            int? remaining=range.Count==0 && window.TotalPages is { } total ? Math.Max(0,total-window.NextPage+1) : null;
            // Estimate only after observing the selected character/date density, not all matches.
            if(range.Count>0 && pages>0 && matches>initialMatches)
            {
                var estimate=(int)Math.Ceiling(Math.Max(0,range.Count-matches)*pages/(double)(matches-initialMatches));
                remaining=window.TotalPages is { } limit ? Math.Min(Math.Max(0,limit-window.NextPage+1),estimate) : estimate;
            }
            var detail=range.Count>0 ? Localization.Format(Strings.MatchesPreparedOfTotal, Math.Min(matches,range.Count), range.Count) : Localization.Format(Strings.MatchesPrepared, matches);
            detail+=Localization.Format(Strings.PagesReadSuffix, pages);
            if(finished && range.Count>0 && matches<range.Count) detail+=Strings.AvailableRangeReached;
            ProgressChanged?.Invoke(new(finished ? Strings.ScoresReady : Localization.Format(Strings.ReadingRankedPage, window.NextPage),fraction,pages,finished ? 0 : remaining,detail));
        }
        try
        {
            while(forceHead || refreshToBoundary || NeedsScoreRange(range))
            {
                token.ThrowIfCancellationRequested();
                Report();
                if(window.NextPage==1 && !window.Incremental) window.Boundary=_store.Frontier(Owner,"rank");
                window.Limit=Math.Max(window.Limit,window.NextPage*10);
                added+=await FetchOneAsync(source,"rank",token,pageSaved,commitCheckpoint:false); pages++;
                forceHead=false;
                if(window.ReachedBoundary || window.Complete) {refreshToBoundary=false;window.Prepared=true;}
                // With no previous games, choose the same first character as the score view,
                // then keep it fixed while later pages contain other characters.
                range=range.Resolve(_store.QuerySummaries(new(Owner,Category:"rank")));
            }
            token.ThrowIfCancellationRequested();SaveWindow("rank");Report(finished:true);
            _store.FinishRun(run,_windows.Values.All(w=>w.Complete) ? "completed" : "paged",source.Requests,added,$"{pages} pages");
            return new(added,pages,source.Requests);
        }
        catch(Exception ex)
        { _store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name); throw; }
    }
    public async Task<SyncResult> LoadInitialAsync(IBattleSource source,string category,CancellationToken token,Func<Task> pageSaved)
    {
        var run=_store.BeginRun(Owner);var added=0;var pages=0;var window=_windows[category];
        RankHeadFetched=false;IsPreparingInitial=true;
        void Report(bool finished=false)
        {
            var target=Math.Min(2,window.TotalPages ?? 2);
            ProgressChanged?.Invoke(new(finished ? Strings.MatchesSaved : Reading(category),finished ? 1 : window.Incremental ? null : (double)(window.NextPage-1)/Math.Max(1,target),
                pages,finished ? 0 : window.Incremental ? null : Math.Max(0,target-window.NextPage+1),window.Incremental ? Localization.Format(Strings.NewMatchesCount,added) : Localization.Format(Strings.MatchesPreparedOfTotal,Math.Min(20,Ordered(_store.QuerySummaries(new(Owner)),category).Count),20)));
        }
        try
        {
            while(NeedsInitial(category))
            {
                token.ThrowIfCancellationRequested();Report();
                added+=await FetchOneAsync(source,category,token,pageSaved,commitCheckpoint:false);pages++;
            }
            token.ThrowIfCancellationRequested();window.Prepared=true;SaveWindow(category);Report(true);
            _store.FinishRun(run,IsComplete("") ? "completed" : "paged",source.Requests,added,$"{pages} pages");
            return new(added,pages,source.Requests);
        }
        catch(Exception ex) {_store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name);throw;}
        finally {IsPreparingInitial=false;}
    }
    public async Task<SyncResult> LoadAccountAsync(IBattleSource source,CancellationToken token,Func<Task> pageSaved,Func<string?>? preferredCategory=null)
    {
        var run=_store.BeginRun(Owner);var added=0;var pages=0;
        RankHeadFetched=false;IsPreparingInitial=true;
        // Only a fully successful account refresh can establish this checkpoint.
        _windows[""]=new() {Boundary=_store.Frontier(Owner,AccountBoundary)};
        // Older versions only saved per-category checkpoints. Verify those directly once
        // instead of traversing All searching for an account checkpoint that cannot exist.
        var verifyCategoryCheckpoints=!_store.HasCategoryCheckpoint(Owner,AccountBoundary) &&
            BattleCategory.All.All(c=>_store.HasCategoryCheckpoint(Owner,c.Key));
        // Retain old checkpoints while marking every category's new coverage as unconfirmed.
        // This also survives a stop between finishing All and starting category fallback.
        foreach(var category in BattleCategory.All)
        {
            Reset(category.Key,usePrefix:false);
            _store.MarkCategoryPending(Owner,category.Key);
        }
        var all=_windows[""];
        var fetched=new HashSet<string>();
        async Task Fetch(string category)
        {
            token.ThrowIfCancellationRequested();
            ProgressChanged?.Invoke(new(Reading(category),null,pages,null,Localization.Format(Strings.NewMatchesCount,added)));
            added+=await FetchOneAsync(source,category,token,pageSaved);pages++;fetched.Add(category);
        }
        async Task PrioritizeView()
        {
            // Finish the in-flight request, then serve only the latest category's first screen.
            while(preferredCategory?.Invoke() is {Length:>0} category && _windows.ContainsKey(category))
            {
                if(!fetched.Contains(category)) Reset(category,usePrefix:false);
                if(!NeedsInitial(category)) break;
                await Fetch(category);
            }
        }
        try
        {
            do
            {
                await Fetch("");
                // A proven All boundary already covers the selected category too.
                if(!all.ReachedBoundary) await PrioritizeView();
            } while(!all.Complete && !verifyCategoryCheckpoints);
            if(!all.ReachedBoundary)
            {
                foreach(var category in BattleCategory.All)
                {
                    // A first screen fetched for the selected category can be continued here.
                    if(!fetched.Contains(category.Key)) Reset(category.Key,usePrefix:false);
                    while(!_windows[category.Key].Complete) {await Fetch(category.Key);await PrioritizeView();}
                }
                // Verified complete category windows also prove coverage for the All stream.
                all.Complete=true;
                _store.CompleteCategory(Owner,"",all.Head);
            }
            token.ThrowIfCancellationRequested();
            if(all.ReachedBoundary)
            {
                // The previous account checkpoint covered every category; All has now bridged
                // every newer match, including any category refresh that stopped halfway.
                foreach(var category in BattleCategory.All)
                {
                    var window=_windows[category.Key];
                    window.Complete=true;
                    _store.CompleteCategory(Owner,category.Key,_store.QuerySummaryPage(new(Owner,Category:category.Key),0,1).FirstOrDefault()?.ReplayId);
                }
            }
            _store.CompleteCategory(Owner,AccountBoundary,all.Head);
            await pageSaved();
            ProgressChanged?.Invoke(new(Strings.MatchesSaved,1,pages,0,Localization.Format(Strings.NewMatchesCount,added)));
            _store.FinishRun(run,"completed",source.Requests,added,$"{pages} pages");
            return new(added,pages,source.Requests);
        }
        catch(Exception ex) {_store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name);throw;}
        finally {IsPreparingInitial=false;}
    }
    public async Task<SyncResult> LoadMoreAsync(IBattleSource? source,string category,IReadOnlyList<Battle> filtered,CancellationToken token,Func<Task> pageSaved)
    {
        token.ThrowIfCancellationRequested();
        if(!HasMore(filtered,category)) return new(0,0,0);
        var window=_windows[category];
        if(LocalPageAvailable(Ordered(filtered,category),window))
        {window.Limit+=10;await pageSaved();return new(0,0,0);}
        if(source==null) throw new InvalidOperationException(Strings.LoginToLoad);
        var run=_store.BeginRun(Owner);var added=0;
        try
        {
            ProgressChanged?.Invoke(new(Reading(category),0,0,1,""));
            window.Limit+=10;added=await FetchOneAsync(source,category,token,pageSaved,commitCheckpoint:false);
            token.ThrowIfCancellationRequested();SaveWindow(category);
            ProgressChanged?.Invoke(new(Strings.MatchesSaved,1,1,0,Localization.Format(Strings.NewMatchesCount,added)));
            _store.FinishRun(run,IsComplete("") ? "completed" : "paged",source.Requests,added,"1 page");
            return new(added,1,source.Requests);
        }
        catch(Exception ex) {_store.FinishRun(run,ex is OperationCanceledException ? "cancelled" : "failed",source.Requests,added,ex.GetType().Name);throw;}
    }
    private async Task<int> FetchOneAsync(IBattleSource source,string category,CancellationToken token,Func<Task> pageSaved,bool commitCheckpoint=true)
    {
        var window=_windows[category];
        if(window.NextPage==1) _store.MarkCategoryPending(Owner,category);
        Diagnostics.Write("sync","fetch_page",page:window.NextPage,category:category);
        var page=await source.FetchPageAsync(Owner,category,window.NextPage,token);
        token.ThrowIfCancellationRequested();
        if(page.CurrentPage!=window.NextPage) throw new InvalidDataException(Strings.DuplicatePage);
        if(page.Battles.Any(b=>b.OwnerId!=Owner || (category.Length>0 ? b.Category!=category : !BattleCategory.All.Any(c=>c.Key==b.Category))))
            throw new InvalidDataException(Strings.MismatchedRecords);
        var signature=string.Join('|',page.Battles.Select(b=>b.ReplayId));
        if(page.Battles.Count>0 && !window.Signatures.Add(signature)) throw new InvalidDataException(Strings.DuplicateContent);
        if(window.NextPage==1) window.Head=page.Battles.FirstOrDefault()?.ReplayId;
        if(window.NextPage==1 && (category=="rank" || category.Length==0 && page.Battles.Any(b=>b.Category=="rank"))) RankHeadFetched=true;
        var added=_store.SavePage(page.Battles);
        Diagnostics.Write("sync","page_saved",page:page.CurrentPage,category:category);
        var tail=window.Prefix.LastOrDefault();
        var tailIndex=page.Battles.ToList().FindIndex(b=>b.ReplayId==tail);
        // Ignore newly inserted heads seen during older-page browsing until they connect
        // to the known prefix. They are saved, but cannot prove score adjacency.
        var extension=tailIndex>=0 ? page.Battles.Skip(tailIndex+1) :
            tail==null || page.Battles.All(b=>b.PlayedAt<=window.Oldest) ? page.Battles : [];
        window.Prefix=window.Prefix.Concat(extension.Select(b=>b.ReplayId)).Distinct().ToList();
        window.Initialized=true;window.NextPage++;window.TotalPages=page.TotalPages;
        window.ReachedBoundary=window.Boundary!=null && page.Battles.Any(b=>b.ReplayId==window.Boundary);
        if(window.ReachedBoundary && !window.BoundaryComplete)
        {
            var at=window.Prefix.IndexOf(window.Boundary!);
            if(at<0) throw new InvalidDataException(Strings.MismatchedRecords);
            window.Prefix=window.Prefix.Take(at).Concat(window.PreviousPrefix).Concat(window.Prefix.Skip(at)).Distinct().Take(page.TotalPages*10).ToList();
            // A shifted prefix can end halfway through a page. Re-read that page on scroll.
            window.NextPage=window.Prefix.Count/10+1;
            window.Signatures.Clear();
            window.Prepared=true;
        }
        SetOldest(window,category);
        if(category is "rank" or "") _store.SaveRankLinks(Owner,PrefixRows(window,category).Where(b=>b.Category=="rank").ToArray(),new());
        window.Complete=page.Battles.Count==0 || page.CurrentPage>=page.TotalPages || window.ReachedBoundary &&
            (window.BoundaryComplete || window.Prefix.Count>=page.TotalPages*10);
        if(window.Complete && commitCheckpoint) _store.CompleteCategory(Owner,category,window.Head);
        await pageSaved();return added;
    }
}
