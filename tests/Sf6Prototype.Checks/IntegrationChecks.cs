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
        var stats=BattleStore.Summaries([Row("S1","rank"), Row("S2","rank") with {MyRounds=1,OpponentRounds=2}, Row("S3","rank") with {MyRounds=1,OpponentRounds=1}, Row("S4","rank") with {MyRounds=null}]).Single();
        Assert(stats.WinRate=="33.3%", "Win rate includes draws and excludes unknown outcomes");
        var points=BattleStore.Scores([Row("A","rank",1000),Row("B","rank",1050)]);
        Assert(points[1].LpDelta==50 && points[0].LpDelta==null, "Score differences compare saved same-character records");
        var other=BattleStore.Scores([Row("A","rank",1000),Row("B","rank",1050,1)]);
        Assert(other[1].LpDelta==null,"Never compute changes across characters");
        var mrA=Row("MA","rank",25000) with {Mr=1500,Season=13,MasterLeague=36};
        var mrB=Row("MB","rank",25010) with {Mr=1510,Season=13,MasterLeague=40};
        Assert(BattleStore.Scores([mrA,mrB])[1].MrDelta==10,"A Master badge change must not be mistaken for a season boundary");
        Assert(BattleStore.Scores([mrA,mrB with {Season=14}])[1].MrDelta==null,"MR differences must not cross actual seasons");
        Assert(BattleStore.Scores([mrA with {Season=null},mrB with {Season=null}])[1].MrDelta==null,"Unknown seasons cannot establish continuous MR");
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
        Assert(DateRanges.ScoreDifference(recentScores,false)==19,"Displayed LP interval change compares endpoints of the selected window");
        var mrWindow=new[] { recentScores[0] with {Mr=1500,MrDelta=null},recentScores[1] with {Mr=1510,MrDelta=10} };
        Assert(DateRanges.ScoreDifference(mrWindow,true)==10,"MR interval uses a continuous season");
        Assert(DateRanges.ScoreDifference([mrWindow[0],mrWindow[1] with {MrDelta=null}],true)==null,"MR interval must not show a misleading change across seasons");
        const string rosterJson="""{"pageProps":{"play":{"character_league_infos":[{"character_id":16,"character_name":"韩蛛俐","character_tool_name":"juri"},{"character_id":35,"character_name":"New fighter","character_tool_name":"new_fighter"},{"character_id":36,"character_name":"Bad path","character_tool_name":"../bad"}]}}}""";
        var roster=ProfileCache.ParseCharacters(rosterJson);
        Assert(roster.Count==2 && roster[1].Id==35,"Profile roster discovers new fighters and rejects unsafe asset names");
        const string rankRows="""[{"character_id":16,"character_name":"韩蛛俐","league_info":{"league_point":23862,"league_rank":35,"master_rating":0,"master_league":0}},{"character_id":1,"character_name":"隆","league_info":{"league_point":25000,"league_rank":36,"master_rating":1800,"master_league":40}},{"character_id":7,"character_name":"春丽","league_info":{"league_point":0,"league_rank":0,"master_rating":0,"master_league":0}}]""";
        var profileJson="{\"pageProps\":{\"fighter_banner_info\":{\"personal_info\":{\"short_id\":4293037925,\"fighter_id\":\"Test opponent\"}},\"play\":{\"current_season_id\":14,\"season_ids\":[14,13,13,0,-1],\"character_league_infos\":"+rankRows+"}}}";
        var opponent=RankProfiles.ParseProfile(profileJson,"4293037925");
        Assert(opponent.OwnerId=="4293037925" && opponent.Characters.Count==3,"One profile response supplies every opponent character");
        Assert(opponent.Seasons.SequenceEqual([14,13,0]) && opponent.CurrentSeason==14,"Season choices come from the server, not guessed IDs or Master badge values");
        Assert(opponent.Characters[0].RankName=="Diamond 5" && opponent.Characters[0].MrText=="—","Diamond rank and absent MR are distinct");
        Assert(opponent.Characters[1].RankName=="Master" && opponent.Characters[1].MasterLeague==40,"Master badge is preserved independently of the season");
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
    public int Requests=>Calls.Count;
    public BattlePage Get(string category,int page)=>get(category,page);
    public Task<BattlePage> FetchPageAsync(string owner,string category,int page,CancellationToken token)
    {token.ThrowIfCancellationRequested();Calls.Add((category,page));return Task.FromResult(get(category,page));}
}
