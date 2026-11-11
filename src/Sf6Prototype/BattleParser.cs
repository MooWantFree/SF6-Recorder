using System.Text.Json;

namespace Sf6Prototype;

public record BattleRow(string ReplayId, string Time, string Mode, string Player,
    string Character, string Opponent, string OpponentCharacter, string Score, string Result);
public record BattleParseResult(bool HasReplayList, int SourceCount, IReadOnlyList<BattleRow> Matches);

public static class BattleParser
{
    public static BattleParseResult Parse(string json, string playerId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("props", out var props)) root = props;
        if (root.TryGetProperty("pageProps", out var pageProps)) root = pageProps;
        if (!root.TryGetProperty("replay_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return new(false, 0, []);
        var result = new List<BattleRow>();
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("player1_info", out var p1) || !item.TryGetProperty("player2_info", out var p2)) continue;
            var id1 = ReadPlayer(p1, "short_id");
            var id2 = ReadPlayer(p2, "short_id");
            if (id1 != playerId && id2 != playerId) continue;
            var mine = id1 == playerId ? p1 : p2;
            var other = id1 == playerId ? p2 : p1;
            var myScore = Rounds(mine);
            var theirScore = Rounds(other);
            var score = myScore.HasValue && theirScore.HasValue ? $"{myScore} : {theirScore}" : "—";
            var outcome = !myScore.HasValue || !theirScore.HasValue ? "待确认"
                : myScore > theirScore ? "胜" : myScore < theirScore ? "负" : "平";
            result.Add(new(Text(item, "replay_id"), Timestamp(Text(item, "uploaded_at")),
                Text(item, "replay_battle_type_name"), ReadPlayer(mine, "fighter_id"),
                Text(mine, "character_name"), ReadPlayer(other, "fighter_id"),
                Text(other, "character_name"), score, outcome));
        }
        return new(true, list.GetArrayLength(), result);
    }
    private static string ReadPlayer(JsonElement player, string key) =>
        player.TryGetProperty("player", out var p) ? Text(p, key) : "";
    private static string Text(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ToString() : "";
    private static int? Rounds(JsonElement player)
    {
        if (!player.TryGetProperty("round_results", out var rounds) || rounds.ValueKind != JsonValueKind.Array || rounds.GetArrayLength() == 0) return null;
        var won = 0;
        foreach (var round in rounds.EnumerateArray())
        {
            if (!round.TryGetInt32(out var value)) return null;
            if (value > 0) won++;
        }
        return won;
    }
    private static string Timestamp(string value)
    {
        if (!long.TryParse(value, out var time)) return value;
        try
        {
            var date = time > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(time) : DateTimeOffset.FromUnixTimeSeconds(time);
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(date, "Taipei Standard Time").ToString("yyyy-MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException) { return value; }
    }
}
