using System.Text.Json;
using Sf6Prototype;

internal static class RoundResultChecks
{
    public static int Run(string fixture)
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        const string owner="3396336472";
        var raw=fixture.Replace("[1,0,1]","[8,0,7]").Replace("[0,1,0]","[0,6,0]");
        var battle=MatchParser.Parse(raw,owner,"rank").Battles.Single();
        Check(battle.RoundResults!.SequenceEqual([8,0,7]) && battle.OpponentRoundResults!.SequenceEqual([0,6,0]) && battle.Score=="2 : 1","The viewed player's ordered round codes and total score are parsed independently");
        var rounds=RoundResult.From(battle);
        Check(rounds.Select(r=>r.Own).SequenceEqual(["Perfect","—","CA"]) && rounds[1].Opponent=="SA" && rounds[1].Label=="2R","Each round exposes the correct side, finish and chronological round number");
        Check(Enumerable.Range(0,9).Select(code=>RoundResult.Symbol(code)).SequenceEqual(["—","V","C","T","D","OD","SA","CA","Perfect"]),"Known result codes retain their individual meanings");
        Check(RoundResult.Symbol(99)=="?" && RoundResult.Kind(99)=="other" && RoundResult.Description(99).Contains("99"),"Unknown result codes are disclosed without inventing a finish");
        var reverse=Favorites.FromPlayer(battle,battle.OpponentId);
        Check(reverse.RoundResults!.SequenceEqual(battle.OpponentRoundResults!) && reverse.OpponentRoundResults!.SequenceEqual(battle.RoundResults),"Favorite perspectives swap both round sequences");
        var missing=MatchParser.Parse(raw.Replace("\"round_results\":[8,0,7],",""),owner,"rank").Battles.Single();
        Check(missing.RoundResults==null && RoundResult.From(missing).Count==3 && RoundResult.From(missing)[0].Own=="?","One missing side stays unknown without concealing the available other side");
        var invalid=MatchParser.Parse(raw.Replace("[8,0,7]","[8,null,7]"),owner,"rank").Battles.Single();
        Check(invalid.RoundResults==null && invalid.MyRounds==null,"Malformed arrays do not produce fabricated counts or partial round sequences");
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"rounds.sqlite");var store=new BattleStore(path);
        Check(store.SavePage([battle])==1,"A new replay saves its parsed fields and full source object together");
        Check(store.SavePage([battle])==0,"Reading the same replay again never counts it as a new match");
        var saved=new BattleStore(path).Query(new(owner)).Single();
        Check(saved.RoundResults!.SequenceEqual([8,0,7]) && saved.OpponentRoundResults!.SequenceEqual([0,6,0]) && saved.MyRounds==battle.MyRounds && saved.Lp==battle.Lp,"Round arrays, scores and metadata survive restart");
        store.SavePage([battle with {RoundResults=[1,1],OpponentRoundResults=[]}]);
        saved=store.Query(new(owner)).Single();
        Check(saved.RoundResults!.SequenceEqual([8,0,7]) && saved.OpponentRoundResults!.SequenceEqual([0,6,0]),"Subsequent incomplete or conflicting responses never erase recorded round evidence");
        Check(JsonSerializer.Deserialize<Battle>(JsonSerializer.Serialize(saved))!.RoundResults!.SequenceEqual([8,0,7]),"Round codes serialize as numeric arrays rather than localized labels");
        Check(saved.RawJson!.Value.GetProperty("player2_info").GetProperty("round_results")[0].GetInt32()==8 && !JsonSerializer.Serialize(saved).Contains("must-not-be-exported"),"The complete replay is retained independently from surrounding session metadata");
        var extended=raw.Replace("\"replay_id\":","\"future_field\":{\"values\":[true,null,123],\"label\":\"原文\"},\"replay_id\":");
        Check(store.SavePage(MatchParser.Parse(extended,owner,"rank").Battles)==0,"A new upstream field enriches the existing raw payload without inserting another replay");
        var restored=new BattleStore(path).Query(new(owner)).Single();
        Check(restored.RawJson!.Value.GetProperty("future_field").GetProperty("label").GetString()=="原文" && restored.RawJson.Value.GetProperty("future_field").GetProperty("values")[1].ValueKind==JsonValueKind.Null,"Unknown nested fields, array types and Unicode survive storage and restart");
        Check(Favorites.FromPlayer(restored,restored.OpponentId).RawJson!.Value.GetRawText()==restored.RawJson.Value.GetRawText(),"Changing player perspective never rewrites the original official JSON");
        return count;
    }
}
