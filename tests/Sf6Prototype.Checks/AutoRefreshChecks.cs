using Sf6Prototype;

public static class AutoRefreshChecks
{
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        const string owner=PagingChecks.Owner;
        var games=PagingChecks.Games();var store=new BattleStore(Path.Combine(root,"sync.sqlite"));
        store.SavePage([games[0] with {OwnerId="9999999999",ReplayId="other"}]);
        var source=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        var result=await AccountRefresh.RunAsync(store,source,owner,CancellationToken.None);
        Check(source.Calls.Take(10).All(c=>c.Category=="") && source.Requests==50,"A first account refresh exhausts All before filling each category's available window");
        Check(store.Query(new(owner)).Count==400 && store.Query(new("9999999999")).Count==1 && result.Pager.IsComplete(""),"All categories merge into one complete archive without changing another player's records");
        Check(store.Frontier(owner,"all-sync")==games[0].ReplayId,"Only a full account synchronization establishes the global fast-path boundary");
        var unchanged=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await new BattlePager(store,owner).LoadAccountAsync(unchanged,CancellationToken.None,()=>Task.CompletedTask,()=>"rank");
        Check(unchanged.Calls.SequenceEqual(new[]{("",1)}) && store.Frontier(owner,"all-sync")==games[0].ReplayId,"An unchanged first replay hits the previous head checkpoint immediately, even while viewing another category");
        var few=Enumerable.Range(0,9).Select(i=>games[i] with {ReplayId=$"F{i:D3}",PlayedAt=150000-i}).Concat(games).ToList();
        var withinPage=new FakeSource((c,p)=>PagingChecks.Page(few,c,p));
        await AccountRefresh.RunAsync(store,withinPage,owner,CancellationToken.None);
        Check(withinPage.Calls.SequenceEqual(new[]{("",1)}) && store.Frontier(owner,"all-sync")==few[0].ReplayId,"A boundary later in the first page stops there and advances to that page's first replay, never its last replay");
        var repeatFew=new FakeSource((c,p)=>PagingChecks.Page(few,c,p));
        await AccountRefresh.RunAsync(store,repeatFew,owner,CancellationToken.None);
        Check(repeatFew.Calls.SequenceEqual(new[]{("",1)}),"The next refresh reuses the newly advanced head checkpoint");
        var fresh=Enumerable.Range(0,25).Select(i=>games[i] with {ReplayId=$"N{i:D3}",PlayedAt=200000-i}).Concat(few).ToList();
        var incremental=new FakeSource((c,p)=>PagingChecks.Page(fresh,c,p));
        await AccountRefresh.RunAsync(store,incremental,owner,CancellationToken.None);
        Check(incremental.Calls.SequenceEqual(new[]{("",1),("",2),("",3)}) && store.Query(new(owner)).Count==434 && store.Frontier(owner,"all-sync")==fresh[0].ReplayId,"A boundary on the third page needs three requests, then saves the first page's head as the next checkpoint");
        var newer=Enumerable.Range(0,21).Select(i=>games[i] with {ReplayId=$"X{i:D3}",PlayedAt=300000-i}).Concat(fresh).ToList();
        using(var cancelled=new CancellationTokenSource())
        {
            var interrupted=new BattlePager(store,owner);var partial=new FakeSource((c,p)=>PagingChecks.Page(newer,c,p));
            try {await interrupted.LoadAccountAsync(partial,cancelled.Token,()=>{cancelled.Cancel();return Task.CompletedTask;});throw new Exception("Expected cancellation");}catch(OperationCanceledException){count++;}
        }
        Check(store.Frontier(owner,"all-sync")==fresh[0].ReplayId && !new BattlePager(store,owner).IsComplete(""),"An interrupted refresh keeps its prior checkpoint and its incomplete marker across restart");
        var resumed=new FakeSource((c,p)=>PagingChecks.Page(newer,c,p));
        await AccountRefresh.RunAsync(store,resumed,owner,CancellationToken.None);
        Check(resumed.Requests==3 && store.Query(new(owner)).Count==455,"An already stored partial head cannot hide missing middle records on resume");
        var failureStore=new BattleStore(Path.Combine(root,"fallback.sqlite"));
        var failure=new FakeSource((c,p)=>c=="rank" ? throw new IOException("Interrupted fallback") : PagingChecks.Page(games,c,p));
        try {await AccountRefresh.RunAsync(failureStore,failure,owner,CancellationToken.None);throw new Exception("Expected failure");}catch(IOException){count++;}
        Check(failureStore.Frontier(owner,"")!=null && failureStore.Frontier(owner,"all-sync")==null,"Finishing All alone cannot advance the full-account boundary when fallback fails");
        var retry=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await AccountRefresh.RunAsync(failureStore,retry,owner,CancellationToken.None);
        Check(retry.Calls.Any(c=>c.Category=="hub") && failureStore.Query(new(owner)).Count==400,"Retrying incomplete fallback still collects the remaining categories");
        var priorityStore=new BattleStore(Path.Combine(root,"priority.sqlite"));var priorityPager=new BattlePager(priorityStore,owner);
        var priority=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));string? selected=null;
        await priorityPager.LoadAccountAsync(priority,CancellationToken.None,()=>{selected="rank";return Task.CompletedTask;},()=>selected);
        Check(priority.Calls.Take(4).SequenceEqual(new[]{("",1),("rank",1),("rank",2),("",2)}),"A same-user category request is served between pages without cancelling the account refresh");
        Check(priority.Calls.Count(c=>c==("rank",1))==1 && priorityStore.Query(new(owner)).Count==400,"Fallback resumes a prioritized category without fetching its first page again");
        var gap=Enumerable.Range(0,110).Select(i=>games[i] with {ReplayId=$"G{i:D3}",PlayedAt=400000-i}).Concat(games).ToList();
        using(var stop=new CancellationTokenSource())
        {
            var atFallback=new FakeSource((c,p)=>PagingChecks.Page(gap,c,p));
            try {await new BattlePager(priorityStore,owner).LoadAccountAsync(atFallback,stop.Token,()=>{if(atFallback.Requests==10)stop.Cancel();return Task.CompletedTask;});throw new Exception("Expected cancellation");}catch(OperationCanceledException){count++;}
        }
        Check(!new BattlePager(priorityStore,owner).IsComplete("") && BattleCategory.All.All(c=>priorityStore.IsCategoryPending(owner,c.Key)),"Stopping exactly at the All-to-category handoff preserves incomplete coverage across restart");
        var legacyStore=new BattleStore(Path.Combine(root,"legacy.sqlite"));legacyStore.SavePage(games);
        foreach(var category in BattleCategory.All)
        {
            legacyStore.CompleteCategory(owner,category.Key,games.First(b=>b.Category==category.Key).ReplayId);
            legacyStore.MarkCategoryPending(owner,category.Key);
        }
        var legacy=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await AccountRefresh.RunAsync(legacyStore,legacy,owner,CancellationToken.None);
        Check(legacy.Calls.SequenceEqual(new[]{("",1),("rank",1),("casual",1),("custom",1),("hub",1)}) && new BattlePager(legacyStore,owner).IsComplete(""),"An upgrade verifies existing category checkpoints once without searching later All pages for a missing account checkpoint");
        var afterUpgrade=new FakeSource((c,p)=>PagingChecks.Page(games,c,p));
        await AccountRefresh.RunAsync(legacyStore,afterUpgrade,owner,CancellationToken.None);
        Check(afterUpgrade.Calls.SequenceEqual(new[]{("",1)}) && legacyStore.Frontier(owner,"all-sync")==games[0].ReplayId,"After migration, the first All replay is the checkpoint for the very next refresh");
        return count;
    }
}
