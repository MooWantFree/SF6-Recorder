using System.Diagnostics;
using Sf6Prototype;

public static class StreamingChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var dir = Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string owner = "3396336472";
        var store = new BattleStore(Path.Combine(dir, "stream.sqlite"));
        var delivered = new List<int>();
        var source = new FakeSource((category, page) =>
        {
            if (category != "rank") return new(page, 0, []);
            if (page == 2) Check(delivered.SequenceEqual([1]), "First page is delivered before requesting the second page");
            return new(page, 2, [new Battle {OwnerId=owner,ReplayId="P"+page,Category=category,PlayedAt=10-page,Lp=1000}]);
        });
        await new SyncService(store, source).RunAsync(owner, null, CancellationToken.None, pageSaved: async added =>
        {
            Check(added==1 && store.Query(new(owner)).Count==delivered.Count+1, "Each page callback observes its already committed records");
            Check(store.IsSyncIncomplete(owner), "Partial histories are marked incomplete while pages are still arriving");
            delivered.Add(store.Query(new(owner)).Count);
            await Task.Yield();
        });
        Check(delivered.SequenceEqual([1,2]) && !store.IsSyncIncomplete(owner), "Streaming completes with all pages and restores settlement calculations");
        var notified = 0;
        await new SyncService(store, source).RunAsync(owner, null, CancellationToken.None, pageSaved: _ => { notified++; return Task.CompletedTask; });
        Check(notified==0, "Duplicate incremental pages do not trigger unnecessary list rebuilds");
        using var cancelled = new CancellationTokenSource();
        var partialStore = new BattleStore(Path.Combine(dir, "partial.sqlite"));
        var partialSource = new FakeSource((category,page)=>new(page,5,[new Battle {OwnerId=owner,ReplayId="C"+page,Category=category,Lp=1200}]));
        try
        {
            await new SyncService(partialStore,partialSource).RunAsync(owner,null,cancelled.Token,pageSaved: _ => {cancelled.Cancel();return Task.CompletedTask;});
            throw new Exception("Expected cancellation");
        }
        catch(OperationCanceledException) { }
        Check(partialSource.Requests==1 && partialStore.Query(new(owner)).Count==1 && partialStore.IsSyncIncomplete(owner), "Cancellation preserves visible pages without completing the sync");
        var unsettled = BattleStore.Scores([new Battle {Category="rank",ReplayId="OLD",CharacterId=1,Lp=1000}, new Battle {Category="rank",ReplayId="NEW",CharacterId=1,Lp=1500}], settlementsComplete:false);
        Check(unsettled.Count==2 && unsettled.All(p=>p.LpDelta==null && p.LpAfter==null && !p.Estimated), "Incomplete backfill never labels a missing-record gap as one match's score change");

        var project = Path.Combine(dir, "checkout");
        Directory.CreateDirectory(Path.Combine(project,"src","Sf6Prototype"));
        File.WriteAllText(Path.Combine(project,"build.ps1"),""); File.WriteAllText(Path.Combine(project,"src","Sf6Prototype","Sf6Prototype.csproj"),"");
        var canonical = Path.Combine(project,"artifacts","app","Data");
        Check(DataPaths.Resolve(Path.Combine(project,"src","Sf6Prototype","bin","Release","net10.0-windows"))==canonical, "Release builds use the published app's data directory");
        Check(DataPaths.Resolve(Path.Combine(project,"src","Sf6Prototype","bin","Debug","net10.0-windows"))==canonical && DataPaths.Resolve(Path.Combine(project,"artifacts","app"))==canonical, "Debug and published apps resolve the same data directory and lock");
        Check(DataPaths.Resolve(project,Path.Combine(dir,"isolated"))==Path.Combine(dir,"isolated"), "Explicit test data directories always take precedence");
        var portable = Path.Combine(Path.GetTempPath(),"sf6-portable-"+Guid.NewGuid().ToString("N"));
        Check(DataPaths.Resolve(portable)==Path.Combine(portable,"Data"), "Standalone distributions remain portable");

        const string play = """{"pageProps":{"fighter_banner_info":{"personal_info":{"short_id":3396336472,"fighter_id":"Player"}},"play":{"current_season_id":14,"season_ids":[14],"character_league_infos":[{"character_id":16,"character_name":"Juri","character_tool_name":"juri","league_info":{"league_point":20000,"league_rank":30,"master_rating":0,"master_league":0}}]}}}""";
        var profiles = new ProfileCache(dir);
        var profileSource = new FakeProfiles(play, """{"fighter_name":"Current nickname","icon_file_name":"test_icon"}""");
        var images = await profiles.RefreshMetadataAsync(profileSource,owner,CancellationToken.None,captureScores:true);
        Check(profileSource.Calls==2 && profiles.Profile(owner)?.Name=="Current nickname" && profiles.Scores(owner)?.Characters.Single().Lp==20000, "Nickname and current scores are saved before any portrait downloads");
        Check(images.Count==2 && images[0].Refresh && images.All(i=>!File.Exists(i.Destination)), "Metadata refresh returns deferred images without waiting for them");
        var imageSource = new FakeImages(); var saved = 0;
        await ImageDownloads.RunAsync(images.Concat(images),imageSource,_=>{saved++; return Task.CompletedTask;},CancellationToken.None);
        Check(imageSource.Calls==2 && saved==2 && images.All(i=>File.Exists(i.Destination)), "Background images are deduplicated and announced only after saving");
        await ImageDownloads.RunAsync(images,imageSource,_=>Task.CompletedTask,CancellationToken.None);
        Check(imageSource.Calls==3, "Cached character portraits are skipped while account avatars still refresh");
        using var stopImages = new CancellationTokenSource(); var pending = new[] {new ImageDownload("/unused",Path.Combine(dir,"one.png")),new ImageDownload("/unused",Path.Combine(dir,"two.png"))};
        var cancellableSource = new FakeImages();
        try { await ImageDownloads.RunAsync(pending,cancellableSource,_=>{stopImages.Cancel();return Task.CompletedTask;},stopImages.Token); }
        catch(OperationCanceledException) { }
        Check(cancellableSource.Calls==1 && !File.Exists(pending[1].Destination), "Foreground cancellation stops the remaining background image queue");
        var failingSource = new FakeImages { Fail=true };
        try { await ImageDownloads.RunAsync(pending,failingSource,_=>Task.CompletedTask,CancellationToken.None); }
        catch(HttpRequestException) { }
        Check(failingSource.Calls==1, "Image request failure stops the batch rather than repeatedly hitting the server");

        var gate = new RequestGate(TimeSpan.FromMilliseconds(30));
        var started = new List<long>(); var active=0; var peak=0;
        async Task<int> Request() { started.Add(Stopwatch.GetTimestamp()); active++; peak=Math.Max(peak,active); await Task.Delay(5); active--; return 1; }
        await Task.WhenAll(Enumerable.Range(0,3).Select(_=>gate.RunAsync(Request,CancellationToken.None)));
        Check(peak==1 && started.Count==3, "One shared request gate serializes concurrent foreground/background requests");
        Check(Stopwatch.GetElapsedTime(started[0],started[2]).TotalMilliseconds>=55, "The request gate spaces request starts, not just responses");
        var hold = new TaskCompletionSource<int>(); var entered = new TaskCompletionSource();
        var first = gate.RunAsync(()=>{entered.SetResult();return hold.Task;},CancellationToken.None); await entered.Task;
        using var waiting = new CancellationTokenSource(); var called=false;
        var second = gate.RunAsync(()=>{called=true;return Task.FromResult(1);},waiting.Token); waiting.Cancel();
        try {await second;} catch(OperationCanceledException) { }
        hold.SetResult(1); await first;
        Check(!called, "Cancelling a queued request prevents it from sending");
        var adjustable=new RequestGate(TimeSpan.FromSeconds(30));
        Check(adjustable.RemainingDelay==TimeSpan.Zero,"A fresh gate does not delay its first request");
        await adjustable.RunAsync(()=>Task.FromResult(1),CancellationToken.None);
        using var shortDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var queued=adjustable.RunAsync(()=>Task.FromResult(2),shortDeadline.Token);
        Check(!queued.IsCompleted && adjustable.RemainingDelay.TotalSeconds>25,"A queued request honors the configured interval");
        adjustable.Interval=TimeSpan.Zero;
        Check(await queued==2,"Saving a shorter interval updates an already waiting request without restarting the app");
        adjustable.Interval=TimeSpan.FromSeconds(30);
        using var stopWait=new CancellationTokenSource();
        var sentAfterCancel=false;var waitingForInterval=adjustable.RunAsync(()=>{sentAfterCancel=true;return Task.FromResult(0);},stopWait.Token);
        stopWait.Cancel();try {await waitingForInterval;}catch(OperationCanceledException){}
        Check(!sentAfterCancel,"Cancelling an interval wait sends no request");
        return count;
    }
    private sealed class FakeProfiles(string play, string card) : IProfileSource
    {
        public int Calls;
        public Task<string> FetchPlayAsync(string owner,CancellationToken token) { Calls++;return Task.FromResult(play); }
        public Task<string> FetchCardAsync(string owner,CancellationToken token) { Calls++;return Task.FromResult(card); }
    }
    private sealed class FakeImages : IImageSource
    {
        public int Calls; public bool Fail;
        public Task DownloadAsync(ImageDownload image,CancellationToken token)
        { Calls++; if(Fail) throw new HttpRequestException("test error");File.WriteAllText(image.Destination,"image fixture");return Task.CompletedTask; }
    }
}
