using Sf6Prototype;

public static class RankQueryChecks
{
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); count++; }
        async Task Fails<T>(Func<Task> operation,string message) where T:Exception
        {
            try { await operation(); } catch(T) { count++; return; }
            throw new Exception(message);
        }
        const string owner="3396336472";
        var source=new FakeSource();
        var session=new RankQuerySession(source,owner);
        var token=CancellationToken.None;
        var profile=await session.LoadProfileAsync(token);
        Check(ReferenceEquals(profile,await session.LoadProfileAsync(token)) && source.ProfileCalls==1,"A window reuses its player profile and season list");
        var current=await session.GetAsync(new(14),token);
        Check(current.Characters.Single().Lp==25000 && source.Calls.Count==0,"Current-season ranks reuse the initial profile without another request");

        var highest=await session.GetAsync(new(-1),token);
        Check(source.Calls.Single()==(owner,new RankQuery(-1)) && highest.Characters.Single() is {Lp:26000,Mr:null,MasterLeague:null,MasterRatingRanking:null},"All-time highest LP uses one special query and does not present unrelated MR values");
        Check(ReferenceEquals(highest,await session.GetAsync(new(-1),token)),"Returning to highest LP reuses the result and its original fetch timestamp");
        var eleven=await session.GetAsync(new(11),token);
        await session.GetAsync(new(12),token);
        Check(ReferenceEquals(eleven,await session.GetAsync(new(11),token)) && source.Calls.Count(c=>c.Query==new RankQuery(11))==1,"Season 11 to 12 to 11 fetches season 11 only once");
        var peak=await session.GetAsync(new(11,true),token);
        Check(!ReferenceEquals(peak,eleven) && peak.Characters.Single().Mr==1800 && source.Calls.Count(c=>c.Query.Season==11)==2,"Season standings and peak MR have separate cache entries");
        await session.GetAsync(new(11,true),token);
        Check(source.Calls.Count(c=>c.Query==new RankQuery(11,true))==1,"Peak MR is cached too");

        source.Response=_=>"{\"response\":{\"character_league_infos\":[]}}";
        var empty=await session.GetAsync(new(10),token);
        Check(empty.Characters.Count==0 && ReferenceEquals(empty,await session.GetAsync(new(10),token)),"Successful empty seasons are cached without repeat requests");
        source.Response=_=>throw new HttpRequestException("offline");
        await Fails<HttpRequestException>(()=>session.GetAsync(new(9),token),"Network failures must propagate");
        source.Response=_=>"{}";
        await Fails<InvalidDataException>(()=>session.GetAsync(new(9),token),"Invalid responses must not be accepted as empty seasons");
        source.Response=_=>SeasonJson;
        await session.GetAsync(new(9),token);
        Check(source.Calls.Count(c=>c.Query==new RankQuery(9))==3,"Failed network and parsing attempts do not poison the cache");

        using var cancellation=new CancellationTokenSource();
        source.Response=_=> { cancellation.Cancel(); return SeasonJson; };
        await Fails<OperationCanceledException>(()=>session.GetAsync(new(8),cancellation.Token),"Cancellation after a response must not cache it");
        source.Response=_=>SeasonJson;
        await session.GetAsync(new(8),token);
        Check(source.Calls.Count(c=>c.Query==new RankQuery(8))==2,"A cancelled query is fetched again on retry");
        var calls=source.Calls.Count;
        await Fails<OperationCanceledException>(()=>session.GetAsync(new(11),cancellation.Token),"Cancelled windows must not serve cached results");
        Check(source.Calls.Count==calls,"A pre-cancelled query never reaches the network");

        await session.LoadProfileAsync(token,refresh:true);
        Check(source.ProfileCalls==2 && !ReferenceEquals(eleven,await session.GetAsync(new(11),token)),"Explicit refresh reloads the profile and invalidates old season results");
        session.Clear();
        Check(session.Profile==null && !ReferenceEquals(highest,await session.GetAsync(new(-1),token)),"Closing the window clears both profile and query results");
        var anotherWindow=new RankQuerySession(source,owner);
        var before=source.Calls.Count;
        await anotherWindow.GetAsync(new(-1),token);
        Check(source.Calls.Count==before+1,"Opening another window starts with a fresh cache");
        var otherPlayer=new RankQuerySession(source,"1234567890");
        await otherPlayer.GetAsync(new(-1),token);
        Check(source.Calls.Last().Owner=="1234567890","Queries remain isolated by the window's player identity");
        await Fails<InvalidDataException>(()=>otherPlayer.LoadProfileAsync(token),"A mismatched player profile must be rejected");
        Check(otherPlayer.Profile==null,"Rejected profiles do not populate the window cache");

        before=source.Calls.Count;
        foreach(var invalid in new[]{new RankQuery(-2),new RankQuery(-1,true),new RankQuery(0,true)})
            await Fails<ArgumentException>(()=>session.GetAsync(invalid,token),"Unsupported rank filters must be rejected");
        Check(source.Calls.Count==before,"Invalid filters never trigger requests");
        await session.GetAsync(new(0),token);
        Check(source.Calls.Last().Query==new RankQuery(0) && !new RankQuery(0).HighestLp,"Season zero remains a valid LP season, separate from all-time highest");
        return count;
    }

    private const string SeasonJson="""
        {"response":{"character_league_infos":[{"character_id":1,"character_name":"RYU","league_info":
        {"league_point":26000,"league_rank":36,"master_rating":1800,"master_league":42,"master_rating_ranking":800}}]}}
        """;
    private const string ProfileJson="""
        {"pageProps":{"fighter_banner_info":{"personal_info":{"short_id":3396336472,"fighter_id":"Player"}},
        "play":{"current_season_id":14,"season_ids":[14,13,12,11,0],"character_league_infos":[
        {"character_id":1,"character_name":"RYU","league_info":{"league_point":25000,"league_rank":36,"master_rating":1500}}]}}}
        """;
    private sealed class FakeSource:IRankSource
    {
        public List<(string Owner,RankQuery Query)> Calls { get; }=[];
        public int ProfileCalls { get; private set; }
        public Func<RankQuery,string> Response { get; set; }=_=>SeasonJson;
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        { token.ThrowIfCancellationRequested(); ProfileCalls++; return Task.FromResult(ProfileJson); }
        public Task<string> FetchSeasonRanksAsync(string owner,int season,bool peakMr,CancellationToken token)
        { token.ThrowIfCancellationRequested(); var query=new RankQuery(season,peakMr); Calls.Add((owner,query)); return Task.FromResult(Response(query)); }
    }
}
