using Sf6Prototype;

public static class ScoreSettlementChecks
{
    public static int Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var battle=new Battle {OwnerId="1234567890",ReplayId="latest",Category="rank",CharacterId=16,
            PlayedAt=1000,Lp=23035,Mr=0,MyRounds=1,OpponentRounds=2};
        CurrentScores Current(int lp,long at=2000,int character=16)=>new(DateTimeOffset.FromUnixTimeSeconds(at),1,
            [new(character,"fixture",lp,0,30,null)]);
        foreach(var won in new[]{false,true})
        foreach(var delta in new[]{-121,-120,-119,-1,0,1,120,121,500000,int.MaxValue-23035})
        {
            var row=battle with {MyRounds=won ? 2 : 1,OpponentRounds=won ? 1 : 2};
            var point=ScoreCalculator.Calculate([row],Current(23035+delta)).Single();
            int? expected=delta < -120 ? null : delta;
            Check(point.LpDelta==expected && point.LpAfter==(expected==null ? null : 23035+expected),
                $"Official current LP accepts -120 and unlimited gains independently of the outcome: {won}, {delta}");
            var next=row with {ReplayId="next",PlayedAt=1100,Lp=23035+delta};
            var linked=ScoreCalculator.Calculate([row,next],links:new Dictionary<string,string>{{row.ReplayId,next.ReplayId}}).First();
            Check(linked.LpDelta==expected,"Verified replay settlements apply the same LP lower bound");
        }
        Check(ScoreCalculator.Calculate([battle],Current(22995,999)).Single().LpDelta==null,
            "A score captured before the latest match cannot settle it");
        Check(ScoreCalculator.Calculate([battle],Current(22995,character:1)).Single().LpDelta==null,
            "Another character's official score cannot settle this match");
        Check(ScoreCalculator.Calculate([battle with {Lp=-1}],Current(50000)).Single().LpDelta==null,
            "Unranked scores do not become large placement gains");
        Check(ScoreCalculator.Calculate([battle],Current(-1)).Single().LpDelta==null,
            "An unranked official score cannot settle a ranked match");
        var gap=battle with {ReplayId="gap",PlayedAt=1100,Lp=24000};
        Check(ScoreCalculator.Calculate([battle,gap],links:new Dictionary<string,string>()).First().LpDelta==null,
            "Unlimited gains do not bypass missing replay continuity");
        return count;
    }
}
