using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Sf6Prototype;

public static class AutoRefreshChecks
{
    private sealed class Source(Func<string,string,int,BattlePage> page) : IBattleSource
    {
        public readonly List<(string Owner,string Category,int Page)> Calls=[];
        public int Requests=>Calls.Count;
        public Task<BattlePage> FetchPageAsync(string owner,string category,int number,CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add((owner,category,number));
            return Task.FromResult(page(owner,category,number));
        }
    }
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); count++; }
        const string account="1234567890", other="9999999999";
        var directory=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(directory,"auto.sqlite"));
        Battle Game(string owner,string category,string id,long time)=>new() {OwnerId=owner,Category=category,ReplayId=id,PlayedAt=time};
        var old=BattleCategory.All.Select(c=>Game(account,c.Key,"old-"+c.Key,100)).ToList();
        var opponents=new[]{Game(other,"rank","opponent-only",150)};
        store.SavePage(old); store.SavePage(opponents);
        foreach(var category in BattleCategory.All) store.CompleteCategory(account,category.Key,"old-"+category.Key);
        var viewed=new BattlePager(store,other);
        var beforeOther=viewed.Visible(store.Query(new(other)),"");
        var source=new Source((owner,category,page)=>new(page,8,[Game(owner,category,"new-"+category,200),old.Single(b=>b.Category==category)]));
        var refresh=await AccountRefresh.RunAsync(store,source,account,CancellationToken.None);
        Check(source.Calls.Count==4 && source.Calls.All(c=>c.Owner==account && c.Page==1),"Automatic refresh targets only the signed-in account and stops at each saved boundary");
        Check(refresh.Sync.Inserted==4 && store.Query(new(account)).Count==8,"All four categories merge and persist before presentation");
        Check(viewed.Visible(store.Query(new(other)),"").SequenceEqual(beforeOther),"Refreshing the account leaves another player's pager and cached rows untouched");
        var newRows=refresh.Pager.Visible(store.Query(new(account)),"",retained:old,prependOnly:true);
        Check(newRows.Take(4).All(b=>b.PlayedAt==200) && newRows.Skip(4).Select(b=>b.ReplayId).SequenceEqual(old.OrderBy(b=>b.ReplayId,StringComparer.Ordinal).Select(b=>b.ReplayId)),"New heads prepend without changing existing match order");
        var retained=old.OrderBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
        var rows=new ObservableCollection<Battle>(retained); var selected=rows[2];
        var changes=new List<NotifyCollectionChangedEventArgs>(); rows.CollectionChanged+=(_,e)=>changes.Add(e);
        IncrementalList.Apply(rows,newRows,b=>b.ReplayId,(_,_)=>{});
        Check(ReferenceEquals(rows[6],selected) && changes.All(e=>e.Action==NotifyCollectionChangedAction.Add),"Prepending keeps selected row identity and emits only additions, not moves or resets");
        store.SavePage([Game(account,"rank","older-gap",50)]);
        var headOnly=refresh.Pager.Visible(store.Query(new(account)),"",retained:newRows,prependOnly:true);
        Check(headOnly.All(b=>b.ReplayId!="older-gap"),"Background refresh does not insert backfilled old games into the user's current list");
        Check(refresh.Pager.Visible(store.Query(new(account)),"",retained:newRows).Any(b=>b.ReplayId=="older-gap"),"Scrolling or manual refresh can still reveal older saved games");
        var repeated=await AccountRefresh.RunAsync(store,source,account,CancellationToken.None);
        Check(repeated.Sync.Inserted==0 && store.Query(new(account)).Count==9,"Repeating automatic refresh does not duplicate stored battles");

        var fresh=new BattleStore(Path.Combine(directory,"fresh.sqlite"));
        var longSource=new Source((owner,category,page)=>new(page,10,Enumerable.Range(0,10).Select(i=>Game(owner,category,$"{category}-{page}-{i}",1000-page*10-i)).ToList()));
        await AccountRefresh.RunAsync(fresh,longSource,account,CancellationToken.None);
        Check(longSource.Calls.Count==8 && longSource.Calls.All(c=>c.Page<=2),"An account without a boundary uses only the existing two-page-per-category budget");
        using var cancellation=new CancellationTokenSource();
        var cancelSource=new Source((owner,category,page)=>{cancellation.Cancel();return new(page,1,[Game(owner,category,"cancelled",300)]);});
        try { await AccountRefresh.RunAsync(store,cancelSource,account,cancellation.Token); throw new Exception("Cancellation was ignored"); }
        catch(OperationCanceledException) { count++; }
        Check(cancelSource.Calls.Count==1 && store.Query(new(account)).All(b=>b.ReplayId!="cancelled"),"Navigation cancellation stops later account requests and does not commit an abandoned response");
        return count;
    }
}
