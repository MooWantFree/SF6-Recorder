using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Sf6Prototype;

public static class PresentationChecks
{
    private sealed class Row(string owner,string id,int score)
    {
        public string Owner { get; }=owner;
        public string Id { get; }=id;
        public int Score { get; set; }=score;
    }
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) { if(!value) throw new Exception(message); count++; }
        var first=new Row("me","one",100); var selected=new Row("me","two",200);
        var rows=new ObservableCollection<Row> {first,selected};
        var changes=new List<NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged+=(_,e)=>changes.Add(e);
        void Apply(params Row[] incoming)=>IncrementalList.Apply(rows,incoming,r=>(r.Owner,r.Id),(old,next)=>old.Score=next.Score);
        Apply(new("me","one",150),new("me","two",200),new("me","three",250));
        Check(ReferenceEquals(rows[0],first) && ReferenceEquals(rows[1],selected) && first.Score==150,"Page updates preserve row identity and current selection while refreshing score data");
        Check(changes.Count==1 && changes[0].Action==NotifyCollectionChangedAction.Add && changes[0].NewStartingIndex==2,"Appending a page emits only additions, never Reset or replacement notifications");
        changes.Clear();
        Apply(new("me","two",210),new("me","three",260));
        Check(ReferenceEquals(rows[0],selected) && selected.Score==210 && changes.All(e=>e.Action!=NotifyCollectionChangedAction.Reset),"Filtering preserves surviving row identities without resetting the list");
        Apply(new("me","three",270),new("me","two",220));
        Check(ReferenceEquals(rows[1],selected),"Reordering keeps the same selected row object");
        Apply(new Row("other","two",300));
        Check(rows.Count==1 && !ReferenceEquals(rows[0],selected) && rows[0].Owner=="other","The same replay viewed from another player must not reuse the previous player's row");
        Apply(); Check(rows.Count==0,"Empty results remove all stale rows");

        var now=new DateTimeOffset(2026,11,12,12,0,0,TimeSpan.FromHours(8));
        Check(!DateRanges.IsRefreshOverdue(null,now) && !DateRanges.IsRefreshOverdue(now,now),"Unknown and freshly updated histories do not trigger the refresh reminder");
        Check(!DateRanges.IsRefreshOverdue(now.AddDays(-1),now) && DateRanges.IsRefreshOverdue(now.AddDays(-1).AddSeconds(-1),now),"The refresh reminder starts only after a full 24 hours");
        Check(!DateRanges.IsRefreshOverdue(now.AddMinutes(5),now) && DateRanges.IsRefreshOverdue(now.AddDays(-2).ToUniversalTime(),now),"Reminder timing handles clock skew and timezone offsets");

        Check(ViewportMath.CenterOffset(250,200,800)==150,"Dropdown selection is centered within its viewport");
        Check(ViewportMath.CenterOffset(15,200,800)==0 && ViewportMath.CenterOffset(990,200,800)==800,"Dropdown centering clamps at the first and last season");
        Check(ViewportMath.CenterOffset(80,300,0)==0,"A short season list does not scroll into empty space");
        Check(ViewportMath.HistoryOffset(360,36,360,720)==324,"Browsing older matches keeps the selection in the second row");
        Check(ViewportMath.HistoryOffset(360,36,360,720,true)==72,"Browsing newer matches keeps the selection in the second-to-last row");
        Check(ViewportMath.HistoryOffset(0,36,360,720)==0 && ViewportMath.HistoryOffset(36,36,360,720)==0,"The newest and second newest matches stay at the beginning");
        Check(ViewportMath.HistoryOffset(36,36,360,720,true)==0,"Browsing newer matches clamps at the first meeting");
        Check(ViewportMath.HistoryOffset(1008,36,360,720)==720,"History navigation keeps the bottom filled near the oldest match");
        Check(ViewportMath.HistoryOffset(72,36,360,0)==0 && ViewportMath.HistoryOffset(72,36,360,0,true)==0,"A short history stays in place in either direction");
        Check(ViewportMath.HistoryOffset(360,36,36,720)==360 && ViewportMath.HistoryOffset(360,36,36,720,true)==360,"A one-row viewport keeps the selected match fully visible");

        Check(ScoreDisplay.Value(-1)=="New Challenger" && ScoreDisplay.WithUnit(-1,"LP")=="New Challenger","Unranked sentinel scores display New Challenger without an LP suffix");
        Check(ScoreDisplay.Value(0)=="0" && ScoreDisplay.Value(null)=="—","Zero LP and unknown scores remain distinct from unranked scores");
        Check(new Battle {Lp=-1}.LpText=="New Challenger" && new CharacterRank(1,"RYU",-1,0,0,null).RankName=="New Challenger","Replay and rank summaries use the same unranked display");
        Battle Game(string id,long time,int lp)=>new() {OwnerId="1234567890",ReplayId=id,PlayedAt=time,Category="rank",CharacterId=1,Lp=lp};
        var games=new[]{Game("placement",1,-1),Game("placed",2,15000),Game("ranked",3,15100)};
        var points=BattleStore.Scores(games);
        Check(points[0].LpDelta==null && points[0].LpAfter==null && points[1].LpDelta==100,"Finishing placement does not create a fake 15001 LP change");
        var current=new CurrentScores(DateTimeOffset.FromUnixTimeSeconds(10),1,[new CharacterRank(1,"RYU",15000,0,26,null)]);
        Check(BattleStore.Scores([games[0]],current).Single().LpDelta==null,"Current profile scores cannot estimate a delta from the unranked sentinel");
        var me=new PlayerIdentity("1234567890","Player");
        Check(me.DisplayNameFor(me.Id)=="Player（我）" && me.DisplayNameFor(null)=="Player","The self suffix follows the signed-in UID and disappears after logout");
        Check(new PlayerIdentity("9999999999","Player").DisplayNameFor(me.Id)=="Player","Matching nicknames do not make another player the signed-in user");
        Check(new CharacterRank(1,"RYU",11000,0,23,0) is {RankName:"Gold III",BadgeId:23},"Rank labels use Roman stars with the matching official badge ID");
        Check(new CharacterRank(1,"RYU",25000,1600,36,40) is {RankName:"High Master",BadgeId:40},"High Master starts at the supplied 1600 MR boundary");
        Check(new CharacterRank(1,"RYU",25000,1700,36,null).RankName=="Grand Master" && new CharacterRank(1,"RYU",25000,1800,36,null).RankName=="Ultimate Master","Master subranks follow the supplied MR thresholds without requiring badge metadata");
        Check(new CharacterRank(1,"RYU",-1,0,39,0).BadgeId==null,"New Challenger does not request a nonexistent badge resource");
        int[] thresholds=[0,200,400,600,800,1000,1400,1800,2200,2600,3000,3400,3800,4200,4600,5000,5800,6600,7400,8200,9000,9800,10600,11400,12200,13000,14200,15400,16600,17800,19000,20200,21400,22600,23800,25000];
        for(var i=0;i<thresholds.Length;i++)
        {
            Check(CharacterRank.LeagueFromLp(thresholds[i])==i+1,$"LP rank boundary {thresholds[i]}");
            if(i>0) Check(CharacterRank.LeagueFromLp(thresholds[i]-1)==i,$"One LP below rank boundary {thresholds[i]}");
        }
        Check(CharacterRank.LeagueFromLp(null)==null && CharacterRank.LeagueFromLp(-1)==null && CharacterRank.LeagueFromLp(int.MaxValue)==36,"Unknown and placement LP remain distinct; high LP alone never implies Legend");
        Check(new CharacterRank(0,"CAMMY",13070,0,null,null) is {RankName:"Platinum I",ResolvedLeagueRank:26},"Legacy opponent rows display their LP-derived rank without network requests");
        Check(new CharacterRank(1,"RYU",0,0,0,0).RankName=="Unranked" && new CharacterRank(1,"RYU",-1,0,null,null).RankName==ScoreDisplay.NewChallenger,"Explicit placement status takes precedence over zero LP");
        Check(new CharacterRank(1,"RYU",13000,0,25,0).RankName=="Gold V","A retained official league rank takes precedence over a fallback LP calculation");
        foreach(var (mr,name) in new[]{(1599,"Master"),(1600,"High Master"),(1699,"High Master"),(1700,"Grand Master"),(1799,"Grand Master"),(1800,"Ultimate Master")})
            Check(new CharacterRank(1,"RYU",25000,mr,null,null).RankName==name,$"Master boundary {mr} MR");
        Check(new CharacterRank(1,"RYU",25000,1900,null,null,500).RankName=="Legend" && new CharacterRank(1,"RYU",25000,1900,null,null,501).RankName=="Ultimate Master","Legend requires an existing top-500 rank and excludes position 501");
        Check(new CharacterRank(1,"RYU",25000,2300,null,null).RankName=="Ultimate Master" && new CharacterRank(1,"RYU",25000,2300,null,null,0).RankName=="Ultimate Master","High MR and missing or zero leaderboard positions do not invent Legend status");
        Check(new CharacterRank(1,"RYU",25000,1500,36,37).RankName=="Legend" && new CharacterRank(1,"RYU",25000,null,36,41).RankName=="Grand Master","Existing official Legend and missing-MR subrank markers are retained");
        return count;
    }
}
