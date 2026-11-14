using System.Text.Json;
using Sf6Prototype;

public static class RefreshWorkflowChecks
{
    private sealed class Source : IRefreshSource
    {
        public string AccountId=>"1234567890";
        public int Requests {get;private set;}
        public event Action<RequestActivity>? Activity;
        public List<string> Calls {get;}=[];
        public Exception? PlayError;
        public Task ValidateAsync(CancellationToken token) {Calls.Add("validate");Activity?.Invoke(new(RequestPhase.Sending));return Task.CompletedTask;}
        public Task<BattlePage> FetchPageAsync(string owner,string category,int page,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add("page");Requests++;
            return Task.FromResult(new BattlePage(page,1,category is "" or "rank" ?
                [new(){OwnerId=owner,ReplayId="fixture",Category="rank",PlayedAt=1000,CharacterId=1,Character="RYU",Lp=12000,MyRounds=2,OpponentRounds=1}] : []));
        }
        public Task<string> FetchCardAsync(string owner,CancellationToken token)
        {Calls.Add("card");return Task.FromResult("{\"fighter_name\":\"Fixture\",\"icon_file_name\":\"fixture\"}");}
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        {
            Calls.Add("play");if(PlayError!=null)return Task.FromException<string>(PlayError);
            return Task.FromResult(JsonSerializer.Serialize(new {pageProps=new {
                fighter_banner_info=new {personal_info=new {short_id=owner,fighter_id="Fixture"}},
                play=new {current_season_id=1,season_ids=new[]{1},character_league_infos=new[]{new {
                    character_id=1,character_name="RYU",character_tool_name="ryu",league_info=new {league_point=12050,league_rank=24,master_rating=0,master_league=0}}}}}}));
        }
        public void LateActivity()=>Activity?.Invoke(new(RequestPhase.Completed));
    }
    public static async Task<int> Run()
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        foreach(var automatic in new[]{false,true})
        {
            var directory=Path.Combine(root,automatic.ToString());var profiles=new ProfileCache(directory);
            var store=new BattleStore(Path.Combine(directory,"archive.sqlite"));var source=new Source();var requests=0;var images=0;
            var daily=new DailyProfileRefresh(profiles,source.AccountId,DateTimeOffset.UtcNow);
            var callbacks=new RefreshCallbacks {
                Request=_=>requests++,PrepareImages=()=>{source.Calls.Add("images");return Task.CompletedTask;},ImagesReady=rows=>images+=rows.Count,
                PageSaved=()=>{Check(store.Query(new(source.AccountId)).Count>0,"Refresh callbacks observe committed pages");return Task.CompletedTask;},
                BattlesLoaded=()=>source.Calls.Add("loaded")};
            var result=await RefreshWorkflow.RunAsync(source,true,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:daily)),callbacks,automatic,CancellationToken.None);
            Check(!result.ImagesPending && result.Sync.Inserted==1 && images==2 && requests==1,"Manual and automatic refresh share paging and image delivery");
            Check(source.Calls[0]=="validate" && source.Calls.IndexOf("card")<source.Calls.IndexOf("page") && source.Calls.IndexOf("play")>source.Calls.IndexOf("loaded"),
                "Validation and avatar discovery precede paging; current scores are captured only afterwards");
            source.LateActivity();Check(requests==1,"A completed workflow detaches request observers");
            Check(profiles.Scores(source.AccountId)?.Characters.Single().Lp==12050,"Completed rank reads capture current scores");
            var later=new DailyProfileRefresh(profiles,source.AccountId,DateTimeOffset.UtcNow);source.Calls.Clear();
            await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:later)),callbacks,automatic,CancellationToken.None);
            Check(source.Calls.All(c=>c is "page" or "loaded"),"Same-day refresh keeps its battle-only request policy");
            source.PlayError=new LoginRequiredException("fixture");var failedDaily=new DailyProfileRefresh(new ProfileCache(Path.Combine(directory,"expired")),source.AccountId,DateTimeOffset.UtcNow);
            var task=RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:failedDaily)),callbacks,automatic,CancellationToken.None);
            if(automatic) {try {await task;throw new Exception("Expected login failure");}catch(LoginRequiredException){count++;}}
            else Check((await task).ImagesPending,"Manual refresh retains saved matches when the post-sync profile needs login");
            using var cancelled=new CancellationTokenSource();source.PlayError=null;source.Calls.Clear();
            try
            {
                await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account)),
                    new(){PageSaved=()=>{cancelled.Cancel();return Task.CompletedTask;}},automatic,cancelled.Token);
                throw new Exception("Expected cancellation");
            }
            catch(OperationCanceledException) {count++;}
            Check(!source.Calls.Contains("play") && store.Query(new(source.AccountId)).Count==1,"Cancellation retains committed battles and never starts post-sync profile requests");
        }
        return count;
    }
}
