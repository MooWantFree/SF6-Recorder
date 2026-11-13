using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Sf6Prototype;
using Localization = Sf6Prototype.Localization;

internal static class LocalizationChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool ok,string message) {if(!ok) throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"language.sqlite"));
        const string owner="1234567890";
        var battle=new Battle {OwnerId=owner,OpponentId="9999999999",Opponent="Player",PlayerName="Player",ReplayId="LOCALIZED",Category="rank",CharacterId=1,Character="RYU",OpponentCharacter="CHUN-LI",Lp=25000,Mr=1500,MyRounds=2,OpponentRounds=0,PlayedAt=DateRanges.Epoch(new DateTime(2026,1,2))!.Value};
        store.SavePage([battle,battle with {ReplayId="LOSS",MyRounds=0,OpponentRounds=2,PlayedAt=battle.PlayedAt-60}]);
        store.AddTag(owner,battle.ReplayId,"我的タグ");
        File.WriteAllText(Path.Combine(root,"character-names.json"),System.Text.Json.JsonSerializer.Serialize(new CharacterNameCatalog(1,
            [new(1,"ryu",new(){["en"]="RYU",["zh-Hans"]="隆",["zh-Hant"]="隆",["ja"]="リュウ"})])));
        var archive=new Archive(store,new ProfileCache(root));archive.Load(owner);
        var history=new HistoryView();history.Bind(archive);
        history.Reload(restore:new(){Outcome="胜",Character="RYU",SelectedReplay=battle.ReplayId});
        var settings=new SettingsWindow(null!,new ConnectionSettings {Language="system"});
        var language=(ComboBox)settings.FindName("LanguageBox");
        var save=(Button)settings.FindName("SaveButton");
        var scores=new ScoresView();scores.Bind(archive);scores.Reload();var requested=0;scores.DataRequested+=()=>requested++;
        Check(language.Items.OfType<ComboBoxItem>().Select(i=>i.Tag?.ToString()).SequenceEqual(["system","en","zh-Hans","zh-Hant","ja"]),"Language choices use stable persisted identifiers in the requested order");
        Check(language.Items.OfType<ComboBoxItem>().Skip(1).Select(i=>i.Content?.ToString()).SequenceEqual(["English","简体中文","繁體中文","日本語"]),"Language names stay recognizable in every UI language");
        try
        {
            foreach(var (culture,title,button,result) in new[]{("en","Settings","Save","Win"),("zh-Hans","设置","保存","胜"),("zh-Hant","設定","儲存","勝"),("ja","設定","保存","勝ち")})
            {
                LocalizationWpf.Apply(culture);
                settings.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
                history.RefreshLanguage();
                scores.RefreshLanguage();
                Check(settings.Title==title && save.Content?.ToString()==button,"Existing XAML windows update after changing language");
                Check(AutomationProperties.GetName(language)==Strings.LanguageLabel && Strings.LanguageLabel.EndsWith("(Language)"),"The language selector has a localized accessible name and permanent Language hint");
                Check(history.CaptureState() is {Outcome:"胜",Character:"RYU",SelectedReplay:"LOCALIZED"} && history.Filtered.Count==1,"Switching languages preserves the active outcome, character and replay");
                Check(history.Selected?.Result==result && history.Selected.Battle.Character=="RYU" && history.Selected.Tags.Single().Name=="我的タグ","Only presentation labels change; character and custom tag names stay intact");
                var character=culture switch {"ja"=>"リュウ","zh-Hans" or "zh-Hant"=>"隆",_=>"RYU"};
                Check(history.Selected!.Character==character && ((HistoryView.CharacterChoice)((ComboBox)history.FindName("CharacterFilter")).SelectedItem).Name==character,"Rows and character filters share the official ID-based name mapping");
                Check(((CharacterCard)((ListBox)scores.FindName("CharacterList")).SelectedItem).Name==character && requested==0,"Score cards localize without any data request");
                language.SelectedValue=culture;
                var draft=(ConnectionSettings)typeof(SettingsWindow).GetMethod("Draft",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(settings,[true])!;
                Check(draft.Language==culture,"Settings draft stores the language code, not its translated label");
                var content=(FrameworkElement)settings.Content;
                content.Measure(new Size(settings.Width-16,double.PositiveInfinity));content.Arrange(new Rect(content.DesiredSize));content.UpdateLayout();
                // Content includes the custom title bar; reserve only the outer frame.
                var windowHeight=content.DesiredSize.Height+2*SystemParameters.ResizeFrameHorizontalBorderHeight;
                Check(windowHeight<=720,$"The localized settings window fits the minimum height in {culture} ({windowHeight:F2} DIP)");
                foreach(var text in Descendants<TextBlock>(content).Where(t=>t.Visibility==Visibility.Visible && t.TextWrapping==TextWrapping.NoWrap && t.Text.Length>0 && t.ActualWidth>0))
                {
                    var width=new FormattedText(text.Text,Localization.Culture,FlowDirection.LeftToRight,new Typeface(text.FontFamily,text.FontStyle,text.FontWeight,text.FontStretch),text.FontSize,Brushes.Black,1).Width;
                    Check(width<=text.ActualWidth+1,$"Settings label fits in {culture}: {text.Text}");
                }
            }
        }
        finally {LocalizationWpf.Apply("zh");}
        return count;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T item) yield return item;
            foreach(var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
