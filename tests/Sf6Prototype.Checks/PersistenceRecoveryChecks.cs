using Microsoft.Data.Sqlite;
using Sf6Prototype;

public static class PersistenceRecoveryChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool ok,string message) {if(!ok)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        object? Sql(string path,string sql)
        {
            using var db=new SqliteConnection("Data Source="+path);db.Open();
            using var cmd=db.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteScalar();
        }
        const string owner="1234567890";
        Battle Row(string id,long at)=>new(){OwnerId=owner,ReplayId=id,Category="rank",PlayedAt=at,CharacterId=1,Character="RYU",Lp=1200,Season=36};
        var path=Path.Combine(root,"checkpoint.sqlite");var store=new BattleStore(path);
        store.SavePage([Row("old",1000),Row("new",1100)]);store.CompleteCategory(owner,"rank","old");
        store.MarkCategoryPending(owner,"rank");store.SavePrefix(owner,"rank",new(["old"],2,4));
        Sql(path,"CREATE TRIGGER inject_failure BEFORE DELETE ON history_prefixes BEGIN SELECT RAISE(ABORT,'injected failure'); END;");
        try {store.CompleteCategory(owner,"rank","new");throw new Exception("Expected failure");} catch(SqliteException) {}
        Check(store.Frontier(owner,"rank")=="old" && !store.HasCategorySnapshot(owner,"rank") && store.Prefix(owner,"rank")!=null,
            "A failed checkpoint rolls back the frontier, pending marker and prefix together");
        Sql(path,"DROP TRIGGER inject_failure");store.CompleteCategory(owner,"rank","new");
        Check(store.Frontier(owner,"rank")=="new" && store.HasCategorySnapshot(owner,"rank") && store.Prefix(owner,"rank")==null,"Checkpoint retry commits all three changes");
        var archived=store.Query(new(owner)).Select(b=>System.Text.Json.JsonSerializer.Serialize(b)).ToArray();
        Sql(path,"DROP INDEX ix_battles_history; PRAGMA user_version=4;");var indexed=new BattleStore(path);
        Check(indexed.Query(new(owner)).Select(b=>System.Text.Json.JsonSerializer.Serialize(b)).SequenceEqual(archived) &&
            Convert.ToInt32(Sql(path,"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_battles_history'"))==1,
            "The version 5 read index preserves every archived field and replay");
        Sql(path,"PRAGMA user_version=99");
        var schema=(string)Sql(path,"SELECT group_concat(sql) FROM sqlite_master")!;
        try {_=new BattleStore(path);throw new Exception("Expected version rejection");}catch(InvalidDataException) {}
        Check(Convert.ToInt32(Sql(path,"PRAGMA user_version"))==99 && (string)Sql(path,"SELECT group_concat(sql) FROM sqlite_master")! ==schema,
            "Opening a future archive refuses it without changing its schema or version");
        var migration=Path.Combine(root,"migration.sqlite");var legacy=new BattleStore(migration);
        legacy.SavePage([Row("a",1000),Row("b",1100)]);legacy.CompleteCategory(owner,"rank","b");
        Sql(migration,"UPDATE battles SET data_json=json_remove(data_json,'$.MasterLeague'); PRAGMA user_version=1; CREATE TRIGGER migration_failure BEFORE INSERT ON score_links BEGIN SELECT RAISE(ABORT,'injected migration failure'); END;");
        try {_=new BattleStore(migration);throw new Exception("Expected migration failure");}catch(SqliteException) {}
        Check(Convert.ToInt32(Sql(migration,"PRAGMA user_version"))==1 && Convert.ToInt32(Sql(migration,"SELECT json_extract(data_json,'$.Season') FROM battles LIMIT 1"))==36,
            "A later migration failure rolls back earlier data corrections and version changes");
        Sql(migration,"DROP TRIGGER migration_failure");var upgraded=new BattleStore(migration);
        Check(Convert.ToInt32(Sql(migration,"PRAGMA user_version"))==BattleStore.SchemaVersion && upgraded.Query(new(owner)).All(b=>b.Season==null && b.MasterLeague==36),
            "Retrying migration preserves records and applies the legacy correction once");
        var settings=new ConnectionSettings {Language="ja",AutoRefreshSeconds=300,PlayerId=owner,UseProxy=true,ProxyAddress="http://127.0.0.1:8888"};settings.Save(root);
        var settingsPath=Path.Combine(root,"settings.json");File.WriteAllText(settingsPath,"{broken");
        Check(ConnectionSettings.Load(root)==settings && ConnectionSettings.Load(root)==settings,"Corrupt primary settings recover the last saved preferences without repeated recovery");
        Check(Directory.GetFiles(root,"settings.corrupt-*.json").Length==1 && File.ReadAllText(Directory.GetFiles(root,"settings.corrupt-*.json")[0])=="{broken",
            "Recovery retains the exact damaged file for diagnostics");
        var fresh=Path.Combine(root,"fresh");Directory.CreateDirectory(fresh);File.WriteAllText(Path.Combine(fresh,"settings.json"),"null");
        Check(ConnectionSettings.Load(fresh) is {AutoRefreshSeconds:0,RefreshAfterMatch:false},"Without a usable backup recovery enables no automatic requests");
        var existing=Path.Combine(root,"existing");Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing,"settings.json"),"{\"Language\":\"ja\",\"AutoRefreshSeconds\":1800}");
        var valid=File.ReadAllText(Path.Combine(existing,"settings.json"));ConnectionSettings.Load(existing);
        Check(File.ReadAllText(Path.Combine(existing,"settings.json"))==valid && File.ReadAllText(Path.Combine(existing,"settings.json.bak"))==valid,
            "Existing installations gain a recovery copy without changing stored preferences");
        var before=File.ReadAllText(settingsPath);
        using(var held=new FileStream(settingsPath,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            try {(settings with {Language="en"}).Save(root);throw new Exception("Expected sharing failure");}catch(IOException) {}
        }
        Check(File.ReadAllText(settingsPath)==before && ConnectionSettings.Load(root)==settings,"Failed atomic replacement preserves the previous settings");
        Check(Directory.GetFiles(root,"*.tmp").Length==0,"Failed settings writes clean up only their own temporary file");
        return count;
    }
}
