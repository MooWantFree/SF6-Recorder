using System.IO;
using Microsoft.Data.Sqlite;

namespace Sf6Prototype;

public sealed partial class BattleStore
{
    public const int SchemaVersion = 5;
    private void InitializeSchema()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        // Reject future archives before changing journal mode, schema or records.
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > SchemaVersion) throw new InvalidDataException(Strings.DatabaseVersionUnsupported);
        cmd.CommandText = "PRAGMA journal_mode=WAL"; cmd.ExecuteNonQuery();
        using var transaction = db.BeginTransaction(); cmd.Transaction = transaction;
        void Execute(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        void Migrate(int target, string sql)
        {
            if (version >= target) return;
            Execute(sql);
            Execute("PRAGMA user_version=" + target);
            version = target;
        }
        Execute("""
        CREATE TABLE IF NOT EXISTS battles (
          owner_id TEXT NOT NULL, replay_id TEXT NOT NULL, category TEXT NOT NULL, played_at INTEGER NOT NULL,
          character_id INTEGER NOT NULL, character TEXT NOT NULL, opponent TEXT NOT NULL, outcome TEXT NOT NULL,
          lp INTEGER, mr INTEGER, data_json TEXT NOT NULL, saved_at TEXT NOT NULL,
          PRIMARY KEY(owner_id,replay_id));
        CREATE INDEX IF NOT EXISTS ix_battles_filter ON battles(owner_id,category,character_id,played_at DESC);
        CREATE TABLE IF NOT EXISTS score_snapshots (
          owner_id TEXT NOT NULL, replay_id TEXT NOT NULL, character_id INTEGER NOT NULL, played_at INTEGER NOT NULL,
          lp INTEGER, mr INTEGER, season INTEGER, PRIMARY KEY(owner_id,replay_id),
          FOREIGN KEY(owner_id,replay_id) REFERENCES battles(owner_id,replay_id));
        CREATE INDEX IF NOT EXISTS ix_scores_character ON score_snapshots(owner_id,character_id,played_at);
        CREATE TABLE IF NOT EXISTS score_links (
          owner_id TEXT NOT NULL, replay_id TEXT NOT NULL, next_replay_id TEXT NOT NULL,
          PRIMARY KEY(owner_id,replay_id));
        CREATE TABLE IF NOT EXISTS sync_state (
          owner_id TEXT NOT NULL, category TEXT NOT NULL, frontier_id TEXT, completed_at TEXT NOT NULL,
          PRIMARY KEY(owner_id,category));
        CREATE TABLE IF NOT EXISTS sync_runs (
          id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id TEXT NOT NULL, started_at TEXT NOT NULL,
          finished_at TEXT, status TEXT NOT NULL, requests INTEGER NOT NULL DEFAULT 0,
          inserted INTEGER NOT NULL DEFAULT 0, detail TEXT);
        CREATE TABLE IF NOT EXISTS sync_pending (
          owner_id TEXT NOT NULL, category TEXT NOT NULL, PRIMARY KEY(owner_id,category));
        CREATE TABLE IF NOT EXISTS history_prefixes (
          owner_id TEXT NOT NULL, category TEXT NOT NULL, data_json TEXT NOT NULL,
          PRIMARY KEY(owner_id,category));
        """);
        // Preserve the original migration semantics and every archived replay.
        Migrate(2, """
            UPDATE battles SET data_json=json_set(data_json,'$.MasterLeague',json_extract(data_json,'$.Season'),'$.Season',NULL)
              WHERE json_type(data_json,'$.MasterLeague') IS NULL;
            UPDATE score_snapshots SET season=NULL;
            """);
        Migrate(3, """
            INSERT OR IGNORE INTO score_links(owner_id,replay_id,next_replay_id)
            SELECT owner_id,replay_id,next_id FROM (
              SELECT owner_id,replay_id,LEAD(replay_id) OVER(PARTITION BY owner_id,character_id ORDER BY played_at,replay_id) AS next_id
              FROM battles WHERE category='rank' AND owner_id IN (SELECT owner_id FROM sync_state WHERE category='rank')
            ) WHERE next_id IS NOT NULL;
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS tags (
              name TEXT PRIMARY KEY COLLATE NOCASE, color INTEGER NOT NULL CHECK(color>=0));
            CREATE TABLE IF NOT EXISTS battle_tags (
              owner_id TEXT NOT NULL, replay_id TEXT NOT NULL, tag_name TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY(owner_id,replay_id,tag_name),
              FOREIGN KEY(owner_id,replay_id) REFERENCES battles(owner_id,replay_id),
              FOREIGN KEY(tag_name) REFERENCES tags(name));
            CREATE INDEX IF NOT EXISTS ix_battle_tags_name ON battle_tags(owner_id,tag_name,replay_id);
            """);
        Migrate(4, "SELECT 1");
        // A covering read index avoids scanning archived raw JSON for list totals/order.
        // Version 5 changes no rows, columns or serialized battle fields.
        Migrate(5, "CREATE INDEX IF NOT EXISTS ix_battles_history ON battles(owner_id,played_at DESC,replay_id,category,character,outcome)");
        transaction.Commit();
    }
}
