using Microsoft.Data.Sqlite;
using Sf6Prototype;

public static class TagChecks
{
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message) {if(!value) throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"tags.sqlite");var store=new BattleStore(path);
        const string owner="1234567890",guest="9999999999";
        Battle Game(string id,string other="春丽",string player=owner)=>new() {OwnerId=player,ReplayId=id,PlayerName="玩家",Category="rank",CharacterId=1,Character="隆",OpponentCharacter=other,PlayedAt=100,Lp=10000,MyRounds=2,OpponentRounds=1};
        store.SavePage([Game("A"),Game("B","肯"),Game("C"),Game("A",player:guest)]);
        store.AddTag(owner,"A"," 连招 ");store.AddTag(owner,"A","防空");store.AddTag(owner,"B","连招");store.AddTag(guest,"A","对策");
        Check(store.Query(new(owner,FavoritesOnly:true)).Select(b=>b.ReplayId).SequenceEqual(["A","B"]),"Favorites are tagged replays belonging only to the viewed player");
        Check(store.Query(new(guest,FavoritesOnly:true)).Single().ReplayId=="A" && store.TagsFor(guest)["A"].Single().Name=="对策","Other players can be bookmarked without modifying the same replay's tags in another perspective");
        Check(store.Query(new(owner,Tags:["连招","防空"])).Count==2,"Any-tag matching returns the union without duplicate matches");
        Check(store.Query(new(owner,Tags:["连招","防空"],MatchAllTags:true)).Single().ReplayId=="A","All-tag matching returns the intersection");
        Check(store.Query(new(owner,Tags:["连招","连招"],MatchAllTags:true)).Count==2,"Repeated selected labels do not change all-tag matching");
        Check(store.Query(new(owner,OpponentCharacter:"肯",FavoritesOnly:true,Tags:["连招"])).Single().ReplayId=="B","Opponent character and bookmark filters combine");
        Check(store.Query(new(owner,Character:"隆",OpponentCharacter:"春丽",Outcome:"胜")).Count==2,"Own and opponent character filters are independent");
        Check(store.Query(new(owner,Tags:["' OR 1=1 --"])).Count==0,"Tag names are parameterized rather than SQL fragments");
        store.AddTag(owner,"A","Combo");store.AddTag(owner,"A","combo");
        Check(store.TagsFor(owner)["A"].Count==3 && store.AllTags().Count(t=>t.Name.Equals("combo",StringComparison.OrdinalIgnoreCase))==1,"Whitespace and ASCII case variants reuse the same label");
        Check(TagNames.Suggest(store.AllTags(),"co").Single().Name=="Combo" && TagNames.Suggest(store.AllTags(),"招").Count==0,"Autocomplete ignores case and matches prefixes, not arbitrary substrings");
        Check(TagNames.Suggest(store.AllTags(),"").Count==store.AllTags().Count,"Opening autocomplete with no input lists all previous labels");
        var before=store.TagsFor(owner)["A"].ToArray();
        foreach(var invalid in new[]{"   ","bad\nname",new string('x',33)})
        {
            try {store.AddTag(owner,"A",invalid);throw new Exception("Expected invalid tag rejection");}catch(ArgumentException) {count++;}
        }
        Check(store.TagsFor(owner)["A"].SequenceEqual(before),"Invalid labels cannot mutate existing bookmarks");
        try {store.AddTag(owner,"missing","失效对局");throw new Exception("Expected missing replay rejection");}catch(SqliteException) {count++;}
        Check(store.AllTags().All(t=>t.Name!="失效对局"),"A failed tag association rolls back the newly created vocabulary entry");
        store.RemoveTag(owner,"B","连招");
        Check(store.Query(new(owner,FavoritesOnly:true)).Count==1 && store.AllTags().Any(t=>t.Name=="连招"),"Removing the last tag unbookmarks the replay but retains autocomplete vocabulary");
        store.SavePage([Game("A")]);
        Check(store.TagsFor(owner)["A"].SequenceEqual(before),"Fetching the same replay again preserves every user label");
        for(var i=0;i<18;i++) store.AddTag(owner,"C","标签"+i);
        Check(store.TagsFor(owner)["C"].Select(t=>t.Color).Distinct().Count()==18,"More than a dozen labels receive separate persisted color identities");
        Check(new BattleStore(path).TagsFor(owner)["A"].SequenceEqual(before),"Labels and color assignments survive reopening the database");
        var backup=Path.Combine(root,"export.sqlite");await DatabaseTransfer.BackupAsync(path,backup);
        Check(new BattleStore(backup).TagsFor(guest)["A"].Single().Name=="对策","Online backup includes bookmarks for other players");
        store.RemoveTag(owner,"A","防空");store.AddTag(owner,"A","新标签");
        var merged=await DatabaseTransfer.ImportAsync(backup,path,Path.Combine(root,"backups"));
        Check(merged.Added==0 && store.TagsFor(owner)["A"].Any(t=>t.Name=="防空") && store.TagsFor(owner)["A"].Any(t=>t.Name=="新标签"),"Merge import unions tags on existing replays and preserves new local tags");
        Check(new BattleStore(merged.BackupPath).TagsFor(owner)["A"].All(t=>t.Name!="防空"),"The automatic pre-import backup retains the previous bookmark state");
        await DatabaseTransfer.ImportAsync(backup,path,Path.Combine(root,"backups"));
        Check(store.TagsFor(owner)["A"].Count==4,"Repeated label imports are idempotent");
        var legacy=Path.Combine(root,"legacy.sqlite");await DatabaseTransfer.BackupAsync(backup,legacy);
        using(var db=new SqliteConnection($"Data Source={legacy};Pooling=False"))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE battle_tags;DROP TABLE tags;PRAGMA user_version=3";cmd.ExecuteNonQuery();}
        await DatabaseTransfer.ImportAsync(legacy,path,Path.Combine(root,"backups"));
        Check(store.TagsFor(owner)["A"].Count==4,"Version 3 archives remain importable without removing current tags");
        var legacyStore=new BattleStore(legacy);
        Check(legacyStore.Query(new(owner)).Count==3 && legacyStore.AllTags().Count==0,"Schema migration adds empty tag tables while preserving old matches");
        var broken=Path.Combine(root,"broken.sqlite");await DatabaseTransfer.BackupAsync(backup,broken);
        using(var db=new SqliteConnection($"Data Source={broken};Pooling=False;Foreign Keys=False"))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO battle_tags VALUES('1234567890','A','missing')";cmd.ExecuteNonQuery();}
        try {await DatabaseTransfer.ImportAsync(broken,path,Path.Combine(root,"backups"));throw new Exception("Expected dangling label rejection");}catch(InvalidDataException) {count++;}
        Check(store.TagsFor(owner)["A"].Count==4,"Rejected imports leave current tags unchanged");
        var independentPath=Path.Combine(root,"independent.sqlite");var independent=new BattleStore(independentPath);
        independent.SavePage([Game("D")]);independent.AddTag(owner,"D","独立标签");
        var oldColor=store.AllTags().Single(t=>t.Name=="连招").Color;
        await DatabaseTransfer.ImportAsync(independentPath,path,Path.Combine(root,"backups"));
        Check(store.AllTags().Single(t=>t.Name=="连招").Color==oldColor && store.AllTags().Select(t=>t.Color).Distinct().Count()==store.AllTags().Count,"Import keeps existing tag colors and assigns a free color when an independent archive collides");
        return count;
    }
}
