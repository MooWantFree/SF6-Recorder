using System.Text.Json;
using Sf6Prototype;

internal static class DailyProfileRefreshChecks
{
    private sealed class Source : IProfileSource
    {
        public List<string> Calls {get;}=[];
        public bool FailCard {get;set;}
        public bool FailPlay {get;set;}
        public Task<string> FetchCardAsync(string owner,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add("card");
            return FailCard ? Task.FromException<string>(new IOException("offline")) : Task.FromResult("{\"fighter_name\":\"Fixture\",\"icon_file_name\":\"fixture\"}");
        }
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Calls.Add("play");
            if(FailPlay) return Task.FromException<string>(new IOException("offline"));
            return Task.FromResult(JsonSerializer.Serialize(new {pageProps=new {
                fighter_banner_info=new {personal_info=new {short_id=owner,fighter_id="Fixture"}},
                play=new {current_season_id=1,season_ids=new[]{1},character_league_infos=new[]{new {
                    character_id=1,character_name="隆",character_tool_name="ryu",
                    league_info=new {league_point=12000,league_rank=24,master_rating=0,master_league=0}}}}}}));
        }
    }
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        static DateTimeOffset At(string value)=>DateTimeOffset.Parse(value,System.Globalization.CultureInfo.InvariantCulture);
        var east=TimeZoneInfo.CreateCustomTimeZone("Test +08",TimeSpan.FromHours(8),"Test +08","Test +08");
        var west=TimeZoneInfo.CreateCustomTimeZone("Test -07",TimeSpan.FromHours(-7),"Test -07","Test -07");
        var start=At("2026-11-14T06:00:00+08:00");
        Check(DailyProfileRefresh.Day(start.AddTicks(-1),east)==new DateOnly(2026,11,13),"05:59:59.999 belongs to the previous local refresh day");
        Check(DailyProfileRefresh.Day(start,east)==new DateOnly(2026,11,14),"Exactly 06:00 starts the next local refresh day");
        Check(!DailyProfileRefresh.IsDue(start,start.AddHours(18),east) && !DailyProfileRefresh.IsDue(start,start.AddDays(1).AddTicks(-1),east),"Midnight never resets the profile update quota");
        Check(DailyProfileRefresh.IsDue(start,start.AddDays(1),east),"The next 06:00 permits a new full profile refresh");
        Check(!DailyProfileRefresh.IsDue(start,start.AddDays(-1),east),"A backwards clock change cannot repeat a completed refresh day");
        var sameInstant=At("2026-11-14T13:00:00Z");
        Check(!DailyProfileRefresh.IsDue(start,sameInstant,east) && DailyProfileRefresh.IsDue(start,sameInstant,west),"The user's local timezone determines the boundary, independently of server UTC");
        Check(DailyProfileRefresh.Day(At("2027-01-01T05:59:59+08:00"),east)==new DateOnly(2026,12,31),"The 06:00 boundary works across the year boundary");
        var pacific=TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        Check(!DailyProfileRefresh.IsDue(At("2026-03-07T14:00:00Z"),At("2026-03-08T12:59:59Z"),pacific) &&
            DailyProfileRefresh.IsDue(At("2026-03-07T14:00:00Z"),At("2026-03-08T13:00:00Z"),pacific),"Spring DST still uses local 06:00 rather than a rolling 24 hours");
        Check(!DailyProfileRefresh.IsDue(At("2026-10-31T13:00:00Z"),At("2026-11-01T13:59:59Z"),pacific) &&
            DailyProfileRefresh.IsDue(At("2026-10-31T13:00:00Z"),At("2026-11-01T14:00:00Z"),pacific),"Fall DST still uses local 06:00 on the longer day");
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        const string owner=PagingChecks.Owner;var profiles=new ProfileCache(root);var source=new Source();
        var daily=new DailyProfileRefresh(profiles,owner,start,east);
        Check(daily.Required,"An account with no daily checkpoint performs its first full refresh");
        var avatar=await daily.RefreshAvatarAsync(source,CancellationToken.None);
        var images=await daily.RefreshPlayAsync(source,CancellationToken.None,captureScores:true);
        Check(source.Calls.SequenceEqual(["card","play"]) && avatar.Single().Refresh && images.Count==1,"The first refresh requests the account card, avatar and missing character pictures");
        Check(profiles.Profile(owner)?.DailyRefreshedAt==start,"A successful profile update persists its captured start time");
        profiles=new ProfileCache(root);source.Calls.Clear();
        var later=new DailyProfileRefresh(profiles,owner,start.AddHours(20),east);
        Check(!later.Required && (await later.RefreshAvatarAsync(source,CancellationToken.None)).Count==0 &&
            (await later.RefreshPlayAsync(source,CancellationToken.None)).Count==0 && source.Calls.Count==0,
            "Restarting or refreshing after midnight makes no account or image requests within the same refresh day");
        profiles.RememberIdentity(new(owner,"Renamed"));
        Check(!new DailyProfileRefresh(new ProfileCache(root),owner,start.AddHours(23),east).Required,"Remembering a player name cannot erase the daily checkpoint");
        Check(new DailyProfileRefresh(profiles,"9876543210",start.AddHours(23),east).Required,"Each account has its own first refresh");
        var crossing=new DailyProfileRefresh(profiles,owner,start.AddDays(1).AddSeconds(-1),east);
        Check(!crossing.Required && new DailyProfileRefresh(profiles,owner,start.AddDays(1),east).Required,"The exact 06:00 boundary is preserved across restarts");
        var fresh=new ProfileCache(Path.Combine(root,"crossing"));var beforeSix=start.AddSeconds(-1);
        var spanning=new DailyProfileRefresh(fresh,owner,beforeSix,east);
        await spanning.RefreshAvatarAsync(source,CancellationToken.None);await spanning.RefreshPlayAsync(source,CancellationToken.None);
        Check(new DailyProfileRefresh(new ProfileCache(Path.Combine(root,"crossing")),owner,start,east).Required,"A request started before 06:00 does not consume the next day's update");
        var retryProfiles=new ProfileCache(Path.Combine(root,"failed"));var failing=new Source {FailPlay=true};
        var interrupted=new DailyProfileRefresh(retryProfiles,owner,start,east);
        await interrupted.RefreshAvatarAsync(failing,CancellationToken.None);
        try {await interrupted.RefreshPlayAsync(failing,CancellationToken.None);throw new Exception("Expected metadata failure");}catch(IOException){}
        Check(new DailyProfileRefresh(new ProfileCache(Path.Combine(root,"failed")),owner,start.AddMinutes(1),east).Required,"An unsuccessful metadata update can be retried instead of suppressing updates for a whole day");
        var store=new BattleStore(Path.Combine(root,"battles.sqlite"));var games=PagingChecks.Games().Take(1).ToArray();
        var battles=new FakeSource((category,page)=>PagingChecks.Page(games,category,page));
        await AccountRefresh.RunAsync(store,battles,owner,CancellationToken.None);
        source.Calls.Clear();var requests=battles.Requests;
        later=new(profiles,owner,start.AddHours(2),east);
        await later.RefreshAvatarAsync(source,CancellationToken.None);
        await AccountRefresh.RunAsync(store,battles,owner,CancellationToken.None);
        await later.RefreshPlayAsync(source,CancellationToken.None);
        Check(battles.Requests==requests+1 && source.Calls.Count==0,"A subsequent refresh requests only the new battle page, not the card or play profile");
        var old=new Battle {OwnerId=owner,ReplayId="OLD",Category="rank",CharacterId=1,PlayedAt=start.ToUnixTimeSeconds(),Lp=12000};
        var latest=old with {ReplayId="NEW",PlayedAt=old.PlayedAt+120,Lp=12050};
        var scores=new CurrentScores(start.AddSeconds(1),1,[new CharacterRank(1,"隆",12050,null,24,0)]);
        var points=BattleStore.Scores([old,latest],scores);
        Check(points.Single(p=>p.ReplayId=="OLD").LpDelta==50 && points.Single(p=>p.ReplayId=="NEW").LpDelta==null,
            "Battle-only updates settle the previous match using the next replay, while an old daily profile cannot settle the new match");
        return count;
    }
}
