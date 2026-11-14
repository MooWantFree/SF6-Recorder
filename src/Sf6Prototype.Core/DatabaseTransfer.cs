using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sf6Prototype;

public sealed record DatabaseImportResult(int Added,int Existing,string BackupPath);

public static class DatabaseTransfer
{
    private static SqliteConnection Open(string path,SqliteOpenMode mode)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder
        {DataSource=path,Mode=mode,Pooling=false,ForeignKeys=true,DefaultTimeout=10}.ToString());
        db.Open(); return db;
    }
    private static void DistinctFiles(string source,string destination)
    {
        if(new[]{source,source+"-wal",source+"-shm",source+"-journal"}.Any(p=>string.Equals(Path.GetFullPath(p),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(Strings.ChooseDifferentDatabase);
    }
    public static Task BackupAsync(string source,string destination,CancellationToken token=default) => Task.Run(()=>Backup(source,destination,token),token);
    public static void Backup(string source,string destination,CancellationToken token=default)
    {
        source=Path.GetFullPath(source); destination=Path.GetFullPath(destination); DistinctFiles(source,destination);
        if(new[]{"-wal","-shm","-journal"}.Any(s=>File.Exists(destination+s)))
            throw new ArgumentException(Strings.BackupJournalExists);
        token.ThrowIfCancellationRequested();
        var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var from=Open(source,SqliteOpenMode.ReadOnly))
            using(var to=Open(temporary,SqliteOpenMode.ReadWriteCreate))
            {
                // SQLite includes committed WAL pages in this consistent online snapshot.
                from.BackupDatabase(to);
                token.ThrowIfCancellationRequested();
                using var cmd=to.CreateCommand(); cmd.CommandText="PRAGMA journal_mode=DELETE"; cmd.ExecuteScalar();
                CheckIntegrity(to);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary,destination,true);
        }
        finally { DeleteTemporary(temporary); }
    }
    public static Task<DatabaseImportResult> ImportAsync(string source,string destination,string backupDirectory,CancellationToken token=default) =>
        Task.Run(()=>Import(source,destination,backupDirectory,token),token);
    private static DatabaseImportResult Import(string source,string destination,string backupDirectory,CancellationToken token)
    {
        source=Path.GetFullPath(source); destination=Path.GetFullPath(destination); DistinctFiles(destination,source);
        Directory.CreateDirectory(backupDirectory);
        var snapshot=Path.Combine(backupDirectory,".import-"+Guid.NewGuid().ToString("N")+".sqlite");
        try
        {
            Backup(source,snapshot,token);
            using(var incoming=Open(snapshot,SqliteOpenMode.ReadOnly)) ValidateArchive(incoming,token);
            var stamp=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss");
            var backup=Path.Combine(backupDirectory,$"before-import-{stamp}-{Guid.NewGuid():N}.sqlite");
            Backup(destination,backup,token);
            using var db=Open(destination,SqliteOpenMode.ReadWrite);
            using(var attach=db.CreateCommand())
            { attach.CommandText="ATTACH DATABASE $path AS incoming"; attach.Parameters.AddWithValue("$path",snapshot); attach.ExecuteNonQuery(); }
            using var tx=db.BeginTransaction();
            using var cmd=db.CreateCommand(); cmd.Transaction=tx;
            cmd.CommandText="SELECT COUNT(*) FROM incoming.battles"; var total=Convert.ToInt32(cmd.ExecuteScalar());
            token.ThrowIfCancellationRequested();
            cmd.CommandText="INSERT OR IGNORE INTO battles SELECT * FROM incoming.battles"; var added=cmd.ExecuteNonQuery();
            cmd.CommandText="INSERT OR IGNORE INTO score_snapshots SELECT * FROM incoming.score_snapshots"; cmd.ExecuteNonQuery();
            // Keep existing evidence on conflict. Do not invent adjacency across imported gaps.
            cmd.CommandText="""
                INSERT OR IGNORE INTO score_links
                SELECT s.* FROM incoming.score_links s
                JOIN battles a ON a.owner_id=s.owner_id AND a.replay_id=s.replay_id
                JOIN battles b ON b.owner_id=s.owner_id AND b.replay_id=s.next_replay_id
                WHERE a.category='rank' AND b.category='rank' AND a.character_id=b.character_id
                  AND (b.played_at>a.played_at OR (b.played_at=a.played_at AND b.replay_id>a.replay_id));
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText="SELECT EXISTS(SELECT 1 FROM incoming.sqlite_master WHERE type='table' AND name='sync_pending')";
            var hasPending=Convert.ToInt32(cmd.ExecuteScalar())==1;
            if(hasPending)
            {
                cmd.CommandText="INSERT OR IGNORE INTO sync_pending SELECT p.* FROM incoming.sync_pending p WHERE NOT EXISTS(SELECT 1 FROM sync_state s WHERE s.owner_id=p.owner_id AND s.category=p.category)";
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText="INSERT OR IGNORE INTO sync_state SELECT s.* FROM incoming.sync_state s"+
                (hasPending ? " WHERE NOT EXISTS(SELECT 1 FROM incoming.sync_pending p WHERE p.owner_id=s.owner_id AND p.category=s.category)" : "");
            cmd.ExecuteNonQuery();
            cmd.CommandText="PRAGMA incoming.user_version";
            if(Convert.ToInt32(cmd.ExecuteScalar())>=4)
            {
                cmd.CommandText="SELECT name,color FROM incoming.tags ORDER BY color,name";
                var labels=new List<MatchTag>();
                using(var reader=cmd.ExecuteReader()) while(reader.Read()) labels.Add(new(reader.GetString(0),reader.GetInt32(1)));
                foreach(var label in labels)
                {
                    token.ThrowIfCancellationRequested();cmd.Parameters.Clear();
                    cmd.CommandText="""
                        INSERT OR IGNORE INTO tags(name,color)
                        SELECT $name,CASE WHEN EXISTS(SELECT 1 FROM tags WHERE color=$color)
                          THEN (SELECT COALESCE(MAX(color)+1,0) FROM tags) ELSE $color END;
                        """;
                    cmd.Parameters.AddWithValue("$name",label.Name);cmd.Parameters.AddWithValue("$color",label.Color);cmd.ExecuteNonQuery();
                }
                cmd.Parameters.Clear();
                cmd.CommandText="INSERT OR IGNORE INTO battle_tags(owner_id,replay_id,tag_name) SELECT owner_id,replay_id,tag_name FROM incoming.battle_tags";
                cmd.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested();
            cmd.CommandText="PRAGMA foreign_key_check";
            using(var reader=cmd.ExecuteReader()) if(reader.Read()) throw new InvalidDataException(Strings.DatabaseLinksIncomplete);
            tx.Commit(); return new(added,total-added,backup);
        }
        finally { DeleteTemporary(snapshot); }
    }
    private static void CheckIntegrity(SqliteConnection db)
    {
        using var cmd=db.CreateCommand(); cmd.CommandText="PRAGMA integrity_check";
        if(cmd.ExecuteScalar() as string!="ok") throw new InvalidDataException(Strings.DatabaseCorrupt);
    }
    private static void ValidateArchive(SqliteConnection db,CancellationToken token)
    {
        CheckIntegrity(db);
        using var cmd=db.CreateCommand(); cmd.CommandText="PRAGMA user_version";
        var version=Convert.ToInt32(cmd.ExecuteScalar());
        if(version<3 || version>BattleStore.SchemaVersion) throw new InvalidDataException(Strings.DatabaseVersionUnsupported);
        var schemas=new Dictionary<string,string[]>
        {
            ["battles"]=["owner_id","replay_id","category","played_at","character_id","character","opponent","outcome","lp","mr","data_json","saved_at"],
            ["score_snapshots"]=["owner_id","replay_id","character_id","played_at","lp","mr","season"],
            ["score_links"]=["owner_id","replay_id","next_replay_id"],
            ["sync_state"]=["owner_id","category","frontier_id","completed_at"]
        };
        if(version>=4) {schemas["tags"]=["name","color"];schemas["battle_tags"]=["owner_id","replay_id","tag_name"];}
        foreach(var (table,columns) in schemas)
        {
            cmd.CommandText="SELECT sql FROM sqlite_master WHERE type='table' AND name=$name";
            cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$name",table);
            if(cmd.ExecuteScalar() is not string definition || !definition.StartsWith("CREATE TABLE",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Strings.ChooseAppBackup);
            cmd.CommandText=$"PRAGMA table_info({table})"; cmd.Parameters.Clear();
            using var reader=cmd.ExecuteReader(); var actual=new List<string>();
            while(reader.Read()) actual.Add(reader.GetString(1));
            if(!actual.SequenceEqual(columns)) throw new InvalidDataException(Strings.DatabaseSchemaUnsupported);
        }
        cmd.CommandText="SELECT owner_id,replay_id,category,played_at,character_id,data_json,character,opponent,outcome,lp,mr,saved_at FROM battles";
        using(var reader=cmd.ExecuteReader()) while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            Battle? battle;
            try { battle=JsonSerializer.Deserialize<Battle>(reader.GetString(5)); }
            catch(JsonException) { throw new InvalidDataException(Strings.BackupUnreadableMatches); }
            if(battle==null || !new PlayerIdentity(battle.OwnerId,battle.PlayerName).IsValid || string.IsNullOrWhiteSpace(battle.ReplayId) ||
                battle.OwnerId!=reader.GetString(0) || battle.ReplayId!=reader.GetString(1) || battle.Category!=reader.GetString(2) ||
                battle.PlayedAt!=reader.GetInt64(3) || battle.CharacterId!=reader.GetInt32(4) ||
                battle.Character!=reader.GetString(6) || battle.Opponent!=reader.GetString(7) || battle.Outcome!=reader.GetString(8) ||
                battle.Lp!=(reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9)) || battle.Mr!=(reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10)) ||
                !DateTimeOffset.TryParse(reader.GetString(11),out _) ||
                battle.PlayedAt<0 || battle.PlayedAt>253402271999 || !BattleCategory.All.Any(c=>c.Key==battle.Category))
                throw new InvalidDataException(Strings.BackupInconsistentMatches);
        }
        cmd.CommandText="PRAGMA foreign_key_check";
        using(var reader=cmd.ExecuteReader()) if(reader.Read()) throw new InvalidDataException(Strings.BackupLinksIncomplete);
        cmd.CommandText="""
            SELECT COUNT(*) FROM sync_state s WHERE category IS NULL OR category NOT IN ('','all-sync','rank','casual','custom','hub')
              OR owner_id IS NULL OR length(owner_id)<>10 OR owner_id GLOB '*[^0-9]*' OR julianday(completed_at) IS NULL
              OR (frontier_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM battles b WHERE b.owner_id=s.owner_id AND (s.category IN ('','all-sync') OR b.category=s.category) AND b.replay_id=s.frontier_id));
            """;
        if(Convert.ToInt64(cmd.ExecuteScalar())!=0) throw new InvalidDataException(Strings.BackupSyncIncomplete);
        cmd.CommandText="""
            SELECT COUNT(*) FROM score_snapshots s LEFT JOIN battles b ON b.owner_id=s.owner_id AND b.replay_id=s.replay_id
            WHERE b.replay_id IS NULL OR s.character_id IS NOT b.character_id OR s.played_at IS NOT b.played_at OR s.lp IS NOT b.lp OR s.mr IS NOT b.mr;
            """;
        if(Convert.ToInt64(cmd.ExecuteScalar())!=0) throw new InvalidDataException(Strings.BackupScoresInconsistent);
        cmd.CommandText="""
            SELECT COUNT(*) FROM score_links s
            LEFT JOIN battles a ON a.owner_id=s.owner_id AND a.replay_id=s.replay_id
            LEFT JOIN battles b ON b.owner_id=s.owner_id AND b.replay_id=s.next_replay_id
            WHERE a.replay_id IS NULL OR b.replay_id IS NULL OR a.category<>'rank' OR b.category<>'rank'
              OR a.character_id<>b.character_id OR b.played_at<a.played_at OR (b.played_at=a.played_at AND b.replay_id<=a.replay_id);
            """;
        if(Convert.ToInt64(cmd.ExecuteScalar())!=0) throw new InvalidDataException(Strings.BackupScoreLinksIncomplete);
        if(version>=4)
        {
            cmd.CommandText="SELECT name,color FROM tags";
            using(var reader=cmd.ExecuteReader()) while(reader.Read())
            {
                token.ThrowIfCancellationRequested();
                try {if(reader.IsDBNull(0) || TagNames.Normalize(reader.GetString(0))!=reader.GetString(0) || reader.IsDBNull(1) || reader.GetInt32(1)<0) throw new ArgumentException();}
                catch(ArgumentException) {throw new InvalidDataException(Strings.BackupTagsInvalid);}
            }
            cmd.CommandText="""
                SELECT COUNT(*) FROM battle_tags t
                LEFT JOIN tags c ON c.name=t.tag_name COLLATE NOCASE
                LEFT JOIN battles b ON b.owner_id=t.owner_id AND b.replay_id=t.replay_id
                WHERE c.name IS NULL OR b.replay_id IS NULL;
                """;
            if(Convert.ToInt64(cmd.ExecuteScalar())!=0) throw new InvalidDataException(Strings.BackupTagLinksIncomplete);
        }
    }
    private static void DeleteTemporary(string path)
    {
        foreach(var suffix in new[]{"","-wal","-shm","-journal"}) if(File.Exists(path+suffix)) File.Delete(path+suffix);
    }
}
