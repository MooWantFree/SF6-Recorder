using Sf6Prototype;

public static class PagingChecks
{
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool value,string message) { if(!value) throw new Exception(message); count++; }
        var dir=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(dir,"paging.sqlite")); const string owner="1234567890";
        BattlePage Page(string category,int page)
        {
            var offset=Array.FindIndex(BattleCategory.All,c=>c.Key==category);
            return new(page,4,Enumerable.Range(0,10).Select(i=>new Battle
            {OwnerId=owner,Category=category,ReplayId=$"{category}-{page}-{i:D2}",PlayedAt=100000-((page-1)*10+i)*4-offset,CharacterId=i%2+1,Lp=20000-(page*10+i)}).ToList());
        }
        var source=new FakeSource(Page); var pager=new BattlePager(store,owner); var delivered=0;
        var observed=new List<string[]>();
        var initial=await pager.LoadInitialAsync(source,"",CancellationToken.None,()=>
        {
            delivered++; Check(store.Query(new(owner)).Count==delivered*10,"Initial pages become available as soon as each page is saved");
            observed.Add(pager.Visible(store.Query(new(owner)),"").Select(b=>b.ReplayId).ToArray());
            return Task.CompletedTask;
        });
        Check(initial.Pages==8 && source.Calls.All(c=>c.Page<=2) && store.Query(new(owner)).Count==80,"All mode loads only two pages of each of the four categories");
        Check(source.Calls.Take(4).All(c=>c.Page==1),"All category heads are discovered before requesting second pages");
        Check(observed.Take(3).All(rows=>rows.Length==0) && observed[3].Length>0,"Unseen category heads cannot reorder already visible matches");
        Check(observed.Zip(observed.Skip(1)).All(p=>p.Second.Take(p.First.Length).SequenceEqual(p.First)),"Every initial page extends the same ordered prefix, including late category heads");
        Check(pager.RankHeadFetched,"Current score capture is allowed only after reading the latest ranked page");
        Check(!pager.NeedsInitial("") && pager.HasMore(store.Query(new(owner)),""),"Initial loading ends while more pages remain available on demand");
        Check(store.Frontier(owner,"rank")==null && store.LastSuccessfulLoad(owner)!=null,"A two-page window is successful without advancing the full-sync checkpoint");
        var before=pager.Visible(store.Query(new(owner)),""); var calls=source.Requests;
        var next=await pager.LoadMoreAsync(source,"",store.Query(new(owner)),CancellationToken.None,()=>Task.CompletedTask);
        var after=pager.Visible(store.Query(new(owner)),"");
        Check(source.Requests==calls+1 && next.Pages==1,"One prefetch issues exactly one page request");
        Check(after.Take(before.Count).Select(b=>b.ReplayId).SequenceEqual(before.Select(b=>b.ReplayId)),"Merging category streams appends a chronological prefix without inserting above existing rows");
        Check(after.Zip(after.Skip(1)).All(pair=>pair.First.PlayedAt>=pair.Second.PlayedAt),"All-mode results remain sorted by time");
        var scoreRows=store.Query(new(owner,Category:"rank")); var points=BattleStore.Scores(scoreRows,links:store.ScoreLinks(owner));
        Check(points.Count(p=>p.LpDelta!=null)==scoreRows.Count-2,"Rank continuity crosses page boundaries separately for each character");
        store.SavePage([new Battle {OwnerId=owner,Category="rank",ReplayId="ancient-gap",PlayedAt=1,CharacterId=1,Lp=100}]);
        Check(BattleStore.Scores(store.Query(new(owner,Category:"rank")),links:store.ScoreLinks(owner)).Single(p=>p.ReplayId=="ancient-gap").LpDelta==null,"Old local records cannot create a fake score jump across an unrequested gap");
        for(var safety=0;pager.HasMore(store.Query(new(owner)),"") && safety<30;safety++)
            await pager.LoadMoreAsync(source,"",store.Query(new(owner)),CancellationToken.None,()=>Task.CompletedTask);
        Check(!pager.HasMore(store.Query(new(owner)),"") && source.Calls.Max(c=>c.Page)==4,"Infinite loading stops at each category's final page");
        Check(store.Frontier(owner,"rank")=="rank-1-00","The incremental checkpoint advances only when the category reaches its end");
        var cached=new BattlePager(store,owner); var previousRequests=source.Requests;
        await cached.LoadInitialAsync(source,"custom",CancellationToken.None,()=>Task.CompletedTask);
        Check(!cached.RankHeadFetched,"Loading a cached or non-ranked category cannot capture a new settlement estimate");
        await cached.LoadMoreAsync(null,"rank",store.Query(new(owner,Category:"rank")),CancellationToken.None,()=>Task.CompletedTask);
        Check(source.Requests==previousRequests && cached.Visible(store.Query(new(owner,Category:"rank")),"rank").Count==30,"Previously saved records are revealed one page at a time without network requests");
        cached.Reset("rank");
        await cached.LoadInitialAsync(source,"rank",CancellationToken.None,()=>Task.CompletedTask);
        Check(source.Requests==previousRequests+1,"Refreshing a fully saved category stops at its existing boundary");
        var partialStore=new BattleStore(Path.Combine(dir,"cancelled.sqlite")); var partial=new BattlePager(partialStore,owner);
        using var cancelled=new CancellationTokenSource(); var cancelSource=new FakeSource(Page);
        try { await partial.LoadInitialAsync(cancelSource,"",cancelled.Token,()=>{cancelled.Cancel();return Task.CompletedTask;}); }
        catch(OperationCanceledException) { }
        Check(cancelSource.Requests==1 && partialStore.Query(new(owner)).Count==10,"Navigation cancellation stops subsequent pages while retaining the page already shown");
        var reopenedPartial=new BattlePager(partialStore,owner);
        Check(reopenedPartial.Visible(partialStore.Query(new(owner)),"").Count==10,"Restarting offline still displays cached partial histories without waiting for network heads");
        var emptyStore=new BattleStore(Path.Combine(dir,"empty.sqlite")); var empty=new BattlePager(emptyStore,owner);
        var emptySource=new FakeSource((category,page)=>new(page,0,[]));
        await empty.LoadInitialAsync(emptySource,"",CancellationToken.None,()=>Task.CompletedTask);
        Check(emptySource.Requests==4 && !empty.HasMore([],""),"Empty categories finish after one request and cannot trigger an infinite preload loop");

        var tiedStore=new BattleStore(Path.Combine(dir,"tied.sqlite")); var tied=new BattlePager(tiedStore,owner);
        var tiedSource=new FakeSource((category,page)=>new(page,3,(page switch
        {
            1 => new[]{("head",300L),("z",200L)},
            2 => new[]{("m",200L)},
            _ => new[]{("a",200L),("tail",100L)}
        }).Select(t=>new Battle {OwnerId=owner,Category=category,ReplayId=$"{t.Item1}-{category}",PlayedAt=t.Item2}).ToList()));
        var tieSnapshots=new List<string[]>();
        Task ObserveTies() { tieSnapshots.Add(tied.Visible(tiedStore.Query(new(owner)),"").Select(b=>b.ReplayId).ToArray()); return Task.CompletedTask; }
        await tied.LoadInitialAsync(tiedSource,"",CancellationToken.None,ObserveTies);
        Check(tied.Visible(tiedStore.Query(new(owner)),"").All(b=>b.PlayedAt==300),"Incomplete timestamp groups stay hidden across page boundaries");
        for(var safety=0;tied.HasMore(tiedStore.Query(new(owner)),"") && safety<10;safety++)
            await tied.LoadMoreAsync(tiedSource,"",tiedStore.Query(new(owner)),CancellationToken.None,ObserveTies);
        Check(tieSnapshots.Zip(tieSnapshots.Skip(1)).All(p=>p.Second.Take(p.First.Length).SequenceEqual(p.First)),"Tied timestamps cannot insert lexically earlier replay IDs above previously visible matches");
        Check(tied.Visible(tiedStore.Query(new(owner)),"").Count==20 && tiedSource.Requests==12,"Boundary matches become visible once complete and no matches are lost");

        var gapStore=new BattleStore(Path.Combine(dir,"cached-gap.sqlite")); var gap=new BattlePager(gapStore,owner);
        gapStore.SavePage([new Battle {OwnerId=owner,Category="rank",ReplayId="old-cache",PlayedAt=1}]);
        var retained=gap.Visible(gapStore.Query(new(owner)),"rank");
        var gapSource=new FakeSource(Page);
        await gap.LoadInitialAsync(gapSource,"rank",CancellationToken.None,()=>Task.CompletedTask);
        Check(gap.Visible(gapStore.Query(new(owner)),"rank").All(b=>b.ReplayId!="old-cache") && gap.MoreNeedsNetwork(gapStore.Query(new(owner)),"rank"),"Older cached games cannot bypass the gap after a partially fetched window");
        Check(gap.Visible(gapStore.Query(new(owner)),"rank",retained:retained).Any(b=>b.ReplayId=="old-cache"),"Refreshing retains older rows already on screen instead of retracting them");
        Check(gap.Visible([],"rank",retained:retained).Count==0,"Retention cannot bring back a row excluded by the current filter");
        Check(gap.Visible(gapStore.Query(new(owner)),"rank",retained:retained.Select(b=>b with {OwnerId="9999999999"}).ToList()).All(b=>b.ReplayId!="old-cache"),"Retained replay IDs from another player never leak into the current window");
        await gap.LoadMoreAsync(gapSource,"rank",gapStore.Query(new(owner)),CancellationToken.None,()=>Task.CompletedTask);
        Check(gapSource.Calls.Last().Page==3 && gap.Visible(gapStore.Query(new(owner)),"rank").All(b=>b.ReplayId!="old-cache"),"Scrolling fills the next remote page before revealing disconnected old cache");
        return count;
    }
}
