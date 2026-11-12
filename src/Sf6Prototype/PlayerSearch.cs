using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Sf6Prototype;

public static class PlayerLookup
{
    public static string ParseUid(string input)
    {
        var id=input.Trim();
        if(id.Length!=10 || !id.All(char.IsAsciiDigit) || id.All(c=>c=='0')) throw new ArgumentException("请输入 10 位数字 UID。");
        return id;
    }
    public static async Task<PlayerIdentity> FindAsync(IProfileSource source,string input,CancellationToken token)
    {
        var id=ParseUid(input);
        token.ThrowIfCancellationRequested();
        try
        {
            var json=await source.FetchPlayAsync(id,token); token.ThrowIfCancellationRequested();
            using var doc=JsonDocument.Parse(json);
            if(doc.RootElement.TryGetProperty("notFound",out var missing) && missing.ValueKind==JsonValueKind.True)
                throw new InvalidDataException("未找到该玩家，请检查 UID。");
            var profile=RankProfiles.ParseProfile(json,id);
            if(string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidDataException("暂时无法读取该玩家资料，请稍后重试。");
            return new(id,profile.Name);
        }
        catch(HttpRequestException ex) when(ex.StatusCode==HttpStatusCode.NotFound)
        { throw new InvalidDataException("未找到该玩家，请检查 UID。"); }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("暂时无法读取该玩家资料，请稍后重试。"); }
    }
}

public sealed class PlayerSearchHistory
{
    private readonly string _path;
    public IReadOnlyList<PlayerIdentity> Entries { get; private set; }
    public PlayerSearchHistory(string directory)
    {
        _path=Path.Combine(directory,"player-search.json");
        try { Entries=File.Exists(_path) ? Clean(JsonSerializer.Deserialize<List<PlayerIdentity>>(File.ReadAllText(_path)) ?? []) : []; }
        catch(JsonException) { Entries=[]; }
    }
    private static List<PlayerIdentity> Clean(IEnumerable<PlayerIdentity> entries) => entries.Where(p=>p!=null && p.IsValid && !string.IsNullOrWhiteSpace(p.Name)).DistinctBy(p=>p.Id).Take(20).ToList();
    public void Remember(PlayerIdentity player)
    {
        if(!player.IsValid || string.IsNullOrWhiteSpace(player.Name)) throw new ArgumentException("没有可保存的玩家资料。");
        var next=Clean(new[]{player}.Concat(Entries));
        File.WriteAllText(_path+".tmp",JsonSerializer.Serialize(next));
        for(var attempt=0;;attempt++)
        {
            try { File.Move(_path+".tmp",_path,true); break; }
            // Windows can briefly deny replacement immediately after the previous rename.
            catch(UnauthorizedAccessException) when(attempt<2) { Thread.Sleep(50*(attempt+1)); }
        }
        Entries=next;
    }
}
