using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sf6Prototype;

public record CharacterAsset(int Id, string Name, string ToolName, string ImagePath);
public record ProfileMetadata(string AccountId, string Name, string AvatarPath, DateTimeOffset UpdatedAt);
public interface IProfileSource
{
    Task<string> FetchPlayAsync(string owner, CancellationToken token);
    Task<string> FetchCardAsync(string owner, CancellationToken token);
}
public sealed class ProfileCache(string directory)
{
    public CharacterNames Names { get; } = new(directory);
    public string DirectoryPath => Path.Combine(directory, "Images");
    public List<CharacterAsset> Characters { get; private set; } = Read<List<CharacterAsset>>(Path.Combine(directory, "characters.json")) ?? [];
    private static T? Read<T>(string path) { try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; } catch (JsonException) { return default; } }
    public ProfileMetadata? Profile(string id) => Regex.IsMatch(id, @"^\d{10}$") ? Read<ProfileMetadata>(Path.Combine(directory, "profile-" + id + ".json")) : null;
    public void RememberIdentity(PlayerIdentity player)
    {
        if(!player.IsValid) throw new ArgumentException(Strings.InvalidPlayerUid);
        var previous=Profile(player.Id);
        WriteJson(Path.Combine(directory,"profile-"+player.Id+".json"),new ProfileMetadata(player.Id,player.Name,previous?.AvatarPath ?? "",DateTimeOffset.UtcNow));
    }
    public CurrentScores? Scores(string id) => Regex.IsMatch(id, @"^\d{10}$") ? Read<CurrentScores>(Path.Combine(directory, "scores-" + id + ".json")) : null;
    public string? CharacterImage(int id, string? name = null)
    {
        var path=Characters.FirstOrDefault(c=>c.Id==id || c.Name==name)?.ImagePath;
        if(path==null && Names.Portraits.FirstOrDefault(c=>c.Id==id).ToolName is { } tool)
            path=Path.Combine(DirectoryPath,"character_"+tool+".png");
        return path!=null && File.Exists(path) ? path : null;
    }
    public IReadOnlyList<ImageDownload> CharacterDownloads() => Characters.Select(c=>(c.Id,c.ToolName))
        .Concat(Names.Portraits).DistinctBy(c=>c.Id).Where(c=>Regex.IsMatch(c.ToolName,"^[a-z0-9_]+$"))
        .Select(c=>new ImageDownload($"/6/buckler/assets/images/material/character/character_{c.ToolName}_l.png",
            Path.Combine(DirectoryPath,"character_"+c.ToolName+".png")))
        .Where(image=>!File.Exists(image.Destination)).ToArray();
    public static IReadOnlyList<(int Id, string Name, string ToolName)> ParseCharacters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("props", out var props)) root = props;
        var rows = root.GetProperty("pageProps").GetProperty("play").GetProperty("character_league_infos");
        return rows.EnumerateArray().Select(c => (Id: c.GetProperty("character_id").GetInt32(), Name: MatchParser.Text(c,"character_name"), ToolName: MatchParser.Text(c,"character_tool_name")))
            .Where(c => c.Id > 0 && Regex.IsMatch(c.ToolName, "^[a-z0-9_]+$") && c.Name.Length > 0).ToList();
    }
    public async Task<IReadOnlyList<ImageDownload>> RefreshMetadataAsync(IProfileSource client, string owner, CancellationToken token, bool captureScores = false)
    {
        var characters=await RefreshPlayAsync(client,owner,token,captureScores);
        var avatar=await RefreshAvatarAsync(client,owner,token);
        return [..avatar,..characters];
    }
    public async Task<IReadOnlyList<ImageDownload>> RefreshPlayAsync(IProfileSource client, string owner, CancellationToken token, bool captureScores=false)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = await client.FetchPlayAsync(owner, token);
        token.ThrowIfCancellationRequested();
        var player = RankProfiles.ParseProfile(json, owner);
        var roster = ParseCharacters(json);
        // Capture scores only after reading the latest ranked head. Login, older-page
        // prefetch and unrelated-category loads must not settle an outdated newest match.
        if (captureScores) SaveScores(owner, json, DateTimeOffset.UtcNow);
        foreach (var c in roster)
        {
            var path = Path.Combine(DirectoryPath, "character_" + c.ToolName + ".png");
            var existing = Characters.FindIndex(x => x.Id == c.Id);
            var asset = new CharacterAsset(c.Id, c.Name, c.ToolName, path);
            if (existing < 0) Characters.Add(asset); else Characters[existing] = asset;
        }
        WriteJson(Path.Combine(directory,"characters.json"), Characters);
        // Name discovery is queued by the shared request observer. It must not extend this
        // foreground refresh or turn a name-service failure into a portrait/score failure.
        var previous=Profile(owner);
        if((previous==null || previous.Name==owner) && !string.IsNullOrWhiteSpace(player.Name))
            WriteJson(Path.Combine(directory,"profile-"+owner+".json"),new ProfileMetadata(owner,player.Name,previous?.AvatarPath ?? "",DateTimeOffset.UtcNow));
        return Characters.Where(c => !File.Exists(c.ImagePath)).Select(c =>
            new ImageDownload($"/6/buckler/assets/images/material/character/character_{c.ToolName}_l.png", c.ImagePath)).ToArray();
    }
    public async Task<IReadOnlyList<ImageDownload>> RefreshAvatarAsync(IProfileSource client,string owner,CancellationToken token)
    {
        var card = await client.FetchCardAsync(owner, token);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        using var doc = JsonDocument.Parse(card);
        var icon = MatchParser.Text(doc.RootElement,"icon_file_name");
        var name = MatchParser.Text(doc.RootElement,"fighter_name");
        var previous = Profile(owner);
        var avatar = previous?.AvatarPath ?? "";
        var images=new List<ImageDownload>();
        if (Regex.IsMatch(icon, "^[a-zA-Z0-9_-]+$"))
        {
            avatar = Path.Combine(DirectoryPath, "account_" + owner + ".jpg");
            // Revalidate on every user refresh, even if the icon filename remains unchanged.
            images.Add(new($"/6/buckler/assets/images/material/icon/{icon}.jpg", avatar, true));
        }
        if (string.IsNullOrWhiteSpace(name)) name = previous?.Name ?? owner;
        WriteJson(Path.Combine(directory,"profile-" + owner + ".json"), new ProfileMetadata(owner, name, avatar, DateTimeOffset.UtcNow));
        return images;
    }
    public void SaveScores(string owner, string playJson, DateTimeOffset fetchedAt)
    {
        try
        {
            var profile = RankProfiles.ParseProfile(playJson, owner);
            WriteJson(Path.Combine(directory, "scores-" + owner + ".json"), new CurrentScores(fetchedAt, profile.CurrentSeason, profile.Characters));
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidOperationException or JsonException) { }
    }
    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value));
        if(File.Exists(path)) File.Replace(path+".tmp",path,null);
        else File.Move(path+".tmp",path);
    }
}
