using System.Text.Json;
using Sf6Prototype;

internal static class BundledCharacterNameChecks
{
    private const string Owner="1234567890";
    private sealed class Source(CharacterNameCatalog catalog) : IProfileSource,ICharacterNameSource
    {
        public List<string> NameRequests {get;}=[];
        public string? FailLanguage {get;set;}
        public bool FailCard {get;set;}
        public Action<CharacterObservation>? Observe {get;set;}
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        {
            var json=Page("zh-Hans");Observe?.Invoke(CharacterObservation.Profile(json));return Task.FromResult(json);
        }
        public Task<string> FetchCardAsync(string owner,CancellationToken token)=>FailCard
            ? Task.FromException<string>(new IOException("card unavailable")) : Task.FromResult("{\"fighter_name\":\"Fixture\"}");
        public Task<string> FetchCharacterNamesAsync(string owner,string language,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();NameRequests.Add(language);
            return language==FailLanguage ? Task.FromException<string>(new IOException("name service unavailable")) : Task.FromResult(Page(language));
        }
        private string Page(string language)=>JsonSerializer.Serialize(new
        {
            pageProps=new
            {
                fighter_banner_info=new {personal_info=new {short_id=Owner,fighter_id="Fixture"}},
                play=new {current_season_id=1,season_ids=new[]{1},character_league_infos=catalog.Characters.Select(c=>new
                {
                    character_id=c.Id,character_name=c.Names[language],character_tool_name=c.ToolName,
                    league_info=new {league_point=0,league_rank=0,master_rating=0,master_league=0}
                })}
            }
        });
    }
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool ok,string message) {if(!ok) throw new Exception(message);count++;}
        string Root()=>Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var raw=File.ReadAllText(Path.Combine(Environment.CurrentDirectory,"src","Sf6Prototype","Resources","CharacterNames.json"));
        var shipped=JsonSerializer.Deserialize<CharacterNameCatalog>(raw)!;
        var roster=shipped.Characters.Select(c=>(c.Id,Name:c.Names["zh-Hans"],c.ToolName)).ToList();
        Check(shipped.Characters.Count==33 && shipped.Characters.Select(c=>c.Id).Distinct().Count()==33 &&
            shipped.Characters.All(c=>c.Names.Keys.ToHashSet().SetEquals(CharacterNames.Languages)),"The downloaded distributable catalog has 33 unique official entries and all four languages");
        Check(!raw.Contains(Owner) && !raw.Contains("short_id") && !raw.Contains("access_token") && !raw.Contains("Cookie"),"Only public character metadata is shipped with the application");
        var cleanRoot=Root();var clean=new CharacterNames(cleanRoot);
        var source=new Source(shipped);
        try
        {
            foreach(var language in CharacterNames.Languages)
            {
                Localization.Apply(language);
                Check(!clean.NeedsLanguage(language,roster),"A new installation has complete names before signing in");
                Check(shipped.Characters.All(c=>clean.Display(c.Id,"raw")==c.Names[language]),"The embedded resource preserves the exact official names in every language");
                await clean.EnsureLanguageAsync(source,Owner,language,roster,CancellationToken.None);
            }
            await clean.EnsureAsync(source,Owner,roster,CancellationToken.None);
            Check(source.NameRequests.Count==0 && !Directory.Exists(cleanRoot),"Bundled names need neither requests nor a writable first-run cache");
            var brokenRoot=Root();Directory.CreateDirectory(brokenRoot);var path=Path.Combine(brokenRoot,"character-names.json");
            foreach(var corrupt in new[]{"{broken","{\"Version\":99,\"Characters\":[]}","{\"Version\":1,\"Characters\":[null]}"})
            {
                File.WriteAllText(path,corrupt);
                Check(!new CharacterNames(brokenRoot).NeedsLanguage("ja",roster),"A malformed or unsupported writable cache cannot erase bundled names");
            }
            File.WriteAllText(path,JsonSerializer.Serialize(new CharacterNameCatalog(1,[new(1,"ryu",new(){["ja"]="ローカル名"})])));
            var overlay=new CharacterNames(brokenRoot);Localization.Apply("ja");
            Check(overlay.Display(1,"raw")=="ローカル名" && !overlay.NeedsLanguage("en",roster),"Local updates override only their fields while bundled names fill the rest");
            Localization.Apply("en");Check(overlay.Display(1,"raw")=="Ryu","A partial local entry keeps its bundled English name");

            var unknown=new CharacterTranslation(9999,"future_fighter",new(){["en"]="Future Fighter",["zh-Hans"]="新角色",["zh-Hant"]="新角色",["ja"]="新キャラクター"});
            var expanded=shipped with {Characters=[..shipped.Characters,unknown]};
            var refreshRoot=Root();var profiles=new ProfileCache(refreshRoot);var live=new Source(expanded);var changes=0;
            var idle=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var updater=new CharacterNameUpdater(profiles.Names,()=>(live,Owner),token=>idle.Task.WaitAsync(token),()=>changes++);
            live.Observe=updater.Observe;
            await profiles.RefreshMetadataAsync(live,Owner,CancellationToken.None);
            Check(live.NameRequests.Count==0 && profiles.Profile(Owner)!=null,"A foreground profile refresh finishes before silent name downloads are allowed to run");
            idle.SetResult();await updater.PendingWork;
            Check(live.NameRequests.SequenceEqual(["en","zh-Hant","ja"]) && changes==1,"An unseen roster ID runs one silent update and reuses the Simplified page");
            Check(File.Exists(Path.Combine(refreshRoot,"character-names.json")),"New character names are persisted atomically in the local cache");
            profiles=new ProfileCache(refreshRoot);live.NameRequests.Clear();
            updater=new(profiles.Names,()=>(live,Owner),_=>Task.CompletedTask,()=>changes++);live.Observe=updater.Observe;
            await profiles.RefreshMetadataAsync(live,Owner,CancellationToken.None);await updater.PendingWork;
            Check(live.NameRequests.Count==0,"Later refreshes and application restarts reuse the newly saved mapping");
            foreach(var language in CharacterNames.Languages)
            {
                Localization.Apply(language);
                Check(profiles.Names.Display(9999,"raw")==unknown.Names[language],"The new character's four translations survive restart");
            }
            var retryRoot=Root();var retry=new Source(expanded){FailLanguage="zh-Hant"};var retryProfiles=new ProfileCache(retryRoot);
            var at=DateTimeOffset.UtcNow;
            var retryUpdater=new CharacterNameUpdater(retryProfiles.Names,()=>(retry,Owner),_=>Task.CompletedTask,()=>{},()=>at);retry.Observe=retryUpdater.Observe;
            await retryProfiles.RefreshMetadataAsync(retry,Owner,CancellationToken.None);
            await retryUpdater.PendingWork;
            Check(retry.NameRequests.SequenceEqual(["en","zh-Hant"]) && retryProfiles.Profile(Owner)!=null,"A name-service failure retains downloaded languages without blocking profile refresh");
            retry.NameRequests.Clear();await retryProfiles.RefreshMetadataAsync(retry,Owner,CancellationToken.None);await retryUpdater.PendingWork;
            Check(retry.NameRequests.Count==0,"Repeated observations during the cooldown do not hammer a failed name service");
            retry.FailLanguage=null;at=at.AddMinutes(6);
            await retryProfiles.RefreshMetadataAsync(retry,Owner,CancellationToken.None);
            await retryUpdater.PendingWork;
            Check(retry.NameRequests.SequenceEqual(["zh-Hant","ja"]),"A later observation resumes only the missing translations after the cooldown");
            var cardRoot=Root();var cardProfiles=new ProfileCache(cardRoot);var badCard=new Source(expanded){FailCard=true};
            var cardUpdater=new CharacterNameUpdater(cardProfiles.Names,()=>(badCard,Owner),_=>Task.CompletedTask,()=>{});badCard.Observe=cardUpdater.Observe;
            try {await cardProfiles.RefreshMetadataAsync(badCard,Owner,CancellationToken.None);throw new Exception("Expected card failure");}catch(IOException) { }
            await cardUpdater.PendingWork;
            Localization.Apply("ja");
            Check(new CharacterNames(cardRoot).Display(9999,"raw")==unknown.Names["ja"],"An unrelated account-card failure does not prevent the new-character update");

            var foreignNames=new CharacterNames(Root());var foreignSource=new Source(expanded);var opened=0;var updated=0;
            var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var foreignUpdater=new CharacterNameUpdater(foreignNames,()=>{opened++;return(foreignSource,Owner);},token=>start.Task.WaitAsync(token),()=>updated++);
            var opponentBattle=new Battle {OwnerId="9999999999",CharacterId=1,OpponentCharacterId=9999};
            var observed=CharacterObservation.Battles([opponentBattle,opponentBattle with {CharacterId=9999,OpponentCharacterId=1}]);
            Check(observed.Ids.ToHashSet().SetEquals([1,9999]),"Name discovery collects both sides of another player's matches and deduplicates IDs");
            foreignUpdater.Observe(observed);foreignUpdater.Observe(observed);foreignUpdater.Observe(new([9999]));
            start.SetResult();await foreignUpdater.PendingWork;
            Check(opened==1 && updated==1 && foreignSource.NameRequests.SequenceEqual(["zh-Hans","en","zh-Hant","ja"]),"Foreign matches, pagination and rank observations share one background batch independent of the signed-in player's roster");
            Check(foreignNames.Display(9999,"raw")==unknown.Names["ja"],"Silent completion updates the shared names used by all player views");
            foreignUpdater.Observe(observed);await foreignUpdater.PendingWork;
            Check(opened==1,"Observing the same character on another surface never repeats a completed download");
            var missingSource=new Source(shipped);var missingUpdater=new CharacterNameUpdater(new CharacterNames(Root()),()=>(missingSource,Owner),_=>Task.CompletedTask,()=>{});
            missingUpdater.Observe(new([9999]));await missingUpdater.PendingWork;
            missingUpdater.Observe(new([9999]));await missingUpdater.PendingWork;
            Check(missingSource.NameRequests.SequenceEqual(["zh-Hans"]),"An ID not yet present on the official page is retried only after the cooldown");
            var stoppedSource=new Source(expanded);var stoppedUpdater=new CharacterNameUpdater(new CharacterNames(Root()),()=>(stoppedSource,Owner),token=>Task.Delay(Timeout.Infinite,token),()=>{});
            stoppedUpdater.Observe(observed);await stoppedUpdater.CancelAsync();
            Check(stoppedSource.NameRequests.Count==0 && stoppedUpdater.PendingWork.IsCompleted,"Cancelling or switching accounts stops queued name work before opening a source");
        }
        finally {Localization.Apply("zh-Hans");}
        return count;
    }
}
