using System.Text.Json;
using Sf6Prototype;

if(Environment.GetCommandLineArgs().Contains("--match-end-log-bench")) {MatchEndSampleLogChecks.Benchmark();return;}

if(Environment.GetCommandLineArgs().Contains("--archive-read-bench")) {ArchiveReadChecks.Benchmark();return;}

if(Environment.GetCommandLineArgs().Contains("--name-cache-checks"))
{
    var logDirectory=Path.Combine(Environment.CurrentDirectory,".local","checks","name-cache-"+Guid.NewGuid().ToString("N"));
    Diagnostics.Log=new DiagnosticLog(logDirectory);Console.WriteLine("Diagnostics: "+logDirectory);
    for(var attempt=0;attempt<10;attempt++) {try {await BundledCharacterNameChecks.Run();Console.WriteLine($"Name cache pass {attempt+1}");} catch(Exception ex) {Diagnostics.Write("name_cache_check","failed",ex);throw;}}
    return;
}
Localization.Apply("zh");
var assertions = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    assertions++;
}
const string data = """
{"pageProps":{"access_token":"must-not-be-exported","replay_list":[
 {"replay_id":"DEMO","uploaded_at":1794384000,"replay_battle_type_name":"Ranked Match",
  "player1_info":{"character_name":"RYU","round_results":[0,1,0],"player":{"short_id":1234567890,"fighter_id":"Other"}},
  "player2_info":{"character_name":"CHUN-LI","round_results":[1,0,1],"player":{"short_id":3396336472,"fighter_id":"Me"}}}
]}}
""";
var result = BattleParser.Parse(data, "3396336472");
Check(result.HasReplayList && result.Matches.Count == 1, "Expected one match");
Check(result.Matches[0].Player == "Me" && result.Matches[0].Opponent == "Other", "Player two must be recognized as the user");
Check(result.Matches[0].Result == "胜" && result.Matches[0].Score == "2 : 1", "Round results must be from the user's perspective");
Check(!JsonSerializer.Serialize(result).Contains("must-not-be-exported"), "Credentials must not enter persisted match objects");
Check(BattleParser.Parse(data, "9999999999").Matches.Count == 0, "Unrelated players must not count as successful retrieval");
Check(BattleParser.Parse("{\"pageProps\":{\"replay_list\":[]}}", "3396336472").HasReplayList, "An empty battle history is valid data");
Check(!BattleParser.Parse("{\"error\":\"login required\"}", "3396336472").HasReplayList, "A login response is not battle history");
Check(BattleParser.Parse("{\"props\":" + data + "}", "3396336472").Matches.Count == 1, "Next.js page wrapper must be supported");
var previous = new CaptureEvidence("first", "3396336472", "account", DateTimeOffset.UtcNow, 1);
Check(SessionEvidence.Restored(previous, "second", "3396336472", "account", false, 200, 200, true), "Fresh successful requests after restart should verify persistence");
Check(!SessionEvidence.Restored(previous, "first", "3396336472", "account", false, 200, 200, true), "A refresh in the same process is not a restart");
Check(!SessionEvidence.Restored(previous, "second", "3396336472", "account", true, 200, 200, true), "Logging in again must not count as restored login");
Check(!SessionEvidence.Restored(previous, "second", "3396336472", "account", false, 200, 403, true), "Cached records cannot override a failed live request");
Check(!SessionEvidence.Restored(previous, "second", "3396336472", "different", false, 200, 200, true), "Different accounts cannot satisfy persistence");
Check(!SessionEvidence.Restored(previous, "second", "3396336472", "account", false, 200, 200, false), "Missing battle data cannot satisfy persistence");
var scored = data.Replace("\"pageProps\":{", "\"pageProps\":{\"current_page\":1,\"total_page\":1,")
    .Replace("\"character_name\":\"RYU\"", "\"character_id\":1,\"character_name\":\"RYU\",\"league_point\":12000")
    .Replace("\"character_name\":\"CHUN-LI\"", "\"character_id\":7,\"character_name\":\"CHUN-LI\",\"league_point\":12500,\"master_rating\":1500")
    .Replace("3396336472", "3396336472.0");
var parsedPage = MatchParser.Parse(scored, "3396336472", "rank");
assertions += RoundResultChecks.Run(scored);
Check(parsedPage.Battles.Single().Lp == 12500 && parsedPage.Battles.Single().Mr == 1500, "Scores must be taken from the user's side");
Check(parsedPage.Battles.Single().CharacterId == 7 && parsedPage.Battles.Single().Outcome == "胜", "Decimal user IDs and character identity must parse correctly");
foreach(var (type,key) in new[]{(1,"rank"),(2,"casual"),(3,"custom"),(4,"hub")})
{
    var typed=scored.Replace("\"replay_id\":",$"\"replay_battle_type\":{type},\"replay_id\":");
    foreach(var name in new[]{"", "Ranked Match", "自定义房间<br />对战", "異なる表示名"})
        Check(MatchParser.Parse(typed.Replace("Ranked Match",name),"3396336472","").Battles.Single().Category==key,"Numeric battle types classify records independently of empty, translated or HTML display names: "+key);
    Check(MatchParser.Parse(typed.Replace("\"replay_battle_type_name\":\"Ranked Match\",",""),"3396336472","").Battles.Single().Category==key,"A display name is not required to classify a replay: "+key);
}
foreach(var typeField in new[]{"", "\"replay_battle_type\":99,", "\"replay_battle_type\":null,"})
{
    try {MatchParser.Parse(scored.Replace("\"replay_id\":",typeField+"\"replay_id\":"),"3396336472","");throw new Exception("Missing or unsupported numeric modes must not be guessed from display text");}
    catch(InvalidDataException){assertions++;}
}
var opponentPage = MatchParser.Parse(scored, "1234567890", "rank");
Check(opponentPage.Battles.Single() is { OwnerId: "1234567890", PlayerName: "Other", OpponentId: "3396336472", Outcome: "负", Lp: 12000 }, "The same replay is parsed from the viewed player's side, including UID, outcome and scores");
var badgePage=MatchParser.Parse(scored.Replace("\"master_rating\":1500","\"master_rating\":1500,\"master_league\":40"),"3396336472","rank");
Check(badgePage.Battles.Single().Season==null && badgePage.Battles.Single().MasterLeague==40,"Replay Master badge must not become a season ID");
var rankingJson=scored.Replace("\"league_point\":12000","\"league_point\":12000,\"master_rating_ranking\":501").Replace("\"master_rating\":1500","\"master_rating\":1500,\"master_rating_ranking\":500");
Check(MatchParser.Parse(rankingJson,"3396336472","rank").Battles.Single() is {MasterRatingRanking:500,OpponentMasterRatingRanking:501},"Replay leaderboard positions are retained separately for both players");
Check(MatchParser.Parse(rankingJson,"1234567890","rank").Battles.Single() is {MasterRatingRanking:501,OpponentMasterRatingRanking:500},"Switching the viewed player swaps both leaderboard positions");
assertions += await IntegrationChecks.Run();
assertions += await StreamingChecks.Run();
assertions += await ImageDownloadChecks.Run();
assertions += await PagingChecks.Run();
assertions += await IncrementalBrowsingChecks.Run();
assertions += PresentationChecks.Run();
assertions += await RankQueryChecks.Run();
assertions += NavigationChecks.Run();
assertions += await ScorePagingChecks.Run();
assertions += SettingsChecks.Run();
assertions += await GitHubUpdateChecks.Run();
assertions += MatchEndDetectionChecks.Run();
assertions += await MatchEndRefreshOverlapChecks.Run();
assertions += MatchEndSampleLogChecks.Run();
assertions += await AutoRefreshChecks.Run();
assertions += await DailyProfileRefreshChecks.Run();
assertions += RecentMatchScoresChecks.Run();
assertions += await DatabaseTransferChecks.Run();
assertions += await PlayerSearchChecks.Run();
assertions += await TagChecks.Run();
assertions += FavoriteChecks.Run();
assertions += await RefreshProgressChecks.Run();
assertions += LocalizationChecks.Run();
assertions += await CharacterNameChecks.Run();
assertions += await BundledCharacterNameChecks.Run();
assertions += await AtomicFileChecks.RunAsync();
assertions += ArchiveReadChecks.Run();
assertions += await DiagnosticChecks.RunAsync();
assertions += await SyncCoordinatorChecks.RunAsync();
Console.WriteLine($"Passed {assertions} checks.");
