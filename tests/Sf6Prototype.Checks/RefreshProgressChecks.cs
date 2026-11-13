using Sf6Prototype;

public static class RefreshProgressChecks
{
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        const string owner="1234567890";
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        BattleStore Store(string name)=>new(Path.Combine(root,name+".sqlite"));
        BattlePage Page(string category,int number,int total=20,int size=10,bool mixed=false)=>new(number,total,
            Enumerable.Range(0,size).Select(i=>new Battle {OwnerId=owner,ReplayId=$"{category}-{number}-{i}",Category=category.Length==0 ? "rank" : category,PlayedAt=10000-number*10-i,CharacterId=mixed && i%2==1 ? 2 : 1,Lp=15000}).ToList());
        var progress=new List<BattleLoadProgress>();
        var pager=new BattlePager(Store("initial"),owner);pager.ProgressChanged+=progress.Add;
        var source=new Source((c,p)=>Page(c,p));
        await pager.LoadInitialAsync(source,"",CancellationToken.None,()=>Task.CompletedTask);
        Check(progress.First() is {Fraction:0,RemainingPages:2,Pages:0} && progress.Last() is {Fraction:1,RemainingPages:0,Pages:2},"An initial All view reports its two actual requests");
        Check(progress.Select(p=>p.Fraction).SequenceEqual(progress.Select(p=>p.Fraction).Order()),"Paging progress does not run backwards across categories");
        var shortPager=new BattlePager(Store("early"),owner);progress.Clear();shortPager.ProgressChanged+=progress.Add;
        await shortPager.LoadInitialAsync(new Source((c,p)=>c=="rank" ? Page(c,p) : new(p,0,[])),"",CancellationToken.None,()=>Task.CompletedTask);
        Check(progress.Last() is {Fraction:1,Pages:1,RemainingPages:0},"An empty All view finishes after one actual request");
        progress.Clear();var scorePager=new BattlePager(Store("scores"),owner);scorePager.ProgressChanged+=progress.Add;
        await scorePager.LoadScoreRangeAsync(new Source((c,p)=>Page(c,p,mixed:true)),new(1,100),CancellationToken.None,()=>Task.CompletedTask);
        Check(progress.Last() is {Fraction:1,Pages:20,RemainingPages:0} && progress.Last().Detail.Contains("100 / 100"),"One hundred selected-character matches produce accurate progress even across twenty mixed-character pages");
        Check(progress[1] is {Fraction:.05,RemainingPages:19},"Remaining pages use the observed selected-character density");
        progress.Clear();var allPager=new BattlePager(Store("all"),owner);allPager.ProgressChanged+=progress.Add;
        await allPager.LoadScoreRangeAsync(new Source((c,p)=>Page(c,p,total:3)),new(1,0),CancellationToken.None,()=>Task.CompletedTask);
        Check(progress.First().Fraction==null && progress[1].Fraction is >.3 and <.34 && progress[1].RemainingPages==2,"Unknown totals remain indeterminate until the first page supplies a total");
        progress.Clear();var scarce=new BattlePager(Store("scarce"),owner);scarce.ProgressChanged+=progress.Add;
        await scarce.LoadScoreRangeAsync(new Source((c,p)=>Page(c,p,total:1,size:3)),new(1,100),CancellationToken.None,()=>Task.CompletedTask);
        Check(progress.Last().Detail.Contains("3 / 100") && progress.Last().Detail.Contains("已到可查范围") && progress.Last().Fraction==1,"Finishing an undersized history reports its actual available matches");
        progress.Clear();var cancelled=new BattlePager(Store("cancelled"),owner);cancelled.ProgressChanged+=progress.Add;
        using(var stop=new CancellationTokenSource())
        {
            try {await cancelled.LoadScoreRangeAsync(new Source((c,p)=>Page(c,p)),new(1,100),stop.Token,()=>{stop.Cancel();return Task.CompletedTask;});throw new Exception("Expected cancellation");}
            catch(OperationCanceledException) {Check(progress.All(p=>p.Fraction!=1),"Cancelled loads never emit a completed progress event");}
        }
        var state=new RefreshProgressState();state.Begin();
        string Tip(double seconds=2,double gap=5,double delay=4)=>state.Tooltip(TimeSpan.FromSeconds(seconds),TimeSpan.FromSeconds(gap),TimeSpan.FromSeconds(delay),TimeSpan.FromSeconds(1));
        Check(state.Fraction==null && Tip().Contains("剩余时间待确定"),"Validation has no invented percentage or countdown to completion");
        state.Load(new("读取排位",.5,1,3,"已准备 10 / 20 场"),2);var half=state.Fraction;
        state.Observe(new(RequestPhase.Waiting));
        Check(Tip().Contains("下次请求") && Tip().Contains("预计还需约") && Tip().Contains("10 / 20"),"Hover text combines selected-count progress, pacing and approximate time remaining");
        Check(Tip(gap:10)!=Tip(gap:5),"Remaining-time estimates adapt when the interval changes");
        _=Tip(seconds:60);Check(state.Fraction==half,"Elapsed time alone never increases the completion percentage");
        state.Load(new("读取排位",.1,2,2,""),2);Check(state.Fraction==half,"Revised counts cannot move the visible completion ring backwards");
        state.Observe(new(RequestPhase.Retrying));Check(!Tip().Contains("预计还需约"),"A retry suspends unreliable completion-time estimates");
        state.Metadata();state.Observe(new(RequestPhase.Completed,1,true));Check(state.Fraction is >.92 and <1 && state.RemainingRequests==1,"Metadata requests update progress while reserving completion for successful saving");
        state.Observe(new(RequestPhase.Completed,1,true));Check(state.Fraction<1 && state.RemainingRequests==0,"Transport completion alone does not display 100 percent");
        state.AddMetadataRequests(3);Check(state.RemainingRequests==3,"First-run character catalogs extend the metadata request estimate");
        for(var i=0;i<3;i++) state.Observe(new(RequestPhase.Completed,1,true));
        Check(state.RemainingRequests==0 && state.Fraction<1,"All localized roster responses complete before final progress reaches 100 percent");
        state.Finish(false,true);Check(state.Fraction<1 && state.Stage=="刷新已取消","Cancellation does not turn partial work into a success");
        state.Begin();state.Finish(true);Check(state.Fraction==1 && Tip().Contains("100%"),"Successful completion exposes 100 percent explicitly in hover text");
        return count;
    }
    private sealed class Source(Func<string,int,BattlePage> get):IBattleSource
    {
        public int Requests {get;private set;}
        public Task<BattlePage> FetchPageAsync(string owner,string category,int page,CancellationToken token)
        {token.ThrowIfCancellationRequested();Requests++;return Task.FromResult(get(category,page));}
    }
}
