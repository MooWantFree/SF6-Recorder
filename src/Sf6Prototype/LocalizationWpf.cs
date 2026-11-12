using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Sf6Prototype;

public static class LocalizationWpf
{
    public static void Apply(string? preference)
    {
        Localization.Apply(preference);
        var resources = Application.Current.Resources;
        foreach (System.Collections.DictionaryEntry entry in Localization.Resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!)
            resources["I18n." + entry.Key] = Localization.Get((string)entry.Key);
        // Keep regional formatting separate from UI translation, including WPF bindings.
        resources["UiLanguage"] = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.Name is { Length: > 0 } name ? name : "en-US");
        resources["UiFont"] = new System.Windows.Media.FontFamily(Localization.Culture.Name switch
        {
            "zh-Hans" => "Microsoft YaHei UI, Global User Interface",
            "zh-Hant" => "Microsoft JhengHei UI, Global User Interface",
            "ja" => "Yu Gothic UI, Global User Interface",
            _ => "Segoe UI, Global User Interface"
        });
    }
}
