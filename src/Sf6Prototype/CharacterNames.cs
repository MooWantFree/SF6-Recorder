using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sf6Prototype;

public interface ICharacterNameSource
{
    Task<string> FetchCharacterNamesAsync(string owner,string language,CancellationToken token);
}

public sealed record CharacterTranslation(int Id,string ToolName,Dictionary<string,string> Names);
public sealed record CharacterNameCatalog(int Version,List<CharacterTranslation> Characters);
public sealed record CharacterObservation(IReadOnlyList<int> Ids,IReadOnlyList<(int Id,string Name,string ToolName)>? Roster=null)
{
    public static CharacterObservation Battles(IEnumerable<Battle> battles)=>new(battles
        .SelectMany(b=>new[]{b.CharacterId,b.OpponentCharacterId ?? 0}).Where(id=>id>0).Distinct().ToArray());
    public static CharacterObservation Profile(string json)
    {
        var roster=ProfileCache.ParseCharacters(json);
        return new(roster.Select(c=>c.Id).ToArray(),roster);
    }
    public static CharacterObservation Ranks(string json)=>new(RankProfiles.ParseSeason(json).Select(c=>c.CharacterId).Distinct().ToArray());
}

/// <summary>Official names keyed by character ID, independent of account, UI language and stored battle names.</summary>
public sealed class CharacterNames
{
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[]{"en","zh-Hans","zh-Hant","ja"});
    private readonly string _path;
    private readonly SemaphoreSlim _gate=new(1,1);
    private Dictionary<int,CharacterTranslation> _characters=[];
    public int Revision {get;private set;}
    public IEnumerable<(int Id,string ToolName)> Portraits => _characters.Values.Select(c=>(c.Id,c.ToolName));
    public bool IsComplete(int id)=>_characters.TryGetValue(id,out var character) && Languages.All(character.Names.ContainsKey);
    public CharacterNames(string directory) : this(directory,ReadBundledCatalog()) { }
    internal CharacterNames(string directory,CharacterNameCatalog bundled)
    {
        _path=Path.Combine(directory,"character-names.json");
        ValidateCatalog(bundled);
        _characters=bundled.Characters.ToDictionary(c=>c.Id,c=>c with {Names=new(c.Names)});
        try
        {
            if(!File.Exists(_path)) return;
            var catalog=JsonSerializer.Deserialize<CharacterNameCatalog>(File.ReadAllText(_path));
            ValidateCatalog(catalog);
            foreach(var saved in catalog!.Characters)
            {
                var names=_characters.TryGetValue(saved.Id,out var shipped) && shipped.ToolName==saved.ToolName
                    ? new Dictionary<string,string>(shipped.Names) : [];
                foreach(var pair in saved.Names) names[pair.Key]=pair.Value;
                _characters[saved.Id]=saved with {Names=names};
            }
        }
        catch(Exception e) when(e is JsonException or IOException or InvalidDataException or ArgumentException)
        { /* Keep the bundled names when the writable cache is absent or corrupt. */ }
    }
    private static CharacterNameCatalog ReadBundledCatalog()
    {
        using var stream=typeof(CharacterNames).Assembly.GetManifestResourceStream("Sf6Prototype.Resources.CharacterNames.json")
            ?? throw new InvalidDataException("The bundled character catalog is missing.");
        var catalog=JsonSerializer.Deserialize<CharacterNameCatalog>(stream);
        ValidateCatalog(catalog);
        return catalog!;
    }
    private static void ValidateCatalog(CharacterNameCatalog? catalog)
    {
        if(catalog is not {Version:1,Characters:not null} ||
            catalog.Characters.Any(c=>c is null || c.Id<=0 || string.IsNullOrWhiteSpace(c.ToolName) || c.Names==null ||
                c.Names.Any(n=>!Languages.Contains(n.Key) || string.IsNullOrWhiteSpace(n.Value))) ||
            catalog.Characters.Select(c=>c.Id).Distinct().Count()!=catalog.Characters.Count)
            throw new InvalidDataException(Strings.CharacterNamesIncomplete);
    }
    public string Display(int? id,string original)
    {
        var character=id is >0 ? _characters.GetValueOrDefault(id.Value) : null;
        // Legacy opponent records can lack an ID; match only an unambiguous saved official name.
        if(id is not >0)
        {
            var matches=_characters.Values.Where(c=>c.Names.Values.Contains(original,StringComparer.Ordinal)).Take(2).ToArray();
            if(matches.Length==1) character=matches[0];
        }
        return character?.Names.GetValueOrDefault(Localization.Culture.Name)
            ?? character?.Names.GetValueOrDefault("en") ?? original;
    }
    public bool Covers(string language,IReadOnlyList<(int Id,string Name,string ToolName)> roster) =>
        roster.Count>0 && roster.All(c=>_characters.TryGetValue(c.Id,out var saved) && saved.ToolName==c.ToolName && saved.Names.ContainsKey(language));
    public int PendingRequests(IReadOnlyList<(int Id,string Name,string ToolName)> roster) => Languages.Count(l=>l!="zh-Hans" && !Covers(l,roster));

    public async Task EnsureObservedAsync(ICharacterNameSource source,string owner,CharacterObservation observation,CancellationToken token)
    {
        if(observation.Ids.All(IsComplete)) return;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if(observation.Ids.All(IsComplete)) return;
            var roster=observation.Roster;
            if(roster==null || observation.Ids.Where(id=>!IsComplete(id)).Any(id=>!roster.Any(c=>c.Id==id)))
                roster=ProfileCache.ParseCharacters(await source.FetchCharacterNamesAsync(owner,"zh-Hans",token));
            token.ThrowIfCancellationRequested();
            ValidateRoster(roster,[]);
            await EnsureRosterAsync(source,owner,roster,token);
        }
        finally {_gate.Release();}
    }

    public bool NeedsLanguage(string language,IReadOnlyList<(int Id,string Name,string ToolName)> roster)
    {
        if(!Languages.Contains(language)) throw new ArgumentException(Strings.InvalidLanguage);
        return _characters.Count==0 || (roster.Count>0 && !Covers(language,roster)) ||
            _characters.Values.Any(c=>!c.Names.ContainsKey(language));
    }

    public async Task EnsureLanguageAsync(ICharacterNameSource source,string owner,string language,
        IReadOnlyList<(int Id,string Name,string ToolName)> roster,CancellationToken token)
    {
        if(!NeedsLanguage(language,roster)) return;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            if(!NeedsLanguage(language,roster)) return;
            var json=await source.FetchCharacterNamesAsync(owner,language,token);
            token.ThrowIfCancellationRequested();
            var translated=ProfileCache.ParseCharacters(json);
            var expected=roster.Concat(_characters.Values.Where(c=>!roster.Any(r=>r.Id==c.Id))
                .Select(c=>(c.Id,Name:"",c.ToolName))).ToList();
            ValidateRoster(translated,expected);
            // The existing asset roster uses the original Simplified Chinese names, also needed
            // to identify older opponent records that did not include character IDs.
            if(language!="zh-Hans" && roster.Count>0) Merge("zh-Hans",roster);
            Merge(language,translated);
        }
        finally {_gate.Release();}
    }

    private static void ValidateRoster(IReadOnlyList<(int Id,string Name,string ToolName)> translated,
        IReadOnlyList<(int Id,string Name,string ToolName)> expected)
    {
        if(translated.Count==0 || translated.Select(c=>c.Id).Distinct().Count()!=translated.Count ||
            expected.Select(c=>c.Id).Distinct().Count()!=expected.Count ||
            expected.Any(c=>!translated.Any(t=>t.Id==c.Id && t.ToolName==c.ToolName)))
            throw new InvalidDataException(Strings.CharacterNamesIncomplete);
    }

    public async Task EnsureAsync(ICharacterNameSource source,string owner,IReadOnlyList<(int Id,string Name,string ToolName)> roster,CancellationToken token)
    {
        if(roster.Count==0) return;
        if(roster.Select(c=>c.Id).Distinct().Count()!=roster.Count) throw new InvalidDataException(Strings.CharacterNamesIncomplete);
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await EnsureRosterAsync(source,owner,roster,token);
        }
        finally {_gate.Release();}
    }
    private async Task EnsureRosterAsync(ICharacterNameSource source,string owner,IReadOnlyList<(int Id,string Name,string ToolName)> roster,CancellationToken token)
    {
        Merge("zh-Hans",roster);
        foreach(var language in Languages.Where(l=>l!="zh-Hans"))
        {
            if(Covers(language,roster)) continue;
            var json=await source.FetchCharacterNamesAsync(owner,language,token);
            token.ThrowIfCancellationRequested();
            var translated=ProfileCache.ParseCharacters(json);
            ValidateRoster(translated,roster);
            Merge(language,translated);
        }
    }
    private void Merge(string language,IReadOnlyList<(int Id,string Name,string ToolName)> roster)
    {
        var updated=_characters.ToDictionary(p=>p.Key,p=>p.Value with {Names=new(p.Value.Names)});
        var changed=false;
        foreach(var entry in roster)
        {
            if(!updated.TryGetValue(entry.Id,out var saved) || saved.ToolName!=entry.ToolName)
                updated[entry.Id]=saved=new(entry.Id,entry.ToolName,[]);
            if(saved.Names.GetValueOrDefault(language)==entry.Name) continue;
            saved.Names[language]=entry.Name;changed=true;
        }
        if(!changed) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Commit each completed language separately so an interrupted first run can resume.
        File.WriteAllText(_path+".tmp",JsonSerializer.Serialize(new CharacterNameCatalog(1,updated.Values.OrderBy(c=>c.Id).ToList()),
            new JsonSerializerOptions {WriteIndented=true,Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        if(File.Exists(_path)) File.Replace(_path+".tmp",_path,null);
        else File.Move(_path+".tmp",_path);
        _characters=updated;
        Revision++;
    }
    public static string PagePath(string owner,string language)
    {
        if(owner.Length!=10 || !owner.All(char.IsAsciiDigit)) throw new ArgumentException(Strings.InvalidPlayerUid);
        var prefix=language switch {"en"=>"", "zh-Hans"=>"zh-hans/", "zh-Hant"=>"zh-hant/", "ja"=>"ja-jp/", _=>throw new ArgumentException(Strings.InvalidLanguage)};
        return $"/6/buckler/{prefix}profile/{owner}/play";
    }
}
