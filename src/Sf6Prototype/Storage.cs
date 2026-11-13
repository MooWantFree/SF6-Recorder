using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Sf6Prototype;

public sealed record BattlePrefix(IReadOnlyList<string> ReplayIds,int NextPage,int TotalPages);

public sealed partial class BattleStore
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
        """;
        cmd.ExecuteNonQuery();
        cmd.CommandText="PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if(version<2)
        {
            // Version 1 mistook the master_league badge for a season. Preserve it as a badge,
            // invalidate only the erroneous season metadata, and keep every battle/score.
            using var tx=db.BeginTransaction(); cmd.Transaction=tx;
            cmd.CommandText="""
            UPDATE battles SET data_json=json_set(data_json,'$.MasterLeague',json_extract(data_json,'$.Season'),'$.Season',NULL)
              WHERE json_type(data_json,'$.MasterLeague') IS NULL;
            UPDATE score_snapshots SET season=NULL;
            PRAGMA user_version=2;
            """;
            cmd.ExecuteNonQuery(); tx.Commit();
            cmd.Transaction = null;
        }
        if (version < 3)
        {
            cmd.CommandText = """
            INSERT OR IGNORE INTO score_links(owner_id,replay_id,next_replay_id)
            SELECT owner_id,replay_id,next_id FROM (
              SELECT owner_id,replay_id,LEAD(replay_id) OVER(PARTITION BY owner_id,character_id ORDER BY played_at,replay_id) AS next_id
              FROM battles WHERE category='rank' AND owner_id IN (SELECT owner_id FROM sync_state WHERE category='rank')
            ) WHERE next_id IS NOT NULL;
            PRAGMA user_version=3;
            """;
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS tags (
              name TEXT PRIMARY KEY COLLATE NOCASE, color INTEGER NOT NULL CHECK(color>=0));
            CREATE TABLE IF NOT EXISTS battle_tags (
              owner_id TEXT NOT NULL, replay_id TEXT NOT NULL, tag_name TEXT NOT NULL COLLATE NOCASE,
              PRIMARY KEY(owner_id,replay_id,tag_name),
              FOREIGN KEY(owner_id,replay_id) REFERENCES battles(owner_id,replay_id),
              FOREIGN KEY(tag_name) REFERENCES tags(name));
            CREATE INDEX IF NOT EXISTS ix_battle_tags_name ON battle_tags(owner_id,tag_name,replay_id);
            """;
        cmd.ExecuteNonQuery();
        if(version<4) {cmd.CommandText="PRAGMA user_version=4";cmd.ExecuteNonQuery();}
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public int SavePage(IEnumerable<Battle> rows)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var added = 0;
        var changed=new HashSet<(string Owner,string Replay)>();
        foreach (var b in rows)
        {
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO battles VALUES($owner,$id,$category,$time,$cid,$char,$opponent,$outcome,$lp,$mr,$data,$saved) ON CONFLICT(owner_id,replay_id) DO NOTHING";
            Add(cmd, "$owner", b.OwnerId); Add(cmd, "$id", b.ReplayId); Add(cmd, "$category", b.Category); Add(cmd, "$time", b.PlayedAt);
            Add(cmd, "$cid", b.CharacterId); Add(cmd, "$char", b.Character); Add(cmd, "$opponent", b.Opponent); Add(cmd, "$outcome", b.Outcome);
            Add(cmd, "$lp", b.Lp); Add(cmd, "$mr", b.Mr); Add(cmd, "$data", JsonSerializer.Serialize(b)); Add(cmd, "$saved", DateTimeOffset.UtcNow.ToString("O"));
            var inserted=cmd.ExecuteNonQuery(); added+=inserted;
            if(inserted>0) changed.Add((b.OwnerId,b.ReplayId));
            if(inserted==0 && b.RawJson is {ValueKind:JsonValueKind.Object} raw)
            {
                using var payload=db.CreateCommand();payload.Transaction=tx;
                // Retain the complete replay object, including fields this version does not use.
                // Account/session metadata belongs to the surrounding response, not this object.
                payload.CommandText="""
                    UPDATE battles SET data_json=json_set(data_json,'$.RawJson',json($raw))
                    WHERE owner_id=$owner AND replay_id=$id AND COALESCE(json_extract(data_json,'$.RawJson'),'')<>json($raw)
                    """;
                Add(payload,"$owner",b.OwnerId);Add(payload,"$id",b.ReplayId);Add(payload,"$raw",raw.GetRawText());payload.ExecuteNonQuery();
            }
            // Older archives did not retain the other player's rank. Enrich only missing
            // fields when a known replay is fetched again, without counting it as new.
            if(inserted==0 && (b.OpponentLeagueRank!=null || b.OpponentMasterLeague!=null || b.MasterRatingRanking!=null || b.OpponentMasterRatingRanking!=null))
            {
                using var metadata=db.CreateCommand(); metadata.Transaction=tx;
                metadata.CommandText="""
                    UPDATE battles SET data_json=json_set(data_json,
                        '$.OpponentLeagueRank',COALESCE(json_extract(data_json,'$.OpponentLeagueRank'),$rank),
                        '$.OpponentMasterLeague',COALESCE(json_extract(data_json,'$.OpponentMasterLeague'),$master),
                        '$.MasterRatingRanking',COALESCE(json_extract(data_json,'$.MasterRatingRanking'),$position),
                        '$.OpponentMasterRatingRanking',COALESCE(json_extract(data_json,'$.OpponentMasterRatingRanking'),$opponentPosition))
                    WHERE owner_id=$owner AND replay_id=$id AND
                        ((json_extract(data_json,'$.OpponentLeagueRank') IS NULL AND $rank IS NOT NULL) OR
                         (json_extract(data_json,'$.OpponentMasterLeague') IS NULL AND $master IS NOT NULL) OR
                         (json_extract(data_json,'$.MasterRatingRanking') IS NULL AND $position IS NOT NULL) OR
                         (json_extract(data_json,'$.OpponentMasterRatingRanking') IS NULL AND $opponentPosition IS NOT NULL))
                    """;
                Add(metadata,"$owner",b.OwnerId); Add(metadata,"$id",b.ReplayId);
                Add(metadata,"$rank",b.OpponentLeagueRank); Add(metadata,"$master",b.OpponentMasterLeague);
                Add(metadata,"$position",b.MasterRatingRanking); Add(metadata,"$opponentPosition",b.OpponentMasterRatingRanking);
                if(metadata.ExecuteNonQuery()>0) changed.Add((b.OwnerId,b.ReplayId));
            }
            using var score = db.CreateCommand(); score.Transaction = tx;
            score.CommandText = "INSERT INTO score_snapshots VALUES($owner,$id,$cid,$time,$lp,$mr,$season) ON CONFLICT(owner_id,replay_id) DO NOTHING";
            Add(score, "$owner", b.OwnerId); Add(score, "$id", b.ReplayId); Add(score, "$cid", b.CharacterId); Add(score, "$time", b.PlayedAt);
            Add(score, "$lp", b.Lp); Add(score, "$mr", b.Mr); Add(score, "$season", b.Season); score.ExecuteNonQuery();
        }
        tx.Commit(); Changed(changed); return added;
    }
    public string? Frontier(string owner, string category)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT frontier_id FROM sync_state WHERE owner_id=$owner AND category=$category";
        Add(cmd, "$owner", owner); Add(cmd, "$category", category); return cmd.ExecuteScalar() as string;
    }
    public BattlePrefix? Prefix(string owner,string category)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT data_json FROM history_prefixes WHERE owner_id=$owner AND category=$category";
        Add(cmd,"$owner",owner);Add(cmd,"$category",category);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<BattlePrefix>(json) : null;
    }
    public void SavePrefix(string owner,string category,BattlePrefix prefix)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT INTO history_prefixes VALUES($owner,$category,$json) ON CONFLICT(owner_id,category) DO UPDATE SET data_json=excluded.data_json";
        Add(cmd,"$owner",owner);Add(cmd,"$category",category);Add(cmd,"$json",JsonSerializer.Serialize(prefix));cmd.ExecuteNonQuery();
    }
    public List<Battle> PrefixBattles(string owner,IReadOnlyList<string> replayIds)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT b.data_json FROM json_each($ids) p JOIN battles b ON b.owner_id=$owner AND b.replay_id=p.value ORDER BY p.key";
        Add(cmd,"$owner",owner);Add(cmd,"$ids",JsonSerializer.Serialize(replayIds));
        using var reader=cmd.ExecuteReader();var rows=new List<Battle>();
        while(reader.Read()) rows.Add(JsonSerializer.Deserialize<Battle>(reader.GetString(0))!);
        return rows;
    }
    public bool HasCategorySnapshot(string owner, string category)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT EXISTS(SELECT 1 FROM sync_state WHERE owner_id=$owner AND category=$category) AND NOT EXISTS(SELECT 1 FROM sync_pending WHERE owner_id=$owner AND category=$category)";
        Add(cmd,"$owner",owner); Add(cmd,"$category",category); return Convert.ToInt32(cmd.ExecuteScalar())==1;
    }
    public bool HasCategoryCheckpoint(string owner,string category)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT EXISTS(SELECT 1 FROM sync_state WHERE owner_id=$owner AND category=$category)";
        Add(cmd,"$owner",owner);Add(cmd,"$category",category);return Convert.ToInt32(cmd.ExecuteScalar())==1;
    }
    public void MarkCategoryPending(string owner,string category)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT OR IGNORE INTO sync_pending VALUES($owner,$category)";
        Add(cmd,"$owner",owner); Add(cmd,"$category",category); cmd.ExecuteNonQuery();
    }
    public bool IsCategoryPending(string owner,string category)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT EXISTS(SELECT 1 FROM sync_pending WHERE owner_id=$owner AND category=$category)";
        Add(cmd,"$owner",owner); Add(cmd,"$category",category); return Convert.ToInt32(cmd.ExecuteScalar())==1;
    }
    public void SaveRankLinks(string owner, IReadOnlyList<Battle> page, Dictionary<int,string> newer)
    {
        using var db=Open(); using var tx=db.BeginTransaction();
        foreach(var b in page.OrderByDescending(b=>b.PlayedAt).ThenByDescending(b=>b.ReplayId))
        {
            if(newer.TryGetValue(b.CharacterId,out var next) && next!=b.ReplayId)
            {
                using var cmd=db.CreateCommand(); cmd.Transaction=tx;
                cmd.CommandText="INSERT INTO score_links VALUES($owner,$id,$next) ON CONFLICT(owner_id,replay_id) DO UPDATE SET next_replay_id=excluded.next_replay_id";
                Add(cmd,"$owner",owner); Add(cmd,"$id",b.ReplayId); Add(cmd,"$next",next); cmd.ExecuteNonQuery();
            }
            newer[b.CharacterId]=b.ReplayId;
        }
        tx.Commit(); Changed();
    }
    public Dictionary<string,string> ScoreLinks(string owner)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT replay_id,next_replay_id FROM score_links WHERE owner_id=$owner"; Add(cmd,"$owner",owner);
        using var reader=cmd.ExecuteReader(); var result=new Dictionary<string,string>();
        while(reader.Read()) result[reader.GetString(0)]=reader.GetString(1); return result;
    }
    public DateTimeOffset? LastSuccessfulLoad(string owner)
    {
        using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT finished_at FROM sync_runs WHERE owner_id=$owner AND status IN ('completed','paged') ORDER BY id DESC LIMIT 1";
        Add(cmd,"$owner",owner); return DateTimeOffset.TryParse(cmd.ExecuteScalar() as string,out var date) ? date : null;
    }
    public void CompleteCategory(string owner, string category, string? head)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO sync_state VALUES($owner,$category,$head,$time) ON CONFLICT(owner_id,category) DO UPDATE SET frontier_id=excluded.frontier_id,completed_at=excluded.completed_at; DELETE FROM sync_pending WHERE owner_id=$owner AND category=$category; DELETE FROM history_prefixes WHERE owner_id=$owner AND category=$category";
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
    public DateTimeOffset? LastCompletedSync(string owner)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT finished_at FROM sync_runs WHERE owner_id=$owner AND status='completed' ORDER BY id DESC LIMIT 1";
        Add(cmd, "$owner", owner);
        return DateTimeOffset.TryParse(cmd.ExecuteScalar() as string, out var date) ? date : null;
    }
    public bool IsSyncIncomplete(string owner)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT status FROM sync_runs WHERE owner_id=$owner ORDER BY id DESC LIMIT 1";
        Add(cmd, "$owner", owner);
        return cmd.ExecuteScalar() is string status && status != "completed";
    }
    public List<Battle> Query(BattleFilter filter)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM battles WHERE " + FilterSql(cmd,filter) + " ORDER BY played_at DESC,replay_id";
        using var reader = cmd.ExecuteReader(); var rows = new List<Battle>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<Battle>(reader.GetString(0))!);
        return rows;
    }
    private static string FilterSql(SqliteCommand cmd,BattleFilter filter)
    {
        var where = new List<string> { "owner_id=$owner" }; Add(cmd, "$owner", filter.OwnerId);
        if (filter.Category != null) { where.Add("category=$category"); Add(cmd, "$category", filter.Category); }
        if (filter.Character != null) { where.Add("character=$character"); Add(cmd, "$character", filter.Character); }
        if (filter.OpponentCharacter != null) { where.Add("json_extract(data_json,'$.OpponentCharacter')=$otherCharacter"); Add(cmd,"$otherCharacter",filter.OpponentCharacter); }
        var tags=filter.Tags?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if(tags.Length>0)
        {
            var names=tags.Select((name,i)=> {var key="$tag"+i;Add(cmd,key,name);return key;});
            var matches="SELECT COUNT(*) FROM battle_tags t WHERE t.owner_id=battles.owner_id AND t.replay_id=battles.replay_id AND t.tag_name IN ("+string.Join(',',names)+")";
            where.Add("("+matches+")"+(filter.MatchAllTags ? "="+tags.Length : ">0"));
        }
        else if(filter.FavoritesOnly) where.Add("EXISTS(SELECT 1 FROM battle_tags t WHERE t.owner_id=battles.owner_id AND t.replay_id=battles.replay_id)");
        if (filter.Outcome != null) { where.Add("outcome=$outcome"); Add(cmd, "$outcome", filter.Outcome); }
        if (!string.IsNullOrWhiteSpace(filter.Search)) { where.Add("(instr(lower(opponent),lower($search))>0 OR instr(lower(replay_id),lower($search))>0 OR instr(json_extract(data_json,'$.OpponentId'),$search)>0)"); Add(cmd, "$search", filter.Search.Trim()); }
        if (filter.From != null) { where.Add("played_at >= $from"); Add(cmd, "$from", filter.From); }
        if (filter.Until != null) { where.Add("played_at < $until"); Add(cmd, "$until", filter.Until); }
        return string.Join(" AND ",where);
    }
    public static List<CharacterSummary> Summaries(IEnumerable<Battle> rows) => rows.GroupBy(b => new { b.CharacterId, b.Character })
        .Select(g => { var last = g.OrderByDescending(b => b.PlayedAt).First(); return new CharacterSummary(g.Key.Character, g.Key.CharacterId, g.Count(), g.Count(b => b.Outcome == "胜"), g.Count(b => b.Outcome == "负"), g.Count(b => b.Outcome == "平"), last.Lp, last.Mr); })
        .OrderByDescending(c => c.Count).ToList();
    // Official replay scores are recorded before the match, so a ranked match's settlement is the
    // next ranked record of the same character minus this one. Only ranked matches move LP/MR.
    // The newest match uses the profile score captured after the last completed sync.
    public static List<ScorePoint> Scores(IEnumerable<Battle> rows, CurrentScores? current = null, bool settlementsComplete = true, IReadOnlyDictionary<string,string>? links = null)
    {
        // During backfill there may be a gap between old records and new pages.
        // Show recorded pre-match scores, but do not label that gap as one match's settlement.
        if (!settlementsComplete) return rows.Where(b => b.Category == "rank").OrderBy(b => b.PlayedAt).ThenBy(b => b.ReplayId)
            .Select(b => new ScorePoint(b.PlayedAt, b.Time, b.Lp, b.Mr, null, null, b.ReplayId)).ToList();
        var result = new List<ScorePoint>();
        foreach (var group in rows.Where(b => b.Category == "rank").GroupBy(b => b.CharacterId))
        {
            var ordered = group.OrderBy(b => b.PlayedAt).ThenBy(b => b.ReplayId).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var b = ordered[i];
                if (i + 1 < ordered.Count)
                {
                    var next = ordered[i + 1];
                    var linked = links == null || links.TryGetValue(b.ReplayId,out var knownNext) && knownNext==next.ReplayId;
                    result.Add(new(b.PlayedAt, b.Time, b.Lp, b.Mr, linked ? LpDelta(b.Lp,next.Lp) : null, linked ? MrDelta(b, next.Mr, next.Season) : null, b.ReplayId));
                    continue;
                }
                var now = current != null && current.FetchedAt.ToUnixTimeSeconds() > b.PlayedAt
                    ? current.Characters.FirstOrDefault(c => c.CharacterId == b.CharacterId && c.Lp >= 0) : null;
                int? lp = Plausible(b, LpDelta(b.Lp,now?.Lp)), mr = Plausible(b, now == null ? null : MrDelta(b, now.Mr, current!.Season));
                result.Add(new(b.PlayedAt, b.Time, b.Lp, b.Mr, lp, mr, b.ReplayId, lp != null || mr != null));
            }
        }
        return result.OrderBy(p => p.PlayedAt).ThenBy(p => p.ReplayId).ToList();
    }
    private static int? LpDelta(int? before,int? after) => before>=0 && after>=0 ? after-before : null;
    private static int? MrDelta(Battle b, int? nextMr, int? nextSeason) =>
        b.Mr > 0 && nextMr > 0 && b.Season != null && b.Season == nextSeason ? nextMr - b.Mr : null;
    // Rejects an estimate that disagrees with the result, e.g. when a newer match is not yet listed.
    private static int? Plausible(Battle b, int? delta) =>
        delta == null || b.Outcome switch { "胜" => delta > 0, "负" => delta <= 0, _ => true } ? delta : null;
    private static void Add(SqliteCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
