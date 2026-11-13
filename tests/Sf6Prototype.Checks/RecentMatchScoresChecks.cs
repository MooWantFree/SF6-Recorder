using Sf6Prototype;

internal static class RecentMatchScoresChecks
{
    public static int Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        const string owner="1234567890";
        var start=new DateTimeOffset(2026,11,14,4,0,0,TimeSpan.FromHours(8));var at=start.ToUnixTimeSeconds();
        var old=new Battle {OwnerId=owner,ReplayId="old",Category="rank",CharacterId=1,Character="隆",PlayedAt=at-300,Lp=900,MyRounds=2,OpponentRounds=0};
        var a=old with {ReplayId="a",PlayedAt=at+60,Lp=1000};
        var b=a with {ReplayId="b",PlayedAt=at+120,Lp=1050,MyRounds=0,OpponentRounds=2};
        var c=a with {ReplayId="c",PlayedAt=at+180,Lp=1010};
        var rows=new[]{c,old,b,a};
        var points=BattleStore.Scores(rows).ToDictionary(p=>p.ReplayId);
        var session=RecentMatchScores.Build(rows,points,owner,start,false);
        Check(session.Latest?.ReplayId=="c" && session.Matches.Select(m=>m.ReplayId).SequenceEqual(["a","b","c"]),"Session membership uses official match time, including newly downloaded historical records only when they actually occurred after startup");
        Check(session.InitialScore==1000 && session.Highest==1050 && session.Lowest==1000,"The range includes the first match's pre-match baseline and confirmed post-match scores");
        Check(session.Change==10 && session.Pending && !session.Samples[^1].Settled,"Known changes exclude the unsettled final match and keep its hollow point pending");
        Check(Math.Abs(session.WinRate!.Value-200d/3)<.001,"The interval win rate includes known outcomes even when the final score is pending");
        var foreign=c with {OwnerId="9876543210",CharacterId=2,PlayedAt=at+999};
        Check(RecentMatchScores.Build(rows.Append(foreign),points,owner,start,false).Latest?.ReplayId=="c","Another account's newer match cannot choose this account's character");
        var custom=c with {ReplayId="custom",CharacterId=2,Character="肯",Category="custom",PlayedAt=at+240};
        var otherCharacter=RecentMatchScores.Build(rows.Append(custom),points,owner,start,false);
        Check(otherCharacter.Latest?.CharacterId==2 && otherCharacter.Matches.Count==0,"The newest refreshed match chooses the character even in a non-ranked mode, without joining different characters' ratings");
        var restarted=RecentMatchScores.Build(rows,points,owner,start.AddMinutes(10),false);
        Check(restarted.Matches.Count==0 && restarted.Samples.Count==0 && restarted.Change==null && restarted.WinRate==null,"Restarting starts a new interval instead of reusing the previous app session");
        var boundary=a with {ReplayId="boundary",PlayedAt=at};
        Check(RecentMatchScores.Build([boundary],BattleStore.Scores([boundary]).ToDictionary(p=>p.ReplayId),owner,start,false).Matches.Count==1,"A match at the startup boundary is included at the official timestamp's second precision");
        var masterA=a with {Mr=1500,Season=1};var masterB=b with {Mr=1510,Season=1};var masterC=c with {Mr=1504,Season=1};
        var masters=new[]{masterA,masterB,masterC};
        var mr=RecentMatchScores.Build(masters,BattleStore.Scores(masters).ToDictionary(p=>p.ReplayId),owner,start,true);
        Check(mr.InitialScore==1500 && mr.Highest==1510 && mr.Lowest==1500 && mr.Change==4 && mr.Pending,"The same session model handles MR and its pending newest settlement");
        var seasons=new[]{masterA,masterB with {Season=2},masterC with {Season=2}};
        var seasonal=RecentMatchScores.Build(seasons,BattleStore.Scores(seasons).ToDictionary(p=>p.ReplayId),owner,start,true);
        Check(seasonal.Change==null && seasonal.Pending,"A season reset never appears as a combined MR change");
        var noMr=RecentMatchScores.Build(rows,points,owner,start,true);
        Check(noMr.Samples.Count==0 && noMr.Highest==null && noMr.Lowest==null && noMr.Change==null,"Non-master sessions never acquire a fabricated MR curve");
        var settled=BattleStore.Scores(rows,new CurrentScores(start.AddMinutes(4),1,[new CharacterRank(1,"隆",1070,null,24,0)]));
        var complete=RecentMatchScores.Build(rows,settled.ToDictionary(p=>p.ReplayId),owner,start,false);
        Check(complete.Change==70 && !complete.Pending && complete.Estimated,"A daily current-score snapshot can settle the latest point while retaining its estimate marker");
        return count;
    }
}
