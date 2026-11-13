using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Sf6Prototype;

public static class DatabaseTransferChecks
{
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool condition,string message) {if(!condition) throw new Exception(message);count++;}
        async Task Fails<T>(Func<Task> action,string message) where T:Exception
        {try {await action();}catch(T) {count++;return;}throw new Exception(message);}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string FileAt(string name)=>Path.Combine(root,name);
        var livePath=FileAt("live.sqlite"); var store=new BattleStore(livePath);
        const string owner="1234567890",other="9999999999";
        Battle Game(string id,long time,int lp,string account=owner)=>new() {OwnerId=account,ReplayId=id,Category="rank",CharacterId=1,Character="RYU",PlayerName="Player",PlayedAt=time,Lp=lp};
        using var held=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=livePath,Pooling=false}.ToString()); held.Open();
        using(var cmd=held.CreateCommand()) {cmd.CommandText="PRAGMA wal_autocheckpoint=0";cmd.ExecuteNonQuery();}
        var oldest=Game("old",100,10000);var current=Game("current",200,10100);
        store.SavePage([oldest,current]); store.SaveRankLinks(owner,[current,oldest],new());
        Check(File.Exists(livePath+"-wal") && new FileInfo(livePath+"-wal").Length>0,"The live source retains committed data in WAL during online backup");
        var export=FileAt("export.sqlite"); await DatabaseTransfer.BackupAsync(livePath,export);
        var exported=new BattleStore(export);
        Check(exported.Query(new(owner)).Count==2 && exported.ScoreLinks(owner)["old"]=="current","Online backup includes committed WAL records and score continuity while the source stays open");
        store.SavePage([Game("after-backup",300,10200)]);
        Check(exported.Query(new(owner)).Count==2 && store.Query(new(owner)).Count==3,"The backup is an independent snapshot and the running store remains writable");
        await Fails<ArgumentException>(()=>DatabaseTransfer.BackupAsync(livePath,livePath),"Backup must reject replacing its live source");
        await Fails<ArgumentException>(()=>DatabaseTransfer.BackupAsync(livePath,livePath+"-wal"),"Backup must reject replacing live WAL data");
        var protectedFile=FileAt("protected.sqlite");File.WriteAllText(protectedFile,"existing-backup");
        await Fails<SqliteException>(()=>DatabaseTransfer.BackupAsync(FileAt("missing.sqlite"),protectedFile),"Missing sources cannot produce an empty successful backup");
        Check(File.ReadAllText(protectedFile)=="existing-backup","Failed backup leaves an existing destination intact");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Fails<OperationCanceledException>(()=>DatabaseTransfer.BackupAsync(livePath,protectedFile,cancelled.Token),"Cancelled backup does not overwrite an existing destination");

        var incomingPath=FileAt("incoming.sqlite");var incoming=new BattleStore(incomingPath);
        var newer=Game("newer",400,10300);var foreign=Game("foreign",500,12000,other);
        incoming.SavePage([current with {Lp=99999},newer,foreign]);incoming.SaveRankLinks(owner,[newer,current],new());
        incoming.CompleteCategory(owner,"rank","newer");
        incoming.CompleteCategory(owner,"","newer");incoming.CompleteCategory(owner,"all-sync","newer");
        var portable=FileAt("portable.sqlite");await DatabaseTransfer.BackupAsync(incomingPath,portable);
        var beforeHash=SHA256.HashData(File.ReadAllBytes(portable));
        var result=await DatabaseTransfer.ImportAsync(portable,livePath,FileAt("Backups"));
        Check(result.Added==2 && result.Existing==1,"Import merges all players and deduplicates by owner and replay");
        Check(store.Query(new(owner)).Single(b=>b.ReplayId=="current").Lp==10100,"Existing match details win on duplicate imports");
        Check(store.Query(new(other)).Single().ReplayId=="foreign" && store.Query(new(owner)).Count==4,"Import retains current history while adding other account archives");
        Check(store.ScoreLinks(owner)["old"]=="current" && store.ScoreLinks(owner)["current"]=="newer","Existing and missing imported score links coexist without inventing extra links");
        Check(BattleStore.Scores(store.Query(new(owner)),links:store.ScoreLinks(owner)).Single(p=>p.ReplayId=="current").LpDelta==null,"Merging a replay between linked records cannot turn a gap into a false single-match score change");
        var safety=new BattleStore(result.BackupPath);
        Check(safety.Query(new(owner)).Count==3 && safety.Query(new(other)).Count==0,"Import creates a recoverable pre-merge database backup");
        Check(SHA256.HashData(File.ReadAllBytes(portable)).SequenceEqual(beforeHash),"Import never changes the selected source file");
        var again=await DatabaseTransfer.ImportAsync(portable,livePath,FileAt("Backups"));
        Check(again.Added==0 && again.Existing==3 && store.Query(new(owner)).Count==4,"Repeated imports are idempotent");
        Check(store.Frontier(owner,"all-sync")=="newer","Import accepts a validated full-account checkpoint without treating it as a battle category");
        incoming.MarkCategoryPending(owner,"");incoming.MarkCategoryPending(owner,"rank");
        var partialPath=FileAt("partial.sqlite");var partialStore=new BattleStore(partialPath);
        await DatabaseTransfer.ImportAsync(incomingPath,partialPath,FileAt("PartialBackups"));
        Check(!new BattlePager(partialStore,owner).IsComplete("") && !partialStore.HasCategorySnapshot(owner,"rank") && partialStore.IsCategoryPending(owner,""),"Importing an interrupted download retains its records and incomplete markers");
        await Fails<ArgumentException>(()=>DatabaseTransfer.ImportAsync(livePath,livePath,FileAt("Backups")),"Importing the live source into itself is rejected");
        await Fails<OperationCanceledException>(()=>DatabaseTransfer.ImportAsync(portable,livePath,FileAt("Backups"),cancelled.Token),"Cancelled import performs no merge");
        var invalid=FileAt("invalid.sqlite");File.WriteAllText(invalid,"not a database");
        await Fails<SqliteException>(()=>DatabaseTransfer.ImportAsync(invalid,livePath,FileAt("Backups")),"Invalid database files are rejected");
        var wrong=FileAt("wrong.sqlite");await DatabaseTransfer.BackupAsync(portable,wrong);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=wrong,Pooling=false}.ToString()))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE battles SET data_json=json_set(data_json,'$.OwnerId','1111111111')";cmd.ExecuteNonQuery();}
        await Fails<InvalidDataException>(()=>DatabaseTransfer.ImportAsync(wrong,livePath,FileAt("Backups")),"A mismatch between serialized and indexed identities is rejected before merging");
        var future=FileAt("future.sqlite");await DatabaseTransfer.BackupAsync(portable,future);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=future,Pooling=false}.ToString()))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=999";cmd.ExecuteNonQuery();}
        await Fails<InvalidDataException>(()=>DatabaseTransfer.ImportAsync(future,livePath,FileAt("Backups")),"Unsupported future schemas are rejected before any current data changes");
        Check(store.Query(new(owner)).Count==4 && store.Query(new(other)).Count==1,"Rejected imports preserve every live record");
        Check(!Directory.EnumerateFiles(FileAt("Backups"),".import-*").Any(),"Import snapshots are removed on success, rejection and cancellation");
        return count;
    }
}
