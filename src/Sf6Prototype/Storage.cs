using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sf6Prototype;

public sealed class BattleStore
{
    private readonly string _connection;
    public BattleStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 10 }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
        PRAGMA journal_mode=WAL;
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
        CREATE TABLE IF NOT EXISTS sync_state (
          owner_id TEXT NOT NULL, category TEXT NOT NULL, frontier_id TEXT, completed_at TEXT NOT NULL,
          PRIMARY KEY(owner_id,category));
        CREATE TABLE IF NOT EXISTS sync_runs (
          id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id TEXT NOT NULL, started_at TEXT NOT NULL,
          finished_at TEXT, status TEXT NOT NULL, requests INTEGER NOT NULL DEFAULT 0,
          inserted INTEGER NOT NULL DEFAULT 0, detail TEXT);
        PRAGMA user_version=1;
        """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public int SavePage(IEnumerable<Battle> rows)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var added = 0;
        foreach (var b in rows)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO battles VALUES($owner,$id,$category,$time,$cid,$char,$opponent,$outcome,$lp,$mr,$data,$saved) ON CONFLICT(owner_id,replay_id) DO NOTHING";
            Add(cmd, "$owner", b.OwnerId); Add(cmd, "$id", b.ReplayId); Add(cmd, "$category", b.Category); Add(cmd, "$time", b.PlayedAt);
            Add(cmd, "$cid", b.CharacterId); Add(cmd, "$char", b.Character); Add(cmd, "$opponent", b.Opponent); Add(cmd, "$outcome", b.Outcome);
            Add(cmd, "$lp", b.Lp); Add(cmd, "$mr", b.Mr); Add(cmd, "$data", JsonSerializer.Serialize(b)); Add(cmd, "$saved", DateTimeOffset.UtcNow.ToString("O"));
            added += cmd.ExecuteNonQuery();
            using var score = db.CreateCommand(); score.Transaction = tx;
            score.CommandText = "INSERT INTO score_snapshots VALUES($owner,$id,$cid,$time,$lp,$mr,$season) ON CONFLICT(owner_id,replay_id) DO NOTHING";
            Add(score, "$owner", b.OwnerId); Add(score, "$id", b.ReplayId); Add(score, "$cid", b.CharacterId); Add(score, "$time", b.PlayedAt);
            Add(score, "$lp", b.Lp); Add(score, "$mr", b.Mr); Add(score, "$season", b.Season); score.ExecuteNonQuery();
        }
        tx.Commit(); return added;
    }
    public string? Frontier(string owner, string category)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT frontier_id FROM sync_state WHERE owner_id=$owner AND category=$category";
        Add(cmd, "$owner", owner); Add(cmd, "$category", category); return cmd.ExecuteScalar() as string;
    }
    public void CompleteCategory(string owner, string category, string? head)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_state VALUES($owner,$category,$head,$time) ON CONFLICT(owner_id,category) DO UPDATE SET frontier_id=excluded.frontier_id,completed_at=excluded.completed_at";
        Add(cmd, "$owner", owner); Add(cmd, "$category", category); Add(cmd, "$head", head); Add(cmd, "$time", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }
    public long BeginRun(string owner)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_runs(owner_id,started_at,status) VALUES($owner,$time,'running'); SELECT last_insert_rowid();";
        Add(cmd, "$owner", owner); Add(cmd, "$time", DateTimeOffset.UtcNow.ToString("O")); return (long)cmd.ExecuteScalar()!;
    }
    public void FinishRun(long id, string status, int requests, int inserted, string detail)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE sync_runs SET finished_at=$time,status=$status,requests=$requests,inserted=$inserted,detail=$detail WHERE id=$id";
        Add(cmd, "$id", id); Add(cmd, "$time", DateTimeOffset.UtcNow.ToString("O")); Add(cmd, "$status", status);
        Add(cmd, "$requests", requests); Add(cmd, "$inserted", inserted); Add(cmd, "$detail", detail); cmd.ExecuteNonQuery();
    }
    public List<Battle> Query(BattleFilter filter)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        var where = new List<string> { "owner_id=$owner" }; Add(cmd, "$owner", filter.OwnerId);
        if (filter.Category != null) { where.Add("category=$category"); Add(cmd, "$category", filter.Category); }
        if (filter.Character != null) { where.Add("character=$character"); Add(cmd, "$character", filter.Character); }
        if (filter.Outcome != null) { where.Add("outcome=$outcome"); Add(cmd, "$outcome", filter.Outcome); }
        if (!string.IsNullOrWhiteSpace(filter.Search)) { where.Add("(instr(lower(opponent),lower($search))>0 OR instr(lower(replay_id),lower($search))>0)"); Add(cmd, "$search", filter.Search.Trim()); }
        if (filter.From != null) { where.Add("played_at >= $from"); Add(cmd, "$from", filter.From); }
        if (filter.Until != null) { where.Add("played_at < $until"); Add(cmd, "$until", filter.Until); }
        cmd.CommandText = "SELECT data_json FROM battles WHERE " + string.Join(" AND ", where) + " ORDER BY played_at DESC,replay_id";
        using var reader = cmd.ExecuteReader(); var rows = new List<Battle>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<Battle>(reader.GetString(0))!);
        return rows;
    }
    public static List<CharacterSummary> Summaries(IEnumerable<Battle> rows) => rows.GroupBy(b => new { b.CharacterId, b.Character })
        .Select(g => { var last = g.OrderByDescending(b => b.PlayedAt).First(); return new CharacterSummary(g.Key.Character, g.Key.CharacterId, g.Count(), g.Count(b => b.Outcome == "胜"), g.Count(b => b.Outcome == "负"), g.Count(b => b.Outcome == "平"), last.Lp, last.Mr); })
        .OrderByDescending(c => c.Count).ToList();
    public static List<ScorePoint> Scores(IEnumerable<Battle> rows)
    {
        var result = new List<ScorePoint>(); Battle? previous = null;
        foreach (var b in rows.OrderBy(b => b.PlayedAt).ThenBy(b => b.ReplayId))
        {
            var same = previous?.CharacterId == b.CharacterId;
            result.Add(new(b.PlayedAt, b.Time, b.Lp, b.Mr, same ? b.Lp - previous!.Lp : null,
                same && b.Mr > 0 && previous!.Mr > 0 && b.Season == previous.Season ? b.Mr - previous.Mr : null, b.ReplayId)); previous = b;
        }
        return result;
    }
    private static void Add(SqliteCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
