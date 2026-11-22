using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Sf6Prototype;

internal static class ArchiveSnapshotChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        const string owner="1234567890",other="2222222222";
        var store=new BattleStore(Path.Combine(root,"archive.sqlite"));var profiles=new ProfileCache(root);var archive=new Archive(store,profiles);
        Battle Row(string id,long at,int lp)=>new() {OwnerId=owner,ReplayId=id,Category="rank",PlayedAt=at,CharacterId=1,Character="RYU",
            OpponentId=other,OpponentCharacterId=2,OpponentCharacter="CHUN-LI",Lp=lp,Mr=1500+(lp-10000)/10,Season=1,
            MyRounds=2,OpponentRounds=1,RawJson=JsonSerializer.SerializeToElement(new {original=true})};
        store.SavePage([Row("first",1000,10000),Row("second",1100,10050)]);
        store.SaveRankLinks(owner,store.Query(new(owner)),new());
        archive.Load(owner);var rows=archive.All;var points=archive.Points;
        archive.Load(owner,refreshImages:true);
        Check(ReferenceEquals(rows,archive.All) && ReferenceEquals(points,archive.Points),"Image refresh reuses battle and score snapshots");
        Check(archive.Points["first"].LpDelta==50 && archive.All.All(b=>b.RawJson==null),"Lightweight rows retain verified score settlements");
        store.AddTag(owner,"first","review");archive.Load(owner);
        Check(archive.Tags["first"].Single().Name=="review","Tags refresh without rereading full battle JSON");
        var reversed=Row("reversed",900,20000) with {OwnerId=other,OpponentId=owner,OpponentLp=12000};
        var favorites=new[]{new FavoriteRecord(reversed,[new("favorite",0)])};
        archive.LoadFavorites(owner,favorites);
        Check(archive.All.Count==3 && archive.Points["reversed"].LpDelta==null,"Reversed favorites retain the viewed player's perspective without inventing settlements");
        archive.LoadFavorites(owner,[]);
        Check(archive.All.Count==2 && !archive.Points.ContainsKey("reversed"),"Removing a favorite cannot leave an overlay in the native score cache");
        archive.Load(owner);
        Check(archive.Tags["first"].Single().Name=="review","Returning to native history restores native tags");
        void Complete(Task task)
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!task.IsCompleted && DateTime.UtcNow<deadline) Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
            Check(task.IsCompleted,"Background archive preparation completes without network or windows");task.GetAwaiter().GetResult();
        }
        Complete(archive.PrepareAsync(owner,CancellationToken.None));
        store.SavePage([Row("third",1200,10100)]);store.SaveRankLinks(owner,store.Query(new(owner)),new());
        archive.Load(owner);
        Check(archive.All.Count==3 && archive.Points["second"].LpDelta==50,"A stale prepared snapshot cannot replace newer saved pages or links");
        Complete(archive.PrepareAsync(owner,CancellationToken.None));archive.Load(other);
        Check(archive.Owner==other && archive.All.Count==0 && archive.Points.Count==0,"A prepared player archive cannot overwrite a different player's view");
        var scoreFile=Path.Combine(root,"scores-"+owner+".json");
        var current=new CurrentScores(DateTimeOffset.FromUnixTimeSeconds(2000),1,[new(1,"RYU",10150,1520,36,null)]);
        File.WriteAllText(scoreFile,JsonSerializer.Serialize(current));archive.Load(owner);
        Check(archive.Points["third"].LpDelta==50 && archive.Points["third"].MrDelta==10 && archive.Points["third"].Estimated,
            "A profile update settles the newest cached match without another battle write");
        Complete(archive.PrepareAsync(owner,CancellationToken.None));
        current=current with {Characters=[new(1,"RYU",10160,1530,36,null)]};
        File.WriteAllText(scoreFile,JsonSerializer.Serialize(current));archive.Load(owner);
        Check(archive.Points["third"].LpDelta==60 && archive.Points["third"].MrDelta==20,
            "Changed scores invalidate prepared results even when the profile timestamp is unchanged");
        current=current with {Season=2};File.WriteAllText(scoreFile,JsonSerializer.Serialize(current));archive.Load(owner);
        Check(archive.Points["third"].MrDelta==null && archive.Points["first"].MrDelta==5,
            "A profile season change clears only the cross-season estimate and preserves verified historic settlements");
        var scores=new ScoresView();scores.Bind(archive);
        current=current with {FetchedAt=DateTimeOffset.FromUnixTimeSeconds(1199)};
        File.WriteAllText(scoreFile,JsonSerializer.Serialize(current));archive.Load(owner);scores.Reload(new(1,false,20));
        var chart=(ScoreChart)scores.FindName("Chart");
        Check(chart.Points.Last() is {ReplayId:"third",Settled:false,Connect:false},
            "A profile older than the latest battle leaves its LP endpoint pending");
        foreach(var delta in new[]{-121,-120,0,50000})
        {
            current=current with {FetchedAt=DateTimeOffset.FromUnixTimeSeconds(2000),Characters=[new(1,"RYU",10100+delta,1530,36,null)]};
            File.WriteAllText(scoreFile,JsonSerializer.Serialize(current));archive.Load(owner);scores.Reload();
            var last=chart.Points.Last();
            Check(last.Settled==(delta>=-120) && last.Connect==(delta>=-120) && last.Value==(delta>=-120 ? 10100+delta : 10100),
                $"The LP chart reconnects fresh official settlements, including -120, zero and large gains: {delta}");
        }
        var restarted=new Archive(new BattleStore(Path.Combine(root,"archive.sqlite")),new ProfileCache(root));
        Complete(restarted.PrepareAsync(owner,CancellationToken.None));restarted.Load(owner);scores.Bind(restarted);scores.Reload();
        Check(chart.Points.Last() is {ReplayId:"third",Value:60100,Settled:true,Connect:true},
            "A cold local archive read retains the settled endpoint and connecting line without another refresh");
        var diamond = Row("diamond",2200,24500) with { CharacterId=2, Mr=null, LeagueRank=35 };
        store.SavePage([diamond]);
        archive.Load(owner);
        scores.Bind(archive);
        scores.ResetSelection();
        scores.Reload();
        Check(chart.Unit=="MR","Opening the scores page on a Master character defaults to MR");
        var characters=(System.Windows.Controls.ListBox)scores.FindName("CharacterList");
        var metric=(System.Windows.Controls.ListBox)scores.FindName("ScoreMetric");
        var requests=0;
        scores.DataRequested+=()=>requests++;
        characters.SelectedItem=characters.Items.Cast<CharacterCard>().Single(c=>c.Id==2);
        Check(chart.Unit=="LP" && requests==1,"Switching to a Diamond character defaults to LP with one data request");
        current=current with {Characters=[new(2,"Ken",24500,null,35,null)]};
        File.WriteAllText(scoreFile,JsonSerializer.Serialize(current));
        store.SavePage([diamond with {ReplayId="promoted",PlayedAt=2300,Lp=25000,LeagueRank=36}]);
        archive.Load(owner);
        scores.Reload();
        Check(chart.Unit=="MR","Promotion in a newer ranked match selects MR even before the profile or first MR value arrives");
        metric.SelectedIndex=0;
        var manual=scores.CaptureState();
        scores.Reload();
        Check(chart.Unit=="LP","Refreshing preserves the manually selected LP view for the Master character");
        characters.SelectedItem=characters.Items.Cast<CharacterCard>().Single(c=>c.Id==1);
        Check(chart.Unit=="MR","A different Master character gets its own rank default");
        scores.Reload(manual);
        Check(chart.Unit=="LP" && scores.Range.CharacterId==2,"Restoring navigation also restores the manual metric");
        return count;
    }
}
