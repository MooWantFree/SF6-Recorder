using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sf6Prototype;

internal static class LocalizationChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool condition,string description) {if(!condition) throw new Exception(description);count++;}
        foreach(var (system,expected) in new[] {("en-US","en"),("en-GB","en"),("zh-CN","zh-Hans"),("zh-SG","zh-Hans"),("zh-Hans","zh-Hans"),("zh-TW","zh-Hant"),("zh-HK","zh-Hant"),("zh-MO","zh-Hant"),("zh-Hant","zh-Hant"),("ja-JP","ja"),("fr-FR","en"),("de-DE","en")})
            Check(Localization.Resolve("system",CultureInfo.GetCultureInfo(system)).Name==expected,$"System language {system} resolves to {expected}");
        Check(Localization.Resolve("en",CultureInfo.GetCultureInfo("ja-JP")).Name=="en","Manual language overrides system language");
        Check(Localization.Resolve("invalid",CultureInfo.GetCultureInfo("ja-JP")).Name=="ja","Unknown settings safely revert to system default");
        Check(Localization.Resolve("system",CultureInfo.InvariantCulture).Name=="en","Invariant or unsupported UI languages fall back to English");
        var region=CultureInfo.CurrentCulture;
        var resourceRoot=Path.Combine(Environment.CurrentDirectory,"src","Sf6Prototype","Resources");
        Dictionary<string,string> Read(string suffix)=>XDocument.Load(Path.Combine(resourceRoot,$"Strings{suffix}.resx")).Root!.Elements("data").ToDictionary(e=>e.Attribute("name")!.Value,e=>e.Element("value")!.Value);
        var neutral=Read("");
        foreach(var suffix in new[]{".zh-Hans",".zh-Hant",".ja"})
        {
            var translated=Read(suffix);
            Check(neutral.Keys.ToHashSet().SetEquals(translated.Keys),$"{suffix} has exactly the same keys as the English catalog");
            foreach(var (key,value) in neutral)
            {
                var other=translated[key];
                var format=CompositeFormat.Parse(value);var translatedFormat=CompositeFormat.Parse(other);
                Check(!string.IsNullOrWhiteSpace(other),$"{key}{suffix} is translated");
                Check(format.MinimumArgumentCount==translatedFormat.MinimumArgumentCount,$"{key}{suffix} preserves argument count");
                static string Slots(string text)=>string.Join("|",Regex.Matches(text,@"\{\d+(?:,-?\d+)?(?::[^}]+)?\}").Select(m=>m.Value).Order());
                Check(Slots(value)==Slots(other),$"{key}{suffix} preserves all format placeholders");
            }
        }
        var battle=new Battle {MyRounds=2,OpponentRounds=0,Character="RYU",Category="rank"};
        try
        {
            foreach(var (language,save,outcome,category) in new[]{("en","Save","Win","Ranked Match"),("zh-Hans","保存","胜","排位赛"),("zh-Hant","儲存","勝","排位賽"),("ja","保存","勝ち","ランクマッチ")})
            {
                Localization.Apply(language);
                Check(Strings.Save==save && Localization.Outcome(battle.Outcome)==outcome,$"{language} resolves compiled localized resources");
                Check(battle.Mode==category,$"Category names update after switching to {language}");
                Check(!System.Text.Json.JsonSerializer.Serialize(battle).Contains("\"Mode\""),"Translated display labels never enter persisted battle JSON");
                Check(battle.Outcome=="胜" && battle.Character=="RYU" && new CharacterRank(1,"RYU",25000,1500,36,null).RankName=="Master","Culture never changes stable outcomes, characters or ranks");
                Check(CultureInfo.CurrentCulture==region,"Changing UI language preserves regional numeric formats");
            }
            Check(Localization.Resources.GetString(nameof(Strings.Save),CultureInfo.GetCultureInfo("fr-FR"))=="Save","ResourceManager falls back to neutral English for missing translations");
            var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Check(ConnectionSettings.Load(root).Language=="system","New installs follow the system language");
            File.WriteAllText(Path.Combine(root,"settings.json"),"{\"UseProxy\":false}");
            Check(ConnectionSettings.Load(root).Language=="system","Existing settings without a language migrate without changing other settings");
            foreach(var language in new[]{"system","en","zh-Hans","zh-Hant","ja"})
            {
                new ConnectionSettings {Language=language,PlayerId="1234567890",ApiIntervalSeconds=3}.Save(root);
                var saved=ConnectionSettings.Load(root);
                Check(saved.Language==language && saved.PlayerId=="1234567890" && saved.ApiIntervalSeconds==3,"Language preferences persist alongside existing settings");
            }
            File.WriteAllText(Path.Combine(root,"settings.json"),"{\"Language\":\"zh\"}");
            Check(ConnectionSettings.Load(root).Language=="zh-Hans","Legacy Chinese preference migrates to Simplified Chinese");
            File.WriteAllText(Path.Combine(root,"settings.json"),"{\"Language\":\"unknown\"}");
            Check(ConnectionSettings.Load(root).Language=="system","Unsupported saved language recovers to system default");
        }
        finally {Localization.Apply("zh");}
        return count;
    }
}
