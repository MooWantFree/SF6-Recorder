using Sf6Prototype;

public static class IncrementalBrowsingChecks
{
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        const string owner=PagingChecks.Owner;
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"browsing.sqlite"));var games=PagingChecks.Games();
        var pager=new BattlePager(store,owner);var source=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        Task Saved()=>Task.CompletedTask;
        await pager.LoadInitialAsync(source,"",CancellationToken.None,Saved);
        Check(source.Calls.SequenceEqual(new[]{("",1),("",2)}) && store.Prefix(owner,"") is {NextPage:3,ReplayIds.Count:20},"The first twenty matches save a durable head and a separate continuation cursor");
        Check(store.Prefix(owner,"")!.ReplayIds[0]==games[0].ReplayId && !store.HasCategorySnapshot(owner,"") && !pager.IsComplete(""),"A first-screen checkpoint never claims complete history");
        pager=new BattlePager(store,owner);
        Check(!pager.NeedsInitial("") && pager.Visible(store.Query(new(owner)),"").Count==20,"Reopening a prepared player reuses the downloaded first screen");
        pager.Reset("");var unchanged=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await pager.LoadInitialAsync(unchanged,"",CancellationToken.None,Saved);
        Check(unchanged.Calls.SequenceEqual(new[]{("",1)}) && !pager.IsComplete("") && pager.HasMore(store.Query(new(owner)),""),"An unchanged head stops immediately while older history remains available and incomplete");
        await pager.LoadMoreAsync(unchanged,"",store.Query(new(owner)),CancellationToken.None,Saved);
        Check(unchanged.Calls.Last()==("",3) && store.Query(new(owner)).Count==30,"Scrolling after an unchanged refresh resumes at page three without losing the second page");
        var nine=Enumerable.Range(0,9).Select(i=>games[i] with {ReplayId=$"N{i:D3}",PlayedAt=200000-i}).Concat(games).ToList();
        pager.Reset("");var newHead=new FakeSource((c,p)=>PagingChecks.Page(nine,c,p));
        await pager.LoadInitialAsync(newHead,"",CancellationToken.None,Saved);
        Check(newHead.Requests==1 && store.Prefix(owner,"") is {NextPage:4,ReplayIds.Count:39} && store.Prefix(owner,"")!.ReplayIds[0]==nine[0].ReplayId,"Nine additions merge with the old contiguous prefix and advance the checkpoint to the new first replay");
        pager=new BattlePager(store,owner);
        while(!pager.MoreNeedsNetwork(store.Query(new(owner)),"")) await pager.LoadMoreAsync(null,"",store.Query(new(owner)),CancellationToken.None,Saved);
        await pager.LoadMoreAsync(newHead,"",store.Query(new(owner)),CancellationToken.None,Saved);
        Check(newHead.Calls.Last()==("",4) && store.Query(new(owner)).Count==40,"A prefix shifted by nine matches resumes within its partial page and includes the missing older match");
        var ranked=store.Query(new(owner,"rank"));
        Check(BattleStore.Scores(ranked,links:store.ScoreLinks(owner)).Count(p=>p.LpDelta!=null)==ranked.Count-2,"Overlapping continuation pages preserve ranked score adjacency without backwards links");
        var newer=Enumerable.Range(0,25).Select(i=>games[i] with {ReplayId=$"X{i:D3}",PlayedAt=300000-i}).Concat(nine).ToList();
        pager.Reset("");var interrupted=new FakeSource((c,p)=>PagingChecks.Page(newer,c,p));
        using(var stop=new CancellationTokenSource())
        {
            try {await pager.LoadInitialAsync(interrupted,"",stop.Token,()=>{if(interrupted.Requests==2)stop.Cancel();return Task.CompletedTask;});throw new Exception("Expected cancellation");}
            catch(OperationCanceledException){count++;}
        }
        Check(store.Prefix(owner,"")!.ReplayIds[0]==nine[0].ReplayId,"An interrupted refresh keeps the previous successful head instead of trusting its partial new prefix");
        pager=new BattlePager(store,owner);pager.Reset("");var resumed=new FakeSource((c,p)=>PagingChecks.Page(newer,c,p));
        await pager.LoadInitialAsync(resumed,"",CancellationToken.None,Saved);
        Check(resumed.Calls.SequenceEqual(new[]{("",1),("",2),("",3)}) && store.Query(new(owner)).Count==65 && store.Prefix(owner,"")!.ReplayIds[0]==newer[0].ReplayId,"A refresh with more than twenty additions crosses page two and fills the cancelled gap before advancing its head");
        pager.Reset("");var failed=new FakeSource((c,p)=>throw new IOException("Synthetic failure"));
        try {await pager.LoadInitialAsync(failed,"",CancellationToken.None,Saved);throw new Exception("Expected failure");}catch(IOException){count++;}
        Check(store.Prefix(owner,"")!.ReplayIds[0]==newer[0].ReplayId,"A failed request does not replace the saved checkpoint");
        var lost=Enumerable.Range(0,110).Select(i=>games[i] with {ReplayId=$"L{i:D3}",PlayedAt=400000-i}).Concat(newer).ToList();
        pager.Reset("");var missingBoundary=new FakeSource((c,p)=>PagingChecks.Page(lost,c,p));
        await pager.LoadInitialAsync(missingBoundary,"",CancellationToken.None,Saved);
        Check(missingBoundary.Requests==10 && store.HasCategorySnapshot(owner,"") && store.Frontier(owner,"")==lost[0].ReplayId && store.Prefix(owner,"")==null,"An expired checkpoint scans to the official end and only then marks that stream complete");
        var categoryStore=new BattleStore(Path.Combine(root,"category.sqlite"));var categoryPager=new BattlePager(categoryStore,owner);
        var rankSource=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await categoryPager.LoadInitialAsync(rankSource,"rank",CancellationToken.None,Saved);
        categoryPager.Reset("rank");var rankRefresh=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await categoryPager.LoadScoreRangeAsync(rankRefresh,new(1,20),CancellationToken.None,Saved,forceHead:true);
        Check(rankRefresh.Calls.First()==("rank",1) && rankRefresh.Calls.Skip(1).All(c=>c.Page>=3),"Refreshing a partial ranked stream uses its head checkpoint before extending the requested score range");
        Check(!categoryPager.IsComplete("rank") && store.Prefix(owner,"rank")==null,"Category checkpoints and completeness remain isolated by owner and stream");
        categoryPager.Reset("rank");var sameRank=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await categoryPager.LoadScoreRangeAsync(sameRank,new(1,20),CancellationToken.None,Saved,forceHead:true);
        Check(sameRank.Calls.SequenceEqual(new[]{("rank",1)}),"A previously satisfied score range refreshes only to its saved head");
        var singleStore=new BattleStore(Path.Combine(root,"single-page.sqlite"));var singlePager=new BattlePager(singleStore,owner);
        var shortSource=new FakeSource((c,p)=>PagingChecks.Page(games.Take(6).ToList(),c,p));
        await singlePager.LoadInitialAsync(shortSource,"rank",CancellationToken.None,Saved);
        Check(singlePager.IsComplete("rank") && shortSource.Requests==1 && singleStore.Prefix(owner,"rank")==null,"A genuinely exhausted short category is complete rather than a partial first-screen checkpoint");
        return count;
    }
}
