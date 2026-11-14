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
        public List<Battle> Battles {get;}=[new(){OwnerId="1234567890",ReplayId="fixture",Category="rank",PlayedAt=1000,
            CharacterId=1,Character="RYU",Lp=12000,MyRounds=2,OpponentRounds=1}];
        public int CurrentLp=12050;
        public Exception? PlayError;
        public Exception? CardError;
        public string? PlayResponse;
        public Func<string,CancellationToken,Task>? BeforePage;
        public Task ValidateAsync(CancellationToken token) {Calls.Add("validate");Activity?.Invoke(new(RequestPhase.Sending));return Task.CompletedTask;}
        public async Task<BattlePage> FetchPageAsync(string owner,string category,int page,CancellationToken token)
        {
            if(BeforePage!=null) await BeforePage(category,token);
            token.ThrowIfCancellationRequested();Calls.Add("page");Requests++;
            return new BattlePage(page,1,category is "" or "rank" ? Battles.ToList() : []);
        }
        public Task<string> FetchCardAsync(string owner,CancellationToken token)
        {Calls.Add("card");return CardError!=null ? Task.FromException<string>(CardError) : Task.FromResult("{\"fighter_name\":\"Fixture\",\"icon_file_name\":\"fixture\"}");}
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add("play");if(PlayError!=null)return Task.FromException<string>(PlayError);
            if(PlayResponse!=null)return Task.FromResult(PlayResponse);
            return Task.FromResult(JsonSerializer.Serialize(new {pageProps=new {
                fighter_banner_info=new {personal_info=new {short_id=owner,fighter_id="Fixture"}},
                play=new {current_season_id=1,season_ids=new[]{1},character_league_infos=new[]{new {
                    character_id=1,character_name="RYU",character_tool_name="ryu",league_info=new {league_point=CurrentLp,league_rank=24,master_rating=0,master_league=0}}}}}}));
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
            Check(!result.ScoresPending && result.Sync.Inserted==1 && images==2 && requests==1,"Manual and automatic refresh share paging and image delivery");
            Check(source.Calls[0]=="validate" && source.Calls.IndexOf("card")<source.Calls.IndexOf("page") &&
                source.Calls.IndexOf("play")>source.Calls.IndexOf("page") && source.Calls.IndexOf("play")<source.Calls.IndexOf("loaded") &&
                source.Calls.Count(c=>c=="play")==1,
                "Validation and avatar discovery precede paging; verified ranked heads capture scores once before history completes");
            source.LateActivity();Check(requests==1,"A completed workflow detaches request observers");
            Check(profiles.Scores(source.AccountId)?.Characters.Single().Lp==12050,"Completed rank reads capture current scores");
            var later=new DailyProfileRefresh(profiles,source.AccountId,DateTimeOffset.UtcNow);source.Calls.Clear();
            var profileBefore=File.ReadAllText(Path.Combine(directory,"profile-"+source.AccountId+".json"));
            var charactersBefore=File.ReadAllText(Path.Combine(directory,"characters.json"));
            var scoreFile=Path.Combine(directory,"scores-"+source.AccountId+".json");
            File.WriteAllText(scoreFile,JsonSerializer.Serialize(profiles.Scores(source.AccountId)! with {FetchedAt=DateTimeOffset.FromUnixTimeSeconds(1001)}));
            source.Battles.Insert(0,source.Battles[0] with {ReplayId="latest",PlayedAt=1120,Lp=12050,MyRounds=1,OpponentRounds=2});
            source.CurrentLp=11930;
            await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:later)),callbacks,automatic,CancellationToken.None);
            Check(!later.Required && source.Calls.SequenceEqual(["page","play","loaded"]),"Same-day rank refresh reads current scores after the saved battle without waiting for history completion or repeating daily metadata");
            var settled=ScoreCalculator.Calculate(store.Query(new(source.AccountId)),profiles.Scores(source.AccountId),links:store.ScoreLinks(source.AccountId));
            Check(settled.Last() is {ReplayId:"latest",LpDelta:-120,LpAfter:11930},"A second match on the same day settles from fresh official scores at the inclusive -120 boundary");
            Check(images==2 && profileBefore==File.ReadAllText(Path.Combine(directory,"profile-"+source.AccountId+".json")) &&
                charactersBefore==File.ReadAllText(Path.Combine(directory,"characters.json")),"Score-only refresh preserves daily metadata and schedules no image downloads");
            source.Calls.Clear();
            await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Initial,"casual",Profile:later)),callbacks,automatic,CancellationToken.None);
            Check(!source.Calls.Contains("play"),"Loading non-ranked history does not recapture scores against an unverified ranked head");
            source.Calls.Clear();
            await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Scores,Range:new(1,1),Profile:later)),callbacks,automatic,CancellationToken.None);
            Check(source.Calls.SequenceEqual(["loaded"]),"A score range already satisfied by local records performs no profile request");
            var scoreBefore=File.ReadAllText(scoreFile);source.PlayError=new IOException("offline");
            var offline=await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:later)),callbacks,automatic,CancellationToken.None);
            Check(offline.ScoresPending && File.ReadAllText(scoreFile)==scoreBefore && store.Query(new(source.AccountId)).Count==2,
                "A failed score-only request retains saved matches and the last successful score cache");
            source.PlayError=null;source.PlayResponse="{malformed";
            var invalid=await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:later)),callbacks,automatic,CancellationToken.None);
            Check(invalid.ScoresPending && File.ReadAllText(scoreFile)==scoreBefore,"An unreadable score response cannot be reported as a successful refresh");
            source.PlayResponse=null;
            source.PlayError=new LoginRequiredException("fixture");var failedDaily=new DailyProfileRefresh(new ProfileCache(Path.Combine(directory,"expired")),source.AccountId,DateTimeOffset.UtcNow);
            var task=RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:failedDaily)),callbacks,automatic,CancellationToken.None);
            if(automatic) {try {await task;throw new Exception("Expected login failure");}catch(LoginRequiredException){count++;}}
            else Check((await task).ScoresPending,"Manual refresh retains saved matches when the score profile needs login");
            source.PlayError=null;source.CardError=new IOException("portrait unavailable");
            var portraitProfiles=new ProfileCache(Path.Combine(directory,"portrait"));
            var portrait=await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,
                Profile:new(portraitProfiles,source.AccountId,DateTimeOffset.UtcNow))),callbacks,automatic,CancellationToken.None);
            Check(!portrait.ScoresPending && portraitProfiles.Scores(source.AccountId)?.Characters.Single().Lp==11930,
                "An optional portrait failure does not make a successful current-score update appear incomplete");
            source.CardError=null;
            foreach (var endpoint in new[] { "card", "play" })
            {
                var limitedDirectory = Path.Combine(directory, "limited-" + endpoint);
                var limitedStore = new BattleStore(Path.Combine(limitedDirectory, "archive.sqlite"));
                var limited = new Source();
                var error = new HttpRequestException("fixture", null, System.Net.HttpStatusCode.TooManyRequests);
                if (endpoint == "play") limited.PlayError = error;
                else limited.CardError = error;
                var limitedProfile = new DailyProfileRefresh(new ProfileCache(limitedDirectory), limited.AccountId, DateTimeOffset.UtcNow);
                var limitedPlan = new RefreshPlan(new(limitedStore, limited.AccountId), RefreshMode.Account, Profile:limitedProfile);
                try
                {
                    await RefreshWorkflow.RunAsync(limited, true, () => Task.FromResult(limitedPlan), new(), automatic, CancellationToken.None);
                    throw new Exception("Expected the profile rate limit to stop the refresh");
                }
                catch (HttpRequestException ex) when (ReferenceEquals(ex, error)) { count++; }
                string[] expected = endpoint == "play" ? ["validate", "card", "page", "play"] : ["validate", "card"];
                Check(limited.Calls.SequenceEqual(expected), $"No more pages or profiles are requested after {endpoint} returns 429");
                Check(limitedStore.Query(new(limited.AccountId)).Count == (endpoint == "play" ? 1 : 0) &&
                    limitedStore.LastCompletedSync(limited.AccountId) == null, "A rate limit retains committed pages without completing the account sync");
            }
            using var cancelled=new CancellationTokenSource();source.PlayError=null;source.Calls.Clear();
            try
            {
                await RefreshWorkflow.RunAsync(source,false,()=>Task.FromResult(new RefreshPlan(new(store,source.AccountId),RefreshMode.Account,Profile:later)),
                    new(){PageSaved=()=>{cancelled.Cancel();return Task.CompletedTask;}},automatic,cancelled.Token);
                throw new Exception("Expected cancellation");
            }
            catch(OperationCanceledException) {count++;}
            Check(!source.Calls.Contains("play") && store.Query(new(source.AccountId)).Count==2,"Cancellation retains committed battles and never starts further profile requests");

            // Hold the next history request open: the latest score must already be
            // published while the overall refresh is still in progress.
            var streamingDirectory=Path.Combine(directory,"streaming");var streamingProfiles=new ProfileCache(streamingDirectory);
            var streamingStore=new BattleStore(Path.Combine(streamingDirectory,"archive.sqlite"));
            var streamingSource=new Source {CurrentLp=11930};
            streamingSource.Battles.Insert(0,streamingSource.Battles[0] with {ReplayId="latest",PlayedAt=1120,Lp=12050,MyRounds=1,OpponentRounds=2});
            var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            streamingSource.BeforePage=async (category,token)=>
            {
                if(category!="rank")return;
                waiting.TrySetResult();await resume.Task.WaitAsync(token);
            };
            var snapshots=new List<List<ScorePoint>>();
            var streaming=RefreshWorkflow.RunAsync(streamingSource,false,
                ()=>Task.FromResult(new RefreshPlan(new(streamingStore,source.AccountId),RefreshMode.Account,
                    Profile:new(streamingProfiles,source.AccountId,DateTimeOffset.UtcNow))),
                new(){PageSaved=()=>
                {
                    snapshots.Add(ScoreCalculator.Calculate(streamingStore.Query(new(source.AccountId)),streamingProfiles.Scores(source.AccountId),
                        links:streamingStore.ScoreLinks(source.AccountId)));
                    return Task.CompletedTask;
                }},automatic,CancellationToken.None);
            try
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(!streaming.IsCompleted && snapshots[0].First().LpDelta==50 && snapshots[0].Last().LpDelta==null,
                    "Replay-derived settlements are published immediately while the newest settlement is still pending");
                Check(snapshots[^1].Last() is {ReplayId:"latest",LpDelta:-120,LpAfter:11930},
                    "The latest official score is published before the remaining history request completes");
                Check(streamingSource.Calls.Count(c=>c=="play")==1,"Streaming settlements reuse the refresh's single profile request");
            }
            finally {resume.TrySetResult();await streaming;}
        }
        return count;
    }
}
