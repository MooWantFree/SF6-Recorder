using System.Globalization;
using System.Resources;

namespace Sf6Prototype;

/// <summary>UI language is independent of persisted values and the user's regional number formats.</summary>
public static class Localization
{
    public const string SystemLanguage = "system";
    public static readonly ResourceManager Resources = new("Sf6Prototype.Resources.Strings", typeof(Localization).Assembly);
    // Capture Windows' UI language before applying an app override; never use the overridden UI culture to resolve "system".
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    public static bool IsSupported(string? language) => language is SystemLanguage or "en" or "zh-Hans" or "zh-Hant" or "ja";
    public static string Normalize(string? language) => language=="zh" ? "zh-Hans" : IsSupported(language) ? language! : SystemLanguage;
    public static CultureInfo Resolve(string? preference, CultureInfo systemCulture)
    {
        var language = Normalize(preference);
        if (language == SystemLanguage)
        {
            language=systemCulture.TwoLetterISOLanguageName;
            if(language=="zh")
            {
                var traditional=false;
                for(var current=systemCulture;current!=CultureInfo.InvariantCulture;current=current.Parent)
                    if(current.Name.Equals("zh-Hant",StringComparison.OrdinalIgnoreCase) || current.Name is "zh-TW" or "zh-HK" or "zh-MO") traditional=true;
                language=traditional ? "zh-Hant" : "zh-Hans";
            }
        }
        return CultureInfo.GetCultureInfo(language is "zh-Hans" or "zh-Hant" or "ja" ? language : "en");
    }
    public static void Apply(string? preference)
    {
        Culture = Resolve(preference, SystemCulture);
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }
    public static string Get(string key) => Resources.GetString(key, Culture)
        ?? throw new MissingManifestResourceException($"Missing localization resource: {key}");
    public static string Format(string format, params object?[] values) => string.Format(CultureInfo.CurrentCulture, format, values);
    // These legacy outcome values are stored in the archive. Translate only their presentation.
    public static string Outcome(string value) => value switch
    {
        "胜" => Strings.OutcomeWin, "负" => Strings.OutcomeLoss,
        "平" => Strings.OutcomeDraw, "待确认" => Strings.OutcomePending, _ => value
    };
}
