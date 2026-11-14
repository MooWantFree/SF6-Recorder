using System.Diagnostics;
using System.Text.Json;

namespace Sf6Prototype;

public sealed partial class BattleStore
{
    // Read models are disposable in-memory projections. No schema or archived JSON changes.
    // A bounded set of players covers foreground browsing plus automatic account refresh.
    private sealed class SummaryCache
    {
        public readonly Dictionary<string, Battle> Rows = [];
        public readonly HashSet<string> Dirty = [];
        public List<Battle>? Ordered;
        public long Access;
        public long FilterRevision = -1;
        public readonly Dictionary<string, List<Battle>> Filters = [];
    }
    private readonly object _readGate = new();
    private readonly Dictionary<string, SummaryCache> _summaries = [];
    private long _readAccess;
    private long _readRevision;
    public long ReadRevision { get { lock (_readGate) return _readRevision; } }
    private void Changed(IEnumerable<(string Owner, string Replay)> rows)
    {
        lock (_readGate)
        {
            var changed = false;
            foreach (var row in rows)
            {
                changed = true;
                if (_summaries.TryGetValue(row.Owner, out var cache)) cache.Dirty.Add(row.Replay);
            }
            if (changed) _readRevision++;
        }
    }
    private void Changed() { lock (_readGate) _readRevision++; }
    public void InvalidateReads()
    {
        lock (_readGate) { _summaries.Clear(); _readRevision++; }
    }
    public List<Battle> QuerySummaries(BattleFilter filter)
    {
        lock (_readGate)
        {
            var cache = ReadSummaries(filter.OwnerId);
            if (filter.Category == null && filter.Character == null && filter.OpponentCharacter == null &&
                filter.Outcome == null && string.IsNullOrWhiteSpace(filter.Search) && filter.From == null &&
                filter.Until == null && !filter.FavoritesOnly && filter.Tags is not { Count: > 0 }) return cache.Ordered!;
            if (cache.FilterRevision != _readRevision) { cache.Filters.Clear(); cache.FilterRevision = _readRevision; }
            var key = JsonSerializer.Serialize(filter);
            if (cache.Filters.TryGetValue(key, out var cached)) return cached;
            using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT replay_id FROM battles WHERE " + FilterSql(cmd, filter) + " ORDER BY played_at DESC,replay_id";
            using var reader = cmd.ExecuteReader(); var result = new List<Battle>();
            while (reader.Read()) if (cache.Rows.TryGetValue(reader.GetString(0), out var row)) result.Add(row);
            if (cache.Filters.Count >= 16) cache.Filters.Clear();
            cache.Filters[key] = result;
            return result;
        }
    }
    // Bounded SQL reads for consumers that only need a head/page, rather than statistics.
    public List<Battle> QuerySummaryPage(BattleFilter filter, int offset, int count)
    {
        if (offset < 0 || count < 0) throw new ArgumentOutOfRangeException();
        using var db = Open(); using var cmd = db.CreateCommand();
        // Select IDs before projecting JSON, so a sort cannot deserialize every candidate.
        cmd.CommandText = "SELECT json_remove(data_json,'$.RawJson') FROM battles WHERE rowid IN (SELECT rowid FROM battles WHERE " + FilterSql(cmd, filter) +
            " ORDER BY played_at DESC,replay_id LIMIT $count OFFSET $offset) ORDER BY played_at DESC,replay_id";
        Add(cmd, "$count", count); Add(cmd, "$offset", offset);
        using var reader = cmd.ExecuteReader(); var result = new List<Battle>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<Battle>(reader.GetString(0))!);
        return result;
    }
    public Battle? Detail(string owner, string replay)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT data_json FROM battles WHERE owner_id=$owner AND replay_id=$replay";
        Add(cmd, "$owner", owner); Add(cmd, "$replay", replay);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<Battle>(json) : null;
    }
    public List<Battle> PrefixSummaries(string owner, IReadOnlyList<string> ids)
    {
        lock (_readGate)
        {
            var cache = ReadSummaries(owner);
            return ids.Where(cache.Rows.ContainsKey).Select(id => cache.Rows[id]).ToList();
        }
    }
    private SummaryCache ReadSummaries(string owner)
    {
        if (!_summaries.TryGetValue(owner, out var cache))
        {
            if (_summaries.Count >= 4) _summaries.Remove(_summaries.MinBy(p => p.Value.Access).Key);
            _summaries[owner] = cache = new();
        }
        cache.Access = ++_readAccess;
        if (cache.Ordered != null && cache.Dirty.Count == 0) return cache;
        var started = Stopwatch.GetTimestamp();
        var initial = cache.Ordered == null;
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json_remove(data_json,'$.RawJson') FROM battles WHERE owner_id=$owner" +
            (initial ? "" : " AND replay_id IN (SELECT value FROM json_each($ids))");
        Add(cmd, "$owner", owner);
        if (!initial) Add(cmd, "$ids", JsonSerializer.Serialize(cache.Dirty));
        // Apply only after a complete successful read; failed reads remain retryable.
        using var reader = cmd.ExecuteReader(); var loaded = new List<Battle>();
        while (reader.Read()) loaded.Add(JsonSerializer.Deserialize<Battle>(reader.GetString(0))!);
        foreach (var battle in loaded) cache.Rows[battle.ReplayId] = battle;
        var changedIds=loaded.Select(b=>b.ReplayId).ToHashSet();
        var additions=loaded.OrderByDescending(b=>b.PlayedAt).ThenBy(b=>b.ReplayId,StringComparer.Ordinal).ToList();
        var previous=cache.Ordered ?? [];
        var merged=new List<Battle>(cache.Rows.Count);var index=0;
        foreach(var row in previous)
        {
            if(changedIds.Contains(row.ReplayId)) continue;
            while(index<additions.Count && (additions[index].PlayedAt>row.PlayedAt ||
                additions[index].PlayedAt==row.PlayedAt && StringComparer.Ordinal.Compare(additions[index].ReplayId,row.ReplayId)<0))
                merged.Add(additions[index++]);
            merged.Add(row);
        }
        while(index<additions.Count) merged.Add(additions[index++]);
        cache.Ordered=merged;
        cache.Dirty.Clear();
        Diagnostics.Write("archive", initial ? "summary_load" : "summary_update", elapsedMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return cache;
    }
}
