using Sf6Prototype;

public static class FavoriteChecks
{
    public static int Run()
    {
        var count=0;void Check(bool ok,string message) {if(!ok) throw new Exception(message);count++;}
        var home=new PlayerIdentity("1111111111","自己");var guest=new PlayerIdentity("2222222222","客人");var third=new PlayerIdentity("3333333333","第三位");
        var original=new Battle {OwnerId=guest.Id,PlayerName=guest.Name,OpponentId=third.Id,Opponent=third.Name,ReplayId="A",Category="rank",CharacterId=1,Character="隆",OpponentCharacter="春丽",OpponentCharacterId=7,
            PlayedAt=100,Lp=21000,Mr=null,LeagueRank=32,MasterLeague=0,OpponentLp=30000,OpponentMr=1720,OpponentLeagueRank=36,OpponentMasterLeague=2,MasterRatingRanking=700,OpponentMasterRatingRanking=200,MyRounds=1,OpponentRounds=2,InputType=1};
        var reverse=Favorites.FromPlayer(original,third.Id);
        Check(reverse.OwnerId==third.Id && reverse.PlayerName==third.Name && reverse.OpponentId==guest.Id && reverse.Opponent==guest.Name,"Choosing either participant makes that player the own side");
        Check(reverse.CharacterId==7 && reverse.Character=="春丽" && reverse.OpponentCharacterId==1 && reverse.OpponentCharacter=="隆","Both character identities swap together");
        Check(reverse.Lp==30000 && reverse.Mr==1720 && reverse.OpponentLp==21000 && reverse.OpponentMr==null,"Scores swap with their players");
        Check(reverse.LeagueRank==36 && reverse.MasterLeague==2 && reverse.MasterRatingRanking==200 && reverse.OpponentMasterRatingRanking==700,"Rank and leaderboard metadata cannot stay attached to the wrong player");
        Check(reverse.Outcome=="胜" && reverse.Score=="2 : 1" && reverse.InputType==null,"Score and outcome reverse while unavailable opponent input metadata is cleared");
        Check(Favorites.FromPlayer(original,guest.Id)==original,"The native perspective keeps the original record");
        try {Favorites.FromPlayer(original,home.Id);throw new Exception("Expected unrelated player rejection");}catch(ArgumentException) {count++;}
        var records=new[]{new FavoriteRecord(original,[new("连招",0)]),new FavoriteRecord(reverse,[new("防空",1)])};
        var forGuest=Favorites.ForPlayer(records,guest.Id);
        Check(forGuest.Count==1 && forGuest[0].Tags.Count==2 && forGuest[0].Battle==original,"Duplicate perspectives form one favorite with unioned tags and native metadata");
        var players=Favorites.Players(records,home);
        Check(players[0].Id==home.Id && players.Skip(1).Select(p=>p.Id).ToHashSet().SetEquals([guest.Id,third.Id]),"The signed-in player is first even without favorites, followed by both other participants");
        Check(Favorites.Players(records,home,"222").Single().Id==guest.Id && Favorites.Players(records,home,"第三").Single().Id==third.Id,"Favorite user suggestions support UID and nickname searches");
        Check(Favorites.Players(records,home,"找不到").Count==0,"Unmatched local users never trigger a network lookup");
        Check(Favorites.Filter(Favorites.ForPlayer(records,third.Id),new(third.Id,Character:"春丽",OpponentCharacter:"隆",Outcome:"胜",Tags:["连招","防空"],MatchAllTags:true)).Count==1,"All normal filters apply after choosing the favorite perspective");
        Check(Favorites.Filter(forGuest,new(guest.Id,Outcome:"胜")).Count==0,"Win and loss filters use the selected user's outcome");
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));var store=new BattleStore(Path.Combine(root,"favorites.sqlite"));
        store.SavePage([original,reverse]);store.AddTag(guest.Id,"A","连招");store.AddTag(third.Id,"A","防空");
        Check(store.FavoriteRecords().Count==2 && Favorites.ForPlayer(store.FavoriteRecords(),third.Id).Count==1,"Stored sources stay intact while collection rows deduplicate by replay");
        store.SetFavoriteTag(third.Id,"A","复盘",true);
        Check(Favorites.ForPlayer(store.FavoriteRecords(),third.Id).Single().Tags.Any(t=>t.Name=="复盘"),"Editing a reversed favorite adds its tag to a real stored source");
        store.AddTag(guest.Id,"A","防空");store.SetFavoriteTag(third.Id,"A","防空",false);
        Check(store.FavoriteRecords().All(r=>r.Tags.All(t=>t.Name!="防空")),"Removing a collection tag removes it from both duplicate source perspectives");
        store.SetFavoriteTag(third.Id,"A","复盘",false);store.SetFavoriteTag(third.Id,"A","连招",false);
        Check(store.FavoriteRecords().Count==0 && store.Query(new(guest.Id)).Count==1 && store.Query(new(third.Id)).Count==1,"Removing the last collection tag removes the favorite for both users without deleting either replay");
        return count;
    }
}
