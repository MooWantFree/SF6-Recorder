using System.Net;
using System.Net.Sockets;
using System.Text;
using Sf6Prototype;

public static class IntegrationChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Assert(bool c, string message) { if (!c) throw new Exception(message); count++; }
        var dir = Path.Combine(Directory.GetCurrentDirectory(), ".local", "checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        const string owner = "3396336472";
        Battle Row(string id, string category, int lp = 1000, int charId = 16) => new()
        { OwnerId = owner, ReplayId = id, Category = category, PlayedAt = 1794337200 + lp, CharacterId = charId, Character = charId == 16 ? "韩蛛俐" : "隆", Opponent = "Test Opponent", MyRounds = 2, OpponentRounds = 1, Lp = lp, Mr = 0, PlayerName = "Test" };
        var store = new BattleStore(Path.Combine(dir, "battles.sqlite"));
        var source = new FakeSource((cat, page) => cat switch
        {
            "rank" => new(page, 3, [Row("R" + (4-page), cat, 1000 + 4-page)]),
            "casual" => new(page, 1, [Row("C1", cat)]),
            "custom" => new(page, 0, []),
            _ => new(page, 1, [Row("H1", cat)])
        });
        var first = await new SyncService(store, source).RunAsync(owner, null, CancellationToken.None);
        Assert(first.Inserted == 5 && first.Pages == 6, "Initial sync must exhaust every category");
        Assert(store.Frontier(owner,"rank") == "R3", "Full sync sets the newest replay as the boundary");
        source.Calls.Clear();
        var again = await new SyncService(store,source).RunAsync(owner,null,CancellationToken.None);
        Assert(again.Inserted == 0 && again.Pages == 4, "Repeat sync must fetch only one page per category");
        var failed = new FakeSource((cat,page) => cat == "rank" && page == 1
            ? new(1,3,[Row("R5",cat,1005)]) : throw new IOException("Simulated network failure"));
        try { await new SyncService(store,failed).RunAsync(owner,null,CancellationToken.None); throw new Exception("Expected error"); }
        catch (IOException) { }
        Assert(store.Frontier(owner,"rank") == "R3", "Partial sync cannot advance the checkpoint");
        Assert(store.Query(new(owner)).Count == 6, "Completed pages must survive a later network error");
        var resume = new FakeSource((cat,page) => cat == "rank"
            ? new(page,3,[Row("R"+(6-page),cat,1006-page)]) : source.Get(cat,page));
        var resumed = await new SyncService(store,resume).RunAsync(owner,null,CancellationToken.None);
        Assert(resume.Calls.Count(c=>c.Category=="rank") == 3, "An already saved partial page must not stop backfill before the committed checkpoint");
        Assert(resumed.Inserted == 1 && store.Query(new(owner)).Count == 7, "Resume must fill the missing middle match without duplicates");
        Assert(store.Query(new(owner,Category:"rank")).Count == 5, "SQL category filter");
        Assert(store.Query(new(owner,Search:"R4")).Single().ReplayId == "R4", "SQL replay search");
        Assert(store.Query(new(owner,Search:"% OR 1=1")).Count == 0, "Search must use bound literal values");
        Assert(store.Query(new(owner,From:1794338204,Until:1794338205)).Single().ReplayId == "R4", "Date boundary uses inclusive start and exclusive end");
        var before=store.Query(new(owner)).Count;
        try { store.SavePage([Row("ROLLBACK","rank"), Row("BAD","rank") with { OwnerId=null! }]); throw new Exception("Expected constraint violation"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        Assert(store.Query(new(owner)).Count==before,"Page insertion must be atomic");
        var legacyMatch=store.Query(new(owner,Search:"R4")).Single();
        Assert(store.SavePage([legacyMatch with {OpponentLeagueRank=23,OpponentMasterLeague=0}])==0,"Backfilling the other player's rank does not count an existing replay as newly inserted");
        var enriched=store.Query(new(owner,Search:"R4")).Single();
        Assert(enriched.OpponentLeagueRank==23 && enriched.OpponentMasterLeague==0 && enriched.Lp==legacyMatch.Lp,"Old replay JSON gains only the missing opponent rank metadata");
        store.SavePage([legacyMatch with {OpponentLeagueRank=24,OpponentMasterLeague=40}]);
        Assert(store.Query(new(owner,Search:"R4")).Single().OpponentLeagueRank==23,"Known historical rank values are not overwritten by a later response");
        store.SavePage([legacyMatch with {MasterRatingRanking=500,OpponentMasterRatingRanking=501}]);
        var rankedReplay=store.Query(new(owner,Search:"R4")).Single();
        Assert(rankedReplay.MasterRatingRanking==500 && rankedReplay.OpponentMasterRatingRanking==501 && rankedReplay.Lp==legacyMatch.Lp,"Available leaderboard positions enrich an existing replay without changing its scores");
        store.SavePage([legacyMatch with {MasterRatingRanking=1,OpponentMasterRatingRanking=2}]);
        Assert(store.Query(new(owner,Search:"R4")).Single().MasterRatingRanking==500,"Later metadata does not overwrite a retained historical leaderboard position");
        const string guest = "1234567890";
        var navigation = new PlayerNavigation(); navigation.SetHome(new(owner, "Me"));
        Assert(navigation.IsHome && !navigation.CanGoBack, "Player browsing starts at the account's own archive");
        Assert(navigation.Visit(new(guest, "Other")) && navigation.Current.Id==guest && navigation.Home.Id==owner, "Visiting another player changes the viewed UID without changing the account");
        Assert(!navigation.Visit(new(guest, "Other")), "Repeated clicks on the viewed player do not add navigation or trigger another fetch");
        navigation.SetHome(new(owner, "Renamed me"), preserveView:true);
        Assert(navigation.Current.Id==guest && navigation.Home.Name=="Renamed me", "Login renewal preserves the viewed opponent");
        navigation.Visit(new("8888888888", "Third")); navigation.Back();
        Assert(navigation.Current.Id==guest && navigation.CanGoBack, "Nested opponent browsing returns one player at a time");
        navigation.Back();
        Assert(navigation.IsHome && navigation.Current.Name=="Renamed me" && !navigation.CanGoBack, "Returning home uses the updated account identity and does not duplicate history");
        navigation.Visit(new(guest, "Other")); navigation.ReturnHome();
        Assert(navigation.IsHome && navigation.CanGoBack && !navigation.CanGoForward, "The home action is a normal history entry that can go back to the opponent");
        navigation.Visit(new(guest, "Other")); navigation.SetHome(new("9999999999", "Second account"));
        Assert(navigation.IsHome && navigation.Current.Id=="9999999999" && !navigation.CanGoBack, "Switching the signed-in account resets the browsing context");
        try { navigation.Visit(new("../invalid", "Invalid")); throw new Exception("Expected invalid UID"); } catch(ArgumentException) { count++; }
        Assert(navigation.Current.Id=="9999999999" && !navigation.CanGoBack, "An invalid player link does not change navigation");
        navigation.UpdateName("Updated nickname"); navigation.UpdateName("");
        Assert(navigation.Current.Name=="Updated nickname" && navigation.Home.Name=="Updated nickname", "The current nickname refreshes without being erased by missing profile data");

        var uidStore = new BattleStore(Path.Combine(dir, "uid-search.sqlite"));
        uidStore.SavePage([
            Row("UID-MATCH", "rank") with {OpponentId=guest, Opponent="Alpha"},
            Row("SECOND-MATCH", "custom") with {OpponentId="8888888888", Opponent="Beta"},
            Row("UID-MATCH", "rank") with {OwnerId=guest, OpponentId=owner, Opponent="Me"}
        ]);
        Assert(uidStore.Query(new(owner,Search:" 1234567890 ")).Single().ReplayId=="UID-MATCH", "UID search accepts a full UID and trims whitespace");
        Assert(uidStore.Query(new(owner,Search:"456789")).Single().OpponentId==guest, "UID search supports partial matching");
        Assert(uidStore.Query(new(owner,Category:"custom",Search:guest)).Count==0, "UID search combines with category filters");
        Assert(uidStore.Query(new(owner,Search:owner)).Count==0 && uidStore.Query(new(guest,Search:owner)).Single().OwnerId==guest, "UID searches stay in the viewed archive and match the opponent, not the owner");
        Assert(uidStore.Query(new(owner,Search:"alpha")).Single().ReplayId=="UID-MATCH" && uidStore.Query(new(owner,Search:"second-match")).Count==1, "Nickname and replay searches still work case-insensitively");
        Assert(uidStore.Query(new(owner,Search:"' OR 1=1 --")).Count==0, "UID search remains a parameterized literal search");

        Assert(store.LastCompletedSync(owner)!=null && store.LastCompletedSync(guest)==null, "Only a completed sync of the selected UID counts as its initial load");
        var guestSource = new FakeSource((category,page) => new(page,1,category=="rank" ? [Row("R3",category,2000) with {OwnerId=guest,OpponentId=owner}] : []));
        var guestSync = await new SyncService(store,guestSource).RunAsync(guest,null,CancellationToken.None);
        Assert(guestSync.Inserted==1 && guestSource.Owners.All(id=>id==guest), "Every category is requested for the viewed UID");
        Assert(store.Query(new(owner)).Count==before && store.Query(new(guest)).Single().Lp==2000, "One replay can be saved independently from two players' perspectives without overwriting own scores");
        Assert(store.LastCompletedSync(guest)!=null && store.Frontier(guest,"rank")=="R3" && store.Frontier(owner,"rank")=="R5", "Opponent history has its own sync completion and incremental boundary");
        const string emptyPlayer="7777777777";
        var emptySource=new FakeSource((category,page)=>new(page,0,[]));
        await new SyncService(store,emptySource).RunAsync(emptyPlayer,null,CancellationToken.None);
        Assert(store.LastCompletedSync(emptyPlayer)!=null && store.Query(new(emptyPlayer)).Count==0, "A successful empty history is cached to avoid fetching again on each visit");
        var incomplete=store.BeginRun("6666666666"); store.FinishRun(incomplete,"cancelled",0,0,"cancelled");
        Assert(store.LastCompletedSync("6666666666")==null, "Cancelled initial visits remain eligible for retry");
        var stats=BattleStore.Summaries([Row("S1","rank"), Row("S2","rank") with {MyRounds=1,OpponentRounds=2}, Row("S3","rank") with {MyRounds=1,OpponentRounds=1}, Row("S4","rank") with {MyRounds=null}]).Single();
        Assert(stats.WinRate=="33.3%", "Win rate includes draws and excludes unknown outcomes");
        var headToHead=new[]
        {
            Row("HISTORY1","rank") with {OpponentId="1234567890",Opponent="Old name",PlayedAt=1},
            Row("HISTORY2","custom") with {OpponentId="1234567890",Opponent="New name",PlayedAt=2,MyRounds=1,OpponentRounds=2},
            Row("HISTORY3","casual") with {OpponentId="1234567890",PlayedAt=3,MyRounds=1,OpponentRounds=1},
            Row("HISTORY4","hub") with {OpponentId="1234567890",PlayedAt=4,MyRounds=null},
            Row("WRONG-OWNER","rank") with {OwnerId="9999999999",OpponentId="1234567890"},
            Row("SAME-NAME","rank") with {OpponentId="8888888888",Opponent="Old name"}
        };
        var history=OpponentHistory.From(headToHead.Append(headToHead[0]),owner,"1234567890");
        Assert(history.Total==4 && history.Wins==1 && history.Losses==1,"Local opponent history uses account and opponent IDs, spans modes, and deduplicates replays");
        Assert(history.Draws==1 && history.Unknown==1 && history.WinRate=="33.3%","Head-to-head win rate includes draws and excludes unknown outcomes");
        Assert(history.Matches.Select(b=>b.ReplayId).SequenceEqual(["HISTORY4","HISTORY3","HISTORY2","HISTORY1"]),"History exposes every meeting in reverse chronological order to fill the available viewport");
        Assert(OpponentHistory.From(headToHead,owner,"").Total==0,"Missing opponent IDs must never combine unrelated players");
        Assert(OpponentHistory.From([headToHead[3]],owner,"1234567890").WinRate=="—","An entirely undecided history has no win rate");
        Assert(OpponentHistory.From([headToHead[0]],owner,"1234567890") is {Total:1,Matches.Count:1},"A sole match is included in its own history");
        Assert(OpponentHistory.From(headToHead.Take(2),owner,"1234567890").Matches.Select(b=>b.ReplayId).SequenceEqual(["HISTORY2","HISTORY1"]),"Two meetings remain complete and ordered");
        Assert(OpponentHistory.From([],owner,"1234567890") is {Total:0,Matches.Count:0},"An empty archive has an empty history list");
        var walk="HISTORY4";
        foreach(var expected in new[]{"HISTORY3","HISTORY2","HISTORY1"})
        {
            var window=history.Matches;
            var index=window.ToList().FindIndex(b=>b.ReplayId==walk);
            walk=window[index+1].ReplayId;
            Assert(walk==expected,"Clicking the next older neighbor can traverse the complete history without skipping matches");
        }
        var tied=headToHead.Take(4).Select(b=>b with {PlayedAt=100}).Reverse().Append(headToHead[1] with {PlayedAt=100});
        Assert(OpponentHistory.From(tied,owner,"1234567890").Matches.Select(b=>b.ReplayId).SequenceEqual(["HISTORY1","HISTORY2","HISTORY3","HISTORY4"]),"Equal timestamps use replay IDs for deterministic ordering and duplicate rows are removed");
        var points=BattleStore.Scores([Row("A","rank",1000),Row("B","rank",1050)]);
        Assert(points[0].LpDelta==50 && points[0].LpAfter==1050 && points[1].LpDelta==null, "Recorded scores are pre-match: a match settles at the next same-character record");
        var other=BattleStore.Scores([Row("A","rank",1000),Row("B","rank",1050,1)]);
        Assert(other.All(p=>p.LpDelta==null),"Never compute changes across characters");
        var mixed=BattleStore.Scores([Row("A","rank",1000),Row("X","custom",1020),Row("B","rank",1050)]);
        Assert(mixed.Count==2 && mixed[0].LpDelta==50,"Only ranked matches settle LP; other modes are skipped");
        var lost=Row("L","rank",1100) with {MyRounds=0,OpponentRounds=2};
        CurrentScores Now(int lp,long at)=>new(DateTimeOffset.FromUnixTimeSeconds(at),14,[new CharacterRank(16,"韩蛛俐",lp,0,30,0)]);
        Assert(BattleStore.Scores([lost],Now(1060,lost.PlayedAt+60)).Single() is {LpDelta:-40,Estimated:true},"The newest match is estimated from current profile scores");
        Assert(BattleStore.Scores([lost],Now(1060,lost.PlayedAt-60)).Single().LpDelta==null,"A profile captured before the match cannot settle it");
        Assert(BattleStore.Scores([lost],Now(1150,lost.PlayedAt+60)).Single() is {LpDelta:null,Estimated:false},"An estimate contradicting the result is discarded");
        var mrA=Row("MA","rank",25000) with {Mr=1500,Season=13,MasterLeague=36};
        var mrB=Row("MB","rank",25010) with {Mr=1510,Season=13,MasterLeague=40};
        Assert(BattleStore.Scores([mrA,mrB])[0].MrDelta==10,"A Master badge change must not be mistaken for a season boundary");
        Assert(BattleStore.Scores([mrA,mrB with {Season=14}])[0].MrDelta==null,"MR differences must not cross actual seasons");
        Assert(BattleStore.Scores([mrA with {Season=null},mrB with {Season=null}])[0].MrDelta==null,"Unknown seasons cannot establish continuous MR");
        var migrationPath=Path.Combine(dir,"migration.sqlite"); var legacy=new BattleStore(migrationPath); legacy.SavePage([mrA with {Season=36}]);
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={migrationPath}"))
        {
            db.Open(); using var cmd=db.CreateCommand();
            cmd.CommandText="UPDATE battles SET data_json=json_remove(data_json,'$.MasterLeague'); PRAGMA user_version=1;"; cmd.ExecuteNonQuery();
        }
        var migrated=new BattleStore(migrationPath); var migratedRow=migrated.Query(new(owner)).Single();
        Assert(migratedRow.MasterLeague==36 && migratedRow.Season==null && migratedRow.Mr==1500 && migratedRow.Lp==25000,"Migration preserves scores and corrects only the mislabeled season field");
        migrated.SavePage([mrB]); var reopened=new BattleStore(migrationPath);
        Assert(reopened.Query(new(owner)).Single(b=>b.ReplayId=="MB").Season==13,"Migration runs once and never clears newly saved valid seasons");
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={migrationPath}"))
        {
            db.Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT season FROM score_snapshots WHERE replay_id='MA'";
            Assert(cmd.ExecuteScalar() is DBNull,"Migration also clears the invalid season in score snapshots");
        }

        var vault=new CookieVault(dir);
        var session=new SavedSession(owner,"Test","test-agent","build","direct",[new("buckler_id","secret-canary-415","www.streetfighter.com","/",DateTime.UtcNow.AddHours(1),true,true)]);
        vault.Save(session);
        Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir,"session.dpapi"))).Contains("secret-canary"),"Cookie file must not contain plaintext credentials");
        Assert(vault.Load()?.Cookies.Single().Value=="secret-canary-415","DPAPI cookie persistence round trip");
        var jar=CookieVault.ToContainer(session);
        Assert(jar.GetCookies(new Uri("https://www.streetfighter.com/")).Count==1,"Cookie restored for the target site");
        Assert(jar.GetCookies(new Uri("https://www.google.com/")).Count==0,"Site credentials must not leak to proxy test destination");
        var expired=session with { Cookies=[session.Cookies[0] with { Expires=DateTime.UtcNow.AddHours(-1) }] };
        Assert(CookieVault.ToContainer(expired).Count==0,"Expired cookies are not reused");
        var second = session with { AccountId="1234567890",AccountName="Second",Cookies=[session.Cookies[0] with { Value="second-session" }] };
        vault.Save(second);
        Assert(vault.Accounts().Count==2,"Multiple accounts retain separate encrypted sessions");
        Assert(vault.Activate(owner).Cookies.Single().Value=="secret-canary-415","Switching restores the selected account cookie jar");
        vault.Remove(owner);
        Assert(vault.Load()==null && vault.Accounts().Single().AccountId==second.AccountId,"Logout removes only the selected active account");
        Assert(vault.Activate(second.AccountId).Cookies.Single().Value=="second-session","Another account remains usable after logout");
        Assert(store.Query(new(owner)).Count==before,"Logging out never removes the local battle archive");
        try { vault.Remove("../session"); throw new Exception("Expected invalid id"); } catch(ArgumentException) { count++; }
        var priorMonth=DateRanges.Preset("previous",new DateTime(2024,3,10));
        Assert(priorMonth.Start==new DateTime(2024,2,1) && priorMonth.End==new DateTime(2024,2,29),"Previous-month preset includes leap day");
        var recentDays=DateRanges.Preset("90",new DateTime(2026,1,15));
        Assert((recentDays.End-recentDays.Start)?.TotalDays==89,"90-day preset includes both boundary dates");
        var reversed=DateRanges.Normalize(new DateTime(2026,11,11),new DateTime(2026,11,1));
        Assert(reversed.Start.Day==1 && reversed.End.Day==11,"Reverse date selection normalizes range");
        var scoreSeries=Enumerable.Range(0,30).Select(i=>new ScorePoint(1000+i/2,"time",1000+i,null,null,null,i.ToString("D2")));
        var recentScores=DateRanges.Recent(scoreSeries,20);
        Assert(recentScores.Count==20 && recentScores[0].Lp==1010 && recentScores[^1].Lp==1029,"Default score window selects the latest 20 records, including equal timestamps");
        Assert(DateRanges.ScoreDifference(recentScores.Select(p=>p with {LpDelta=1}).ToList(),false)==20,"LP interval change sums the settlements of the selected matches");
        Assert(DateRanges.ScoreDifference(recentScores,false)==null,"No known settlement means no interval change");
        var mrWindow=new[] { recentScores[0] with {Mr=1500,MrDelta=10},recentScores[1] with {Mr=1510,MrDelta=-4} };
        Assert(DateRanges.ScoreDifference(mrWindow,true)==6,"MR interval uses a continuous season");
        Assert(DateRanges.ScoreDifference([mrWindow[0],mrWindow[1] with {MrDelta=null}],true)==null,"MR interval must not show a misleading change across seasons");
        var playJson="{\"pageProps\":{\"fighter_banner_info\":{\"personal_info\":{\"short_id\":3396336472,\"fighter_id\":\"Me\"}},\"play\":{\"current_season_id\":14,\"season_ids\":[14],\"character_league_infos\":[{\"character_id\":16,\"character_name\":\"韩蛛俐\",\"league_info\":{\"league_point\":23539,\"league_rank\":34,\"master_rating\":0,\"master_league\":0}}]}}}";
        var cache=new ProfileCache(dir); cache.SaveScores(owner,playJson,DateTimeOffset.UnixEpoch.AddDays(1));
        Assert(cache.Scores(owner) is {Season:14} saved && saved.Characters.Single().Lp==23539,"Current own scores persist for the newest-match estimate");
        cache.SaveScores(owner,playJson.Replace("3396336472","1111111111"),DateTimeOffset.UtcNow);
        Assert(cache.Scores(owner)!.FetchedAt==DateTimeOffset.UnixEpoch.AddDays(1),"Another player's profile never replaces own current scores");
        cache.SaveScores(guest,playJson.Replace(owner,guest).Replace("23539","18000"),DateTimeOffset.UnixEpoch.AddDays(2));
        Assert(cache.Scores(guest)!.Characters.Single().Lp==18000 && cache.Scores(owner)!.Characters.Single().Lp==23539,"Current score snapshots are isolated by the viewed player's UID");
        const string rosterJson="""{"pageProps":{"play":{"character_league_infos":[{"character_id":16,"character_name":"韩蛛俐","character_tool_name":"juri"},{"character_id":35,"character_name":"New fighter","character_tool_name":"new_fighter"},{"character_id":36,"character_name":"Bad path","character_tool_name":"../bad"}]}}}""";
        var roster=ProfileCache.ParseCharacters(rosterJson);
        Assert(roster.Count==2 && roster[1].Id==35,"Profile roster discovers new fighters and rejects unsafe asset names");
        const string rankRows="""[{"character_id":16,"character_name":"韩蛛俐","league_info":{"league_point":23862,"league_rank":35,"master_rating":0,"master_league":0}},{"character_id":1,"character_name":"隆","league_info":{"league_point":25000,"league_rank":36,"master_rating":1800,"master_league":40}},{"character_id":7,"character_name":"春丽","league_info":{"league_point":0,"league_rank":0,"master_rating":0,"master_league":0}}]""";
        var profileJson="{\"pageProps\":{\"fighter_banner_info\":{\"personal_info\":{\"short_id\":4293037925,\"fighter_id\":\"Test opponent\"}},\"play\":{\"current_season_id\":14,\"season_ids\":[14,13,13,0,-1],\"character_league_infos\":"+rankRows+"}}}";
        var opponent=RankProfiles.ParseProfile(profileJson,"4293037925");
        Assert(opponent.OwnerId=="4293037925" && opponent.Characters.Count==3,"One profile response supplies every opponent character");
        Assert(opponent.Seasons.SequenceEqual([14,13,0]) && opponent.CurrentSeason==14,"Season choices come from the server, not guessed IDs or Master badge values");
        Assert(opponent.Characters[0].RankName=="Diamond V" && opponent.Characters[0].MrText=="—","Roman rank stars and absent MR are distinct");
        Assert(opponent.Characters[1].RankName=="Ultimate Master" && opponent.Characters[1].MasterLeague==40,"MR uses the supplied thresholds while preserving the original badge metadata");
        Assert(!opponent.Characters[2].Ranked && opponent.Characters[2].RankName=="未定级","Zero-filled historical entries are not ranked fighters");
        Assert(!(opponent.Characters[0] with {LeagueRank=39,Lp=0}).Ranked,"Placement records are not completed ranks");
        Assert(RankProfiles.ParseSeason("{\"response\":{\"character_league_infos\":"+rankRows+"}}").Count==3,"Season and peak-MR responses include all characters");
        try { RankProfiles.ParseProfile(profileJson,"1111111111"); throw new Exception("Expected identity mismatch"); } catch(InvalidDataException) { count++; }
        try { RankProfiles.ParseSeason("{\"response\":{\"error\":\"failed\"}}"); throw new Exception("Expected invalid payload"); } catch(InvalidDataException) { count++; }
        var config=new ConnectionSettings {UseProxy=true,ProxyAddress="http://127.0.0.1:7890"};
        Assert(config.BrowserArguments.Contains("--proxy-server=http://127.0.0.1:7890"),"Browser uses the selected proxy");
        using(var h=config.CreateHandler()) Assert(h.UseProxy && h.Proxy!.GetProxy(new Uri("https://www.streetfighter.com"))!.Port==7890,"HTTP uses the same proxy");
        using(var h=new ConnectionSettings().CreateHandler()) Assert(!h.UseProxy,"Direct mode must not inherit ambient proxies");
        try { (config with {ProxyAddress="http://user:password@localhost:7890"}).ValidateProxy(); throw new Exception("Expected rejection"); }
        catch(ArgumentException) { count++; }
        // Real loopback proxy verifies HttpClient routing without sending a request to Capcom.
        var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        try
        {
            var port=((IPEndPoint)listener.LocalEndpoint).Port;
            var accept=Task.Run(async () =>
            {
                using var socket=await listener.AcceptTcpClientAsync(); using var stream=socket.GetStream();
                using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                var line=await reader.ReadLineAsync(); while(!string.IsNullOrEmpty(await reader.ReadLineAsync())){}
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                return line;
            });
            using var http=new HttpClient((config with {ProxyAddress=$"http://127.0.0.1:{port}"}).CreateHandler()) {Timeout=TimeSpan.FromSeconds(5)};
            using var reply=await http.GetAsync("http://routing-test.invalid/test");
            Assert(reply.StatusCode==HttpStatusCode.NoContent && (await accept)!.Contains("http://routing-test.invalid/test"),"A custom proxy actually carries the request");
        }
        finally {listener.Stop();}
        return count;
    }
}
public sealed class FakeSource(Func<string,int,BattlePage> get) : IBattleSource
{
    public List<(string Category,int Page)> Calls {get;}=[];
    public List<string> Owners {get;}=[];
    public int Requests=>Calls.Count;
    public BattlePage Get(string category,int page)=>get(category,page);
    public Task<BattlePage> FetchPageAsync(string owner,string category,int page,CancellationToken token)
    {token.ThrowIfCancellationRequested();Calls.Add((category,page));Owners.Add(owner);return Task.FromResult(get(category,page));}
}
