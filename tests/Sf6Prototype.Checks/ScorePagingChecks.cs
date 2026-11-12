using Sf6Prototype;

public static class ScorePagingChecks
{
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); count++; }
        var dir=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        const string owner="3396336472";
        BattleStore Store(string name)=>new(Path.Combine(dir,name+".sqlite"));
        BattlePage Page(int page,int total=12,bool mixed=false,int? size=null)=>new(page,total,Enumerable.Range(0,size ?? 10).Select(i=>new Battle
        {OwnerId=owner,Category="rank",ReplayId=$"P{page}-{i}",PlayedAt=10000-page*10-i,CharacterId=mixed && i%2==1 ? 2 : 1,Lp=20000-page*10-i,Mr=0}).ToList());
        var token=CancellationToken.None;
        var store=Store("ranges"); var pager=new BattlePager(store,owner); var source=new Source(p=>Page(p));
        var snapshots=new List<int>();
        Task Saved() { snapshots.Add(store.Query(new(owner,Category:"rank")).Count); return Task.CompletedTask; }
        await pager.LoadScoreRangeAsync(source,new(1,20),token,Saved);
        Check(source.Requests==2 && snapshots.SequenceEqual([10,20]),"Twenty chart matches load two pages, showing each saved page before the next request");
        Check(!pager.NeedsScoreRange(new(1,20)) && pager.NeedsScoreRange(new(1,100)),"The range decision uses the selected character's saved matches");
        await pager.LoadScoreRangeAsync(source,new(1,20),token,Saved);
        Check(source.Requests==2,"Reopening an already satisfied range sends no request");
        await pager.LoadScoreRangeAsync(source,new(1,100),token,Saved);
        Check(source.Requests==10 && store.Query(new(owner,Category:"rank")).Count==100 && source.Pages.SequenceEqual(Enumerable.Range(1,10)),"Growing from 20 to 100 continues at page 3 and stops at page 10 without refetching earlier pages");
        await pager.LoadScoreRangeAsync(source,new(1,50),token,Saved);
        Check(source.Requests==10,"Shrinking a chart range uses local data only");
        await pager.LoadScoreRangeAsync(source,new(1,0),token,Saved);
        Check(source.Requests==12 && !pager.NeedsScoreRange(new(1,0)),"All reads only remaining pages and stops at the official end");
        await pager.LoadScoreRangeAsync(source,new(1,0),token,Saved);
        Check(source.Requests==12,"An already complete all-records chart sends no request");
        pager.Reset("rank");
        await pager.LoadScoreRangeAsync(source,new(1,20),token,Saved,forceHead:true);
        Check(source.Requests==13 && source.Pages.Last()==1 && pager.RankHeadFetched,"Explicit refresh still fetches the latest head even when cached rows satisfy the chart");

        var mixedStore=Store("mixed"); var mixedPager=new BattlePager(mixedStore,owner); var mixedSource=new Source(p=>Page(p,mixed:true));
        mixedStore.SavePage(Enumerable.Range(0,100).Select(i=>new Battle {OwnerId=owner,ReplayId="casual"+i,Category="casual",CharacterId=1}).ToArray());
        await mixedPager.LoadScoreRangeAsync(mixedSource,new(1,20),token,()=>Task.CompletedTask);
        Check(mixedSource.Requests==4 && mixedStore.Query(new(owner,Category:"rank")).Count(b=>b.CharacterId==1)==20,"Other characters and non-ranked games cannot satisfy the selected character's range");
        await mixedPager.LoadScoreRangeAsync(mixedSource,new(2,20),token,()=>Task.CompletedTask);
        Check(mixedSource.Requests==4,"Switching to another character already present in the downloaded pages reuses the same local data");

        var shortStore=Store("short"); var shortPager=new BattlePager(shortStore,owner); var shortSource=new Source(p=>Page(p,2,size:p==2 ? 3 : 10));
        await shortPager.LoadScoreRangeAsync(shortSource,new(1,100),token,()=>Task.CompletedTask);
        Check(shortSource.Requests==2 && shortStore.Query(new(owner)).Count==13 && !shortPager.NeedsScoreRange(new(1,100)),"Fewer than the requested matches stops at the official end instead of loading forever");
        var empty=new BattlePager(Store("empty"),owner); var emptySource=new Source(p=>new(p,0,[]));
        await empty.LoadScoreRangeAsync(emptySource,new(null,20),token,()=>Task.CompletedTask);
        Check(emptySource.Requests==1 && !empty.NeedsScoreRange(new(null,20)),"An empty player resolves after one page");

        var cancelStore=Store("cancel"); var cancelPager=new BattlePager(cancelStore,owner); var cancelSource=new Source(p=>Page(p));
        using(var cancel=new CancellationTokenSource())
        {
            try { await cancelPager.LoadScoreRangeAsync(cancelSource,new(1,100),cancel.Token,()=> {cancel.Cancel();return Task.CompletedTask;}); throw new Exception("Expected cancellation"); }
            catch(OperationCanceledException) { count++; }
        }
        Check(cancelSource.Requests==1 && cancelStore.Query(new(owner)).Count==10,"Changing options or navigating cancels subsequent requests while retaining saved records");
        await cancelPager.LoadScoreRangeAsync(cancelSource,new(1,20),token,()=>Task.CompletedTask);
        Check(cancelSource.Pages.SequenceEqual([1,2]),"Resuming a cancelled chart range continues from the next page");

        var cachedStore=Store("local"); cachedStore.SavePage(Page(1).Battles.Concat(Page(2).Battles).ToArray());
        var cachedPager=new BattlePager(cachedStore,owner); var cachedSource=new Source(_=>throw new Exception("Unexpected network request"));
        await cachedPager.LoadScoreRangeAsync(cachedSource,new(1,20),token,()=>Task.CompletedTask);
        Check(cachedSource.Requests==0,"Sufficient local rows are used even without a completed remote scan");
        var otherPager=new BattlePager(cachedStore,"1234567890");
        Check(otherPager.NeedsScoreRange(new(1,20)),"Another player's cached records never satisfy the viewed player's chart");

        var chooseStore=Store("choose"); var choosePager=new BattlePager(chooseStore,owner); var chooseSource=new Source(p=>Page(p,5,mixed:true));
        await choosePager.LoadScoreRangeAsync(chooseSource,new(null,20),token,()=>Task.CompletedTask);
        Check(chooseSource.Requests==4,"A chart without an initial character resolves the first available character and fills its requested range");
        var repeated=new BattlePager(Store("repeated"),owner); var repeatSource=new Source(p=>Page(1) with {CurrentPage=p});
        try { await repeated.LoadScoreRangeAsync(repeatSource,new(1,100),token,()=>Task.CompletedTask); throw new Exception("Expected repeated-page rejection"); }
        catch(InvalidDataException) { Check(repeatSource.Requests==2,"Repeated page contents stop chart loading rather than looping"); }
        var datedStore=Store("dates");var datedPager=new BattlePager(datedStore,owner);var datedSource=new Source(p=>Page(p));
        var dateRange=new ScoreRange(1,20,From:9950,Until:9975);
        await datedPager.LoadScoreRangeAsync(datedSource,dateRange,token,()=>Task.CompletedTask);
        Check(datedSource.Requests==4 && dateRange.HasEnough(datedStore.Query(new(owner))),"A historical range skips newer dates and fetches until twenty matching matches exist");
        await datedPager.LoadScoreRangeAsync(datedSource,dateRange with {Count=100},token,()=>Task.CompletedTask);
        Check(datedSource.Requests==5 && !datedPager.NeedsScoreRange(dateRange with {Count=100}),"An undersized date range stops at its start boundary rather than scanning older unrelated pages");
        await datedPager.LoadScoreRangeAsync(datedSource,dateRange with {Count=0},token,()=>Task.CompletedTask);
        Check(datedSource.Requests==5,"All matches within an already covered date interval require no further request");
        Check(dateRange.Contains(9950) && !dateRange.Contains(9975) && !dateRange.Contains(9949),"Date bounds include the first second and exclude the following day's first second");
        var dateEmpty=new BattlePager(Store("date-empty"),owner);var dateEmptySource=new Source(p=>Page(p));
        await dateEmpty.LoadScoreRangeAsync(dateEmptySource,new(2,20,From:9975,Until:10000),token,()=>Task.CompletedTask);
        Check(dateEmptySource.Requests==2,"An absent character in the date interval stops when pagination crosses its start");
        Check(DateRanges.Epoch(new DateTime(1970,1,2))==57600,"Date filters use the same UTC+8 calendar as displayed match times");
        Check(DateRanges.Extrema([]).Count==0 && DateRanges.Extrema([100]).SequenceEqual([0]),"Empty and single-point charts have no duplicate extremum labels");
        Check(DateRanges.Extrema([100,100,100]).SequenceEqual([2]),"A flat chart labels its value once at the latest point");
        Check(DateRanges.Extrema([100,300,50,300,70]).SequenceEqual([3,2]),"Chart labels use the actual displayed maximum and minimum, breaking peak ties at the most recent point");
        return count;
    }
    private sealed class Source(Func<int,BattlePage> page):IBattleSource
    {
        public List<int> Pages { get; }=[];
        public int Requests=>Pages.Count;
        public Task<BattlePage> FetchPageAsync(string owner,string category,int number,CancellationToken token)
        { token.ThrowIfCancellationRequested(); if(category!="rank") throw new Exception("Chart loading requested a non-ranked category"); Pages.Add(number); return Task.FromResult(page(number)); }
    }
}
