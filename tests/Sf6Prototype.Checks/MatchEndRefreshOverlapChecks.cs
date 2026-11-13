using Sf6Prototype;

internal static class MatchEndRefreshOverlapChecks
{
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        const string owner=PagingChecks.Owner;
        var now=new DateTimeOffset(2026,11,14,10,0,0,TimeSpan.FromHours(8));
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        // Advance a logical clock while real pagination is suspended after its first
        // saved page. No network requests or minute-long sleeps are needed.
        foreach(var matchTriggered in new[]{true,false})
        foreach(var finishedSecond in new[]{62,70})
        {
            var store=new BattleStore(Path.Combine(root,$"overlap-{matchTriggered}-{finishedSecond}.sqlite"));
            var old=PagingChecks.Games(1)[0];store.SavePage([old]);
            store.CompleteCategory(owner,BattlePager.AccountBoundary,old.ReplayId);
            var firstSnapshot=Enumerable.Range(0,20).Select(i=>old with
                {ReplayId=$"A{i:00}",PlayedAt=old.PlayedAt+20-i}).Append(old).ToArray();
            var later=old with {ReplayId="B",PlayedAt=old.PlayedAt+60};
            var source=new FakeSource((category,page)=>PagingChecks.Page(firstSnapshot,category,page));
            var schedule=new MatchEndRefreshSchedule();
            string? Frontier()=>store.Frontier(owner,BattlePager.AccountBoundary);
            int? firstVersion=null;
            if(matchTriggered)
            {
                schedule.Queue(owner,now);
                Check(schedule.IsDue(owner,now.AddSeconds(5)),"The first detected match starts its refresh after the configured wait");
                firstVersion=schedule.Begin();
            }
            var initialBoundary=Frontier();
            var headSaved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh=new BattlePager(store,owner).LoadAccountAsync(source,CancellationToken.None,async ()=>
            {
                if(headSaved.TrySetResult()) await resume.Task;
            });
            await headSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!refresh.IsCompleted && Frontier()==old.ReplayId,"The old refresh has saved its head but has not committed its account boundary");
            schedule.Queue(owner,now.AddSeconds(60));
            resume.SetResult();await refresh;
            Check(source.Requests==3 && Frontier()==firstSnapshot[0].ReplayId && store.Query(new(owner)).All(b=>b.ReplayId!="B"),
                "Finishing the old three-page snapshot advances the frontier without fetching the later match");
            if(firstVersion is { } version)
                Check(!schedule.Complete(version,true,Frontier()!=initialBoundary,now.AddSeconds(finishedSecond)),
                    "The old detected task's completion cannot complete the newer detection");
            Check(schedule.IsDue(owner,now.AddSeconds(finishedSecond))==(finishedSecond>=65) && schedule.Owner==owner && schedule.Attempts==0,
                "An old automatic or manual refresh cannot erase the newer detection or change its five-second wait");
            var nextAt=now.AddSeconds(Math.Max(65,finishedSecond));
            Check(schedule.IsDue(owner,nextAt),"The newer detection remains due after the old refresh advances the frontier");
            // The official listing can still lag. Compare against the boundary at
            // this refresh's start, not the boundary captured when B was detected.
            var before=Frontier();var nextVersion=schedule.Begin();
            var lagging=new FakeSource((category,page)=>PagingChecks.Page(firstSnapshot,category,page));
            await new BattlePager(store,owner).LoadAccountAsync(lagging,CancellationToken.None,()=>Task.CompletedTask);
            schedule.Complete(nextVersion,true,Frontier()!=before,nextAt);
            Check(lagging.Requests==1 && schedule.Owner==owner && !schedule.IsDue(owner,nextAt.AddSeconds(7)) &&
                schedule.IsDue(owner,nextAt.AddSeconds(8)),"The newer detection retains its normal eight-second settlement retry");
            before=Frontier();nextVersion=schedule.Begin();
            var available=new FakeSource((category,page)=>PagingChecks.Page(new[]{later}.Concat(firstSnapshot).ToArray(),category,page));
            await new BattlePager(store,owner).LoadAccountAsync(available,CancellationToken.None,()=>Task.CompletedTask);
            schedule.Complete(nextVersion,true,Frontier()!=before,nextAt.AddSeconds(8));
            Check(available.Requests==1 && Frontier()=="B" && store.Query(new(owner)).Any(b=>b.ReplayId=="B") && schedule.Owner==null,
                "One bounded retry fetches the later match and finishes its task without another queued refresh");
        }
        foreach(var success in new[]{true,false})
        {
            var schedule=new MatchEndRefreshSchedule();schedule.Queue(owner,now);
            var oldVersion=schedule.Begin();
            schedule.Queue(owner,now.AddSeconds(60));schedule.Queue(owner,now.AddSeconds(120),12,20);
            Check(!schedule.Complete(oldVersion,success,success,now.AddSeconds(125)) && schedule.Attempts==0 &&
                !schedule.IsDue(owner,now.AddSeconds(131)) && schedule.IsDue(owner,now.AddSeconds(132)),
                "Several overlapping detections merge into the latest wait, unaffected by old success or failure");
            var latest=schedule.Begin();schedule.Complete(latest,true,false,now.AddSeconds(135));
            Check(!schedule.IsDue(owner,now.AddSeconds(154)) && schedule.IsDue(owner,now.AddSeconds(155)),
                "The merged detection keeps its own configured retry interval");
            schedule.Complete(schedule.Begin(),true,false,now.AddSeconds(156));
            Check(schedule.Owner==null && !schedule.IsDue(owner,now.AddSeconds(200)),
                "Coalesced detections still stop after one retry when no new record is available");
            schedule.Queue(owner,now);oldVersion=schedule.Begin();schedule.Clear();
            Check(!schedule.Complete(oldVersion,true,false,now.AddSeconds(7)) && schedule.Owner==null,
                "An in-flight completion cannot recreate a task cleared by stop or disabling detection");
            schedule.Queue("9876543210",now.AddSeconds(8));
            Check(!schedule.Complete(oldVersion,false,false,now.AddSeconds(9)) && schedule.Owner=="9876543210",
                "An old account's completion cannot clear a task belonging to the newly selected account");
        }
        return count;
    }
}
