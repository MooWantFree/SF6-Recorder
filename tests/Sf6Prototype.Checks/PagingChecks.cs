using Sf6Prototype;

public static class PagingChecks
{
    internal const string Owner="1234567890";
    internal static List<Battle> Games(int count=400) => Enumerable.Range(0,count).Select(i=>new Battle
    {OwnerId=Owner,ReplayId=$"R{i:D4}",Category=BattleCategory.All[i%4].Key,PlayedAt=100000-i,CharacterId=i/4%2+1,Lp=20000-i}).ToList();
    internal static BattlePage Page(IReadOnlyList<Battle> games,string category,int page)
    {
        var rows=games.Where(b=>category.Length==0 || b.Category==category).Take(100).ToArray();
        return new(page,(rows.Length+9)/10,rows.Skip((page-1)*10).Take(10).ToArray());
    }
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value) throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"paging.sqlite"));var games=Games();
        var source=new FakeSource((c,p)=>Page(games,c,p));var pager=new BattlePager(store,Owner);
        var snapshots=new List<int>();
        await pager.LoadInitialAsync(source,"",CancellationToken.None,()=>{snapshots.Add(pager.Visible(store.Query(new(Owner)),"").Count);return Task.CompletedTask;});
        Check(source.Calls.SequenceEqual(new[]{("",1),("",2)}) && snapshots.SequenceEqual([10,20]),"All loads exactly two official mixed pages and exposes each saved page immediately");
        Check(store.Query(new(Owner)).Count==20 && BattleCategory.All.All(c=>store.Query(new(Owner,c.Key)).Count==5),"All-page battles retain their actual category");
        Check(!pager.IsComplete("") && !pager.IsComplete("rank") && !pager.NeedsInitial(""),"A prepared first screen is not a complete archive");
        Check(pager.RankHeadFetched,"Ranked matches in the all-stream head permit a fresh score snapshot");
        var before=pager.Visible(store.Query(new(Owner)),"").Select(b=>b.ReplayId).ToArray();
        await pager.LoadMoreAsync(source,"",store.Query(new(Owner)),CancellationToken.None,()=>Task.CompletedTask);
        Check(source.Calls.Last()==("",3) && pager.Visible(store.Query(new(Owner)),"").Take(20).Select(b=>b.ReplayId).SequenceEqual(before),"Scrolling All fetches its next page without touching other endpoints");
        var calls=source.Requests;
        await pager.LoadInitialAsync(source,"rank",CancellationToken.None,()=>Task.CompletedTask);
        Check(source.Requests==calls+2 && pager.Visible(store.Query(new(Owner,"rank")),"rank").Count==20,"Selecting a category prepares twenty matches only from that endpoint");
        Check(store.Query(new(Owner)).Select(b=>b.ReplayId).Distinct().Count()==store.Query(new(Owner)).Count,"Overlapping all and category pages never duplicate matches");
        var ranked=store.Query(new(Owner,"rank"));
        Check(BattleStore.Scores(ranked,links:store.ScoreLinks(Owner)).Count(p=>p.LpDelta!=null)==ranked.Count-2,"Mixed all pages and category pages preserve per-character ranked continuity");
        while(pager.HasMore(store.Query(new(Owner)),"")) await pager.LoadMoreAsync(source,"",store.Query(new(Owner)),CancellationToken.None,()=>Task.CompletedTask);
        Check(source.Calls.Where(c=>c.Category=="").Max(c=>c.Page)==10 && !pager.IsComplete(""),"All paging stops at the official window while uncollected categories remain marked incomplete");
        while(pager.HasMore(store.Query(new(Owner,"rank")),"rank")) await pager.LoadMoreAsync(source,"rank",store.Query(new(Owner,"rank")),CancellationToken.None,()=>Task.CompletedTask);
        Check(pager.IsComplete("rank"),"A category becomes complete only at its boundary or last page");
        var cached=new BattlePager(store,Owner);calls=source.Requests;
        await cached.LoadMoreAsync(null,"rank",store.Query(new(Owner,"rank")),CancellationToken.None,()=>Task.CompletedTask);
        Check(cached.Visible(store.Query(new(Owner,"rank")),"rank").Count==30 && source.Requests==calls,"Already saved records page locally without a request");
        cached.Reset("rank");
        await cached.LoadInitialAsync(source,"rank",CancellationToken.None,()=>Task.CompletedTask);
        Check(source.Requests==calls+1 && cached.IsComplete("rank"),"A refreshed complete category stops at its reliable saved boundary");
        cached.Reset("rank");
        var failed=new FakeSource((c,p)=>throw new IOException("Synthetic failure"));
        try {await cached.LoadInitialAsync(failed,"rank",CancellationToken.None,()=>Task.CompletedTask);throw new Exception("Expected failure");}catch(IOException){count++;}
        Check(!new BattlePager(store,Owner).IsComplete("rank") && store.Frontier(Owner,"rank")!=null,"Restarting after failure preserves both the incomplete marker and the old reliable boundary");
        var partialStore=new BattleStore(Path.Combine(root,"partial.sqlite"));var partial=new BattlePager(partialStore,Owner);
        using var stop=new CancellationTokenSource();
        try {await partial.LoadInitialAsync(source,"",stop.Token,()=>{stop.Cancel();return Task.CompletedTask;});throw new Exception("Expected cancellation");}catch(OperationCanceledException){count++;}
        Check(partialStore.Query(new(Owner)).Count==10 && partial.NeedsInitial(""),"An interrupted first screen retains records and still needs its missing second page");
        var emptyStore=new BattleStore(Path.Combine(root,"empty.sqlite"));var empty=new BattlePager(emptyStore,Owner);var emptySource=new FakeSource((c,p)=>new(p,0,[]));
        await empty.LoadInitialAsync(emptySource,"",CancellationToken.None,()=>Task.CompletedTask);
        Check(emptySource.Requests==1 && !empty.HasMore([],""),"An empty all stream ends after one request");
        var gapStore=new BattleStore(Path.Combine(root,"gap.sqlite"));gapStore.SavePage([games[0] with {ReplayId="old-cache",PlayedAt=1}]);
        var gap=new BattlePager(gapStore,Owner);var retained=gap.Visible(gapStore.Query(new(Owner)),"");
        await gap.LoadInitialAsync(source,"",CancellationToken.None,()=>Task.CompletedTask);
        Check(gap.Visible(gapStore.Query(new(Owner)),"").All(b=>b.ReplayId!="old-cache") && gap.Visible(gapStore.Query(new(Owner)),"",retained:retained).Any(b=>b.ReplayId=="old-cache"),"A remote gap cannot expose disconnected old records, while an existing viewport may retain them");
        var ties=games.Take(30).Select(b=>b with {PlayedAt=1234}).ToList();var tiedStore=new BattleStore(Path.Combine(root,"ties.sqlite"));var tied=new BattlePager(tiedStore,Owner);
        var tiedSource=new FakeSource((c,p)=>Page(ties,c,p));
        await tied.LoadInitialAsync(tiedSource,"",CancellationToken.None,()=>Task.CompletedTask);
        await tied.LoadMoreAsync(tiedSource,"",tiedStore.Query(new(Owner)),CancellationToken.None,()=>Task.CompletedTask);
        Check(tied.Visible(tiedStore.Query(new(Owner)),"").Count==30,"Equal-timestamp page boundaries neither lose nor duplicate records");
        return count;
    }
}
