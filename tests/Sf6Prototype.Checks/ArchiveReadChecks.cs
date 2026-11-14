using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sf6Prototype;

public static class ArchiveReadChecks
{
    private const string Owner = "1234567890";
    private static Battle Row(int i) => new()
    {
        OwnerId=Owner,ReplayId=$"R{i:00000}",Category=i%2==0 ? "rank" : "casual",PlayedAt=1000+i/2,
        CharacterId=i%3+1,Character="character"+i%3,OpponentCharacter="other"+i%2,OpponentCharacterId=4,
        PlayerName="Player",Opponent=i%2==0 ? "ABC玩家" : "abcOpponent",OpponentId="2222222222",
        MyRounds=i%3,OpponentRounds=1,RoundResults=[1,0,8],OpponentRoundResults=[0,1,0],
        Lp=10000+i,Mr=1500+i,Season=7,RawJson=JsonSerializer.SerializeToElement(new { replay=i, extra="original" })
    };
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value) throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"archive.sqlite");var store=new BattleStore(path);
        store.SavePage(Enumerable.Range(0,30).Select(Row));
        store.AddTag(Owner,"R00000","Review");store.AddTag(Owner,"R00002","Review");store.AddTag(Owner,"R00002","防空");
        var filters=new BattleFilter[]
        {
            new(Owner),new(Owner,Category:"rank"),new(Owner,Character:"character1"),new(Owner,Outcome:"胜"),
            new(Owner,Search:"aBc"),new(Owner,Search:"玩家"),new(Owner,Search:"%"),new(Owner,Search:"2222"),
            new(Owner,Search:"R00001"),new(Owner,From:1002,Until:1008),new(Owner,OpponentCharacter:"other1"),
            new(Owner,FavoritesOnly:true),new(Owner,Tags:["review","防空"],MatchAllTags:true),new(Owner,Tags:["防空","unknown"]),
            new(Owner,Category:"rank",Character:"character2",Search:"abc",From:1000,Until:1010),new("9999999999")
        };
        foreach(var filter in filters)
        {
            var full=store.Query(filter);var summary=store.QuerySummaries(filter);
            var view=store.ReadView(filter);
            Check(view.Count==full.Count && view.Statistics.Wins==full.Count(b=>b.Outcome=="胜") &&
                view.Statistics.Losses==full.Count(b=>b.Outcome=="负") && view.Statistics.Draws==full.Count(b=>b.Outcome=="平"),
                "SQL aggregates preserve the full filtered totals, including draws and unknown outcomes");
            Check(view.Take(20).Select(b=>b.ReplayId).SequenceEqual(full.Take(20).Select(b=>b.ReplayId)),"Lazy first pages preserve SQL filter ordering");
            Check(view.Find(["R00000","R00004","missing"]).Select(b=>b.ReplayId).SequenceEqual(full.Where(b=>b.ReplayId is "R00000" or "R00004").Select(b=>b.ReplayId)),
                "Restored visible rows remain constrained by the current filter");
            Check(JsonSerializer.Serialize(full.Select(b=>b with {RawJson=null}))==JsonSerializer.Serialize(summary),
                "Summaries preserve all displayed fields, ordering and SQL filter semantics");
            Check(store.QuerySummaryPage(filter,1,3).Select(b=>b.ReplayId).SequenceEqual(full.Skip(1).Take(3).Select(b=>b.ReplayId)),
                "Database paging preserves filtering and tie ordering");
        }
        var first=store.QuerySummaries(new(Owner));
        Check(ReferenceEquals(first,store.QuerySummaries(new(Owner))),"An unchanged archive reuses its summary snapshot");
        var ranked=store.QuerySummaries(new(Owner,Category:"rank"));
        Check(ReferenceEquals(ranked,store.QuerySummaries(new(Owner,Category:"rank"))),"Repeated filters reuse their result");
        var oldView=store.ReadView(new(Owner));
        store.SavePage([Row(30)]);
        Check(!ReferenceEquals(oldView,store.ReadView(new(Owner))) && store.ReadView(new(Owner)).Count==31,"Page commits invalidate SQL views and aggregate counts");
        var second=store.QuerySummaries(new(Owner));
        Check(second.Count==31 && first.Count==30 && ReferenceEquals(first[0],second.Single(b=>b.ReplayId==first[0].ReplayId)),
            "Appending a page keeps old snapshots stable and reuses unchanged summaries");
        store.SavePage([Row(0) with {OpponentLeagueRank=19,RawJson=JsonSerializer.SerializeToElement(new {extra="updated"})}]);
        Check(store.QuerySummaries(new(Owner)).Single(b=>b.ReplayId=="R00000").OpponentLeagueRank==19,
            "Enrichment of an existing replay invalidates its summary");
        Check(store.Detail(Owner,"R00000")!.RawJson!.Value.GetProperty("extra").GetString()=="updated",
            "Detail reads retain the complete updated original JSON");
        var before=store.QuerySummaries(new(Owner));
        try {store.SavePage([Row(31),Row(32) with {OwnerId=null!}]);throw new Exception("Expected rollback");}
        catch(SqliteException) {count++;}
        Check(ReferenceEquals(before,store.QuerySummaries(new(Owner))) && store.Detail(Owner,"R00031")==null,
            "A rolled-back page never contaminates the read cache");
        var favoriteFilter=new BattleFilter(Owner,FavoritesOnly:true);
        Check(store.QuerySummaries(favoriteFilter).Count==2,"Favorite filtering uses current tags");
        var favoriteSnapshot=store.FavoriteRecords(summariesOnly:true);
        store.RemoveTag(Owner,"R00000","Review");
        Check(store.QuerySummaries(favoriteFilter).Count==1 && store.FavoriteRecords(summariesOnly:true).Count==1,
            "Tag removal invalidates filtered and favorite snapshots");
        Check(favoriteSnapshot.Count==2 && store.FavoriteRecords().Single().Battle.RawJson!=null,
            "Favorite snapshots remain stable and full export reads still retain raw payloads");
        Check(store.PrefixSummaries(Owner,["R00002","missing","R00000"]).Select(b=>b.ReplayId).SequenceEqual(["R00002","R00000"]),
            "Prefix reads preserve the proven replay sequence");
        new BattleStore(path).SavePage([Row(40)]);store.InvalidateReads();
        Check(store.QuerySummaries(new(Owner)).Count==32,"Import invalidation includes writes from another connection");
        foreach(var owner in new[]{"2222222222","3333333333","4444444444","5555555555"}) store.QuerySummaries(new(owner));
        Check(store.QuerySummaries(new(Owner)).Count==32,"Evicting an inactive player cache never loses archived data");
        return count;
    }
    public static void Benchmark()
    {
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks","archive-benchmark-"+Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"archive.sqlite"));
        var payload=JsonSerializer.SerializeToElement(new {extra=new string('x',4096)});
        store.SavePage(Enumerable.Range(0,10000).Select(i=>Row(i) with {RawJson=payload}));
        store.QuerySummaryPage(new(Owner),0,1);store.Query(new(Owner,Until:1001));
        void Measure(string name,Func<List<Battle>> read)
        {
            GC.Collect();GC.WaitForPendingFinalizers();
            var allocated=GC.GetAllocatedBytesForCurrentThread();var start=Stopwatch.GetTimestamp();
            var rows=read();
            Console.WriteLine($"{name}: rows={rows.Count}, ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}, allocatedMB={(GC.GetAllocatedBytesForCurrentThread()-allocated)/1048576d:F2}");
        }
        Measure("full_json",()=>store.Query(new(Owner)));
        Measure("summary_cold",()=>store.QuerySummaries(new(Owner)));
        Measure("summary_warm",()=>store.QuerySummaries(new(Owner)));
        store.SavePage([Row(10000) with {RawJson=payload}]);
        Measure("summary_append_one",()=>store.QuerySummaries(new(Owner)));
        Measure("summary_page_20",()=>store.QuerySummaryPage(new(Owner),0,20));
        Measure("history_first_20_with_totals",()=>store.ReadView(new(Owner)).Take(20).ToList());
        var rows=store.QuerySummaries(new(Owner));var projection=new ScoreProjection();var links=new Dictionary<string,string>();
        var baseline=ScoreCalculator.Calculate(rows,links:links);projection.Update(Owner,rows,null,links);
        var changed=rows.Append(Row(10002)).ToArray();
        var start=Stopwatch.GetTimestamp();var fullScores=ScoreCalculator.Calculate(changed,links:links);
        Console.WriteLine($"scores_full: rows={fullScores.Count}, ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}");
        start=Stopwatch.GetTimestamp();var incremental=projection.Update(Owner,changed,null,links);
        Console.WriteLine($"scores_incremental: rows={incremental.Count}, ms={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}");
        if(fullScores.Any(p=>incremental[p.ReplayId]!=p)) throw new Exception("Benchmark score projections differ");
        Console.WriteLine("Synthetic archive: "+root);
    }
}
