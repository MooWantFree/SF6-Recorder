using System.Text.Json;
using Sf6Prototype;

internal static class CharacterNameChecks
{
    private const string Owner="1234567890";
    private sealed class Source : ICharacterNameSource
    {
        public List<string> Calls {get;}=[];
        public string? FailLanguage {get;set;}
        public bool AddCharacter {get;set;}
        public bool Partial {get;set;}
        public async Task<string> FetchCharacterNamesAsync(string owner,string language,CancellationToken token)
        {
            Calls.Add(language);await Task.Yield();token.ThrowIfCancellationRequested();
            if(language==FailLanguage) throw new IOException("offline");
            return Page(language,AddCharacter,Partial);
        }
    }
    internal static string Page(string language,bool extra=false,bool partial=false)
    {
        var names=language switch
        {
            "en"=>new[]{"RYU","M. BISON","CHUN-LI"},
            "ja"=>new[]{"リュウ","ベガ","春麗"},
            "zh-Hant"=>new[]{"隆","維加","春麗"},
            _=>new[]{"隆","维加","春丽"}
        };
        var rows=new List<object> {new {character_id=1,character_name=names[0],character_tool_name="ryu"}};
        if(!partial) rows.Add(new {character_id=26,character_name=names[1],character_tool_name="vega"});
        if(extra) rows.Add(new {character_id=4,character_name=names[2],character_tool_name="chunli"});
        rows.Reverse();
        return JsonSerializer.Serialize(new {pageProps=new {play=new {character_league_infos=rows}}});
    }
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool ok,string message) {if(!ok) throw new Exception(message);count++;}
        string Root()=>Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        CharacterNames EmptyCache(string path)=>new(path,new CharacterNameCatalog(1,[]));
        var root=Root();var cache=EmptyCache(root);var source=new Source {FailLanguage="zh-Hant"};
        var roster=ProfileCache.ParseCharacters(Page("zh-Hans"));
        try
        {
            try {await cache.EnsureAsync(source,Owner,roster,CancellationToken.None);throw new Exception("Expected offline failure");} catch(IOException) { }
            Check(source.Calls.SequenceEqual(["en","zh-Hant"]),"First initialization reuses the existing Simplified roster and requests missing languages in sequence");
            cache=EmptyCache(root);Localization.Apply("en");
            Check(cache.Display(26,"维加")=="M. BISON" && cache.Display(1,"隆")=="RYU","Official names map by ID regardless of roster order");
            Localization.Apply("ja");
            Check(cache.Display(26,"维加")=="M. BISON","A missing translated name falls back to the cached English name");
            source.Calls.Clear();source.FailLanguage=null;
            await cache.EnsureAsync(source,Owner,roster,CancellationToken.None);
            Check(source.Calls.SequenceEqual(["zh-Hant","ja"]),"Restart resumes only unfinished languages");
            Check(cache.Display(26,"维加")=="ベガ" && cache.Display(null,"维加")=="ベガ","Language-specific character names and legacy opponents use official identity mappings");
            Check(cache.Display(999,"UNKNOWN")=="UNKNOWN" && cache.Display(999,"隆")=="隆","An unknown explicit ID never borrows another character's name");
            source.Calls.Clear();cache=EmptyCache(root);
            await cache.EnsureAsync(source,"9999999999",roster,CancellationToken.None);
            Check(source.Calls.Count==0,"Completed catalogs are reused across restarts and accounts without requests");
            Localization.Apply("zh-Hant");Check(cache.Display(26,"维加")=="維加","Traditional Chinese character names come from their own official roster");
            source.AddCharacter=true;var expanded=ProfileCache.ParseCharacters(Page("zh-Hans",true));
            await cache.EnsureAsync(source,Owner,expanded,CancellationToken.None);
            Check(source.Calls.SequenceEqual(["en","zh-Hant","ja"]) && cache.Display(4,"春丽")=="春麗","A new character triggers a single refill for each missing language");
            source.Calls.Clear();await cache.EnsureAsync(source,Owner,expanded,CancellationToken.None);
            Check(source.Calls.Count==0,"New-character refill is not repeated once all languages are complete");
            var incomplete=EmptyCache(Root());var partial=new Source {Partial=true};
            try {await incomplete.EnsureAsync(partial,Owner,roster,CancellationToken.None);throw new Exception("Expected incomplete roster failure");}catch(InvalidDataException) { }
            Check(!incomplete.Covers("en",roster) && incomplete.Covers("zh-Hans",roster),"Partial pages do not mark a language complete or discard successful languages");
            using var stop=new CancellationTokenSource();stop.Cancel();source.Calls.Clear();
            try {await EmptyCache(Root()).EnsureAsync(source,Owner,roster,stop.Token);throw new Exception("Expected cancellation");} catch(OperationCanceledException) { }
            Check(source.Calls.Count==0,"Cancellation prevents all roster requests");
            var broken=Root();Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"character-names.json"),"{broken");
            Check(EmptyCache(broken).Display(1,"隆")=="隆","Corrupt cache gracefully falls back to original data and can be rebuilt");
            Check(CharacterNames.PagePath(Owner,"en")==$"/6/buckler/profile/{Owner}/play","English roster uses the URL without a language segment");
            Check(CharacterNames.PagePath(Owner,"ja")==$"/6/buckler/ja-jp/profile/{Owner}/play" && CharacterNames.PagePath(Owner,"zh-Hant").Contains("/zh-hant/") && CharacterNames.PagePath(Owner,"zh-Hans").Contains("/zh-hans/"),"All other languages use the official locale paths");
            var json=File.ReadAllText(Path.Combine(root,"character-names.json"));
            Check(!json.Contains(Owner) && !json.Contains("Account") && !json.Contains("Cookie"),"The shared name cache contains only character metadata");
            var selectedRoot=Root();var selected=EmptyCache(selectedRoot);var selectedSource=new Source();
            Check(selected.NeedsLanguage("ja",roster),"Saving a changed language detects a missing local name cache");
            await selected.EnsureLanguageAsync(selectedSource,Owner,"ja",roster,CancellationToken.None);
            Check(selectedSource.Calls.SequenceEqual(["ja"]) && !selected.NeedsLanguage("ja",roster) && selected.NeedsLanguage("en",roster),"A language change requests only the selected language once");
            Localization.Apply("ja");
            Check(selected.Display(null,"维加")=="ベガ","Single-language loading also identifies legacy opponents by their saved raw name");
            selected=EmptyCache(selectedRoot);
            await selected.EnsureLanguageAsync(selectedSource,Owner,"ja",roster,CancellationToken.None);
            Check(selectedSource.Calls.Count==1,"A complete selected-language cache is reused after restart");
            selectedSource.AddCharacter=true;
            await Task.WhenAll(selected.EnsureLanguageAsync(selectedSource,Owner,"ja",expanded,CancellationToken.None),selected.EnsureLanguageAsync(selectedSource,Owner,"ja",expanded,CancellationToken.None));
            Check(selectedSource.Calls.Count==2 && selected.Covers("ja",expanded),"Concurrent requests for a missing character share one selected-language fetch");
            var empty=EmptyCache(Root());var firstSource=new Source();
            await empty.EnsureLanguageAsync(firstSource,Owner,"en",[],CancellationToken.None);
            Check(!empty.NeedsLanguage("en",[]) && firstSource.Calls.SequenceEqual(["en"]),"A first-use language request can initialize a catalog without a prior asset roster");
            firstSource.FailLanguage="ja";
            try {await empty.EnsureLanguageAsync(firstSource,Owner,"ja",[],CancellationToken.None);throw new Exception("Expected fetch failure");}catch(IOException) { }
            Check(empty.NeedsLanguage("ja",[]) && !empty.NeedsLanguage("en",[]),"Failed selected-language requests preserve completed languages and remain retryable");
            firstSource.FailLanguage=null;firstSource.Partial=true;
            try {await empty.EnsureLanguageAsync(firstSource,Owner,"ja",[],CancellationToken.None);throw new Exception("Expected partial roster rejection");}catch(InvalidDataException) { }
            Check(empty.NeedsLanguage("ja",[]),"Existing catalog identities validate a selected-language response even without asset metadata");
            firstSource.Calls.Clear();
            try {await empty.EnsureLanguageAsync(firstSource,Owner,"ja",roster,stop.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException) { }
            Check(firstSource.Calls.Count==0,"Cancelled language changes never start a character-name request");
            firstSource.Partial=false;firstSource.AddCharacter=true;
            await empty.EnsureLanguageAsync(firstSource,Owner,"ja",roster,CancellationToken.None);
            Check(empty.NeedsLanguage("en",roster),"A newly discovered character makes another language incomplete even when asset metadata is older");
            firstSource.AddCharacter=false;
            try {await empty.EnsureLanguageAsync(firstSource,Owner,"en",roster,CancellationToken.None);throw new Exception("Expected missing new character rejection");}catch(InvalidDataException) { }
            Check(empty.NeedsLanguage("en",roster),"A refill must cover newly discovered IDs as well as the older asset roster");
        }
        finally {Localization.Apply("zh-Hans");}
        return count;
    }
}
