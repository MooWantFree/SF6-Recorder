using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sf6Prototype;

internal static class PresentationLayoutChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        Check(BattleDateDisplay.Timestamp("2026-01-01 21:34:00",2026)=="01-01 21:34" &&
            BattleDateDisplay.Timestamp("2025-01-01 21:34:00",2026)=="2025-01-01 21:34","Compact dates distinguish the same month/day in different years");
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"layout.sqlite"));
        const string owner="1234567890",opponent="9999999999";
        var epoch=DateRanges.Epoch(new DateTime(BattleDateDisplay.CurrentYear,1,2))!.Value;
        var challenger=new Battle {OwnerId=owner,OpponentId=opponent,PlayerName="自己",Opponent="对手名字比较长",ReplayId="CHALLENGE",Category="custom",CharacterId=1,Character="桑吉尔夫",OpponentCharacter="春丽",Lp=-1,OpponentLp=23365,PlayedAt=epoch,MyRounds=0,OpponentRounds=2};
        var master=challenger with {ReplayId="MASTER",Category="rank",Lp=25000,Mr=1850,LeagueRank=36,PlayedAt=epoch-60};
        var diamond=challenger with {ReplayId="DIAMOND",Category="rank",CharacterId=2,Character="肯",Lp=23365,PlayedAt=epoch-120};
        var older=challenger with {ReplayId="OLDER",PlayedAt=DateRanges.Epoch(new DateTime(BattleDateDisplay.CurrentYear-1,1,2))!.Value};
        store.SavePage([challenger,master,diamond,older,challenger with {ReplayId="HUB",Category="hub",PlayedAt=epoch-180}]);
        for(var i=0;i<12;i++)store.AddTag(owner,master.ReplayId,$"{i:00}一个很长的标签用于检验省略号和数量");
        var archive=new Archive(store,new ProfileCache(root));archive.Load(owner);
        var history=new HistoryView {FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=13};history.Bind(archive);history.Reload();
        Layout(history,716,590);
        var rows=Descendants<HistoryTableGrid>(history).Where(g=>g.DataContext is MatchListRow).ToList();
        Check(rows.Count==5,"The minimum-width fixture realizes the tagged, master, unranked, battle hub and older matches");
        Check(rows.All(g=>Math.Abs(g.ActualHeight-56)<.1),"Many long tags and three score lines do not increase the row height");
        Check(rows.SelectMany(g=>g.Children.OfType<TextBlock>().Where(t=>Grid.GetColumn(t)==4)).All(t=>TextWidth(t)<=t.ActualWidth+.5),"Battle mode names fit without clipping at minimum width");
        var masterRow=archive.Row(master);
        Check(masterRow.FirstTag!=null && masterRow.MoreTags=="+11" && masterRow.TagsTip.Contains("11一个很长"),"Tag summaries retain the exact overflow count and full disclosure text");
        var masterGrid=rows.Single(g=>((MatchListRow)g.DataContext).Battle.ReplayId=="MASTER");
        var tagged=Descendants<Button>(masterGrid).Single(b=>b.Name=="TagCell");
        var more=Descendants<TextBlock>(tagged).Single(t=>t.Text=="+11");
        Check(more.ActualWidth>0 && more.TranslatePoint(new Point(more.ActualWidth,0),tagged).X<=tagged.ActualWidth,"The overflow count stays inside the tag cell with a maximum-length label");
        var rankText=Descendants<TextBlock>(masterGrid).Single(t=>t.Name=="RankText");
        Check(rankText.Text=="Ultimate Master" && TextWidth(rankText)<=rankText.ActualWidth+.5,"The longest master rank fits the narrower score column");
        var scoreTexts=Descendants<TextBlock>(masterGrid).Select(t=>t.Text).ToList();
        Check(scoreTexts.Contains("25,000 LP") && scoreTexts.Contains("1,850 MR") && masterRow.RankScoreTip.Contains("结算未知"),"Unknown settlements preserve both pre-match scores and their explanation");
        var settled=new MatchListRow(master,null,null,new(master.PlayedAt,master.Time,25000,1850,0,12,master.ReplayId));
        Check(settled.ListLp=="25,000 LP" && settled.MasterScore=="1,862 MR" && settled.MrChange=="+12 MR" && settled.LpChange=="","MR settlements remain on the MR line with the correct unit");
        var lpFallback=new MatchListRow(master,null,null,new(master.PlayedAt,master.Time,25000,1850,50,null,master.ReplayId));
        Check(lpFallback.LpChange=="+50 LP" && lpFallback.MrChange=="" && lpFallback.MasterScore=="1,850 MR","A known LP settlement with unknown MR never acquires the wrong change unit");
        var challengerGrid=rows.Single(g=>((MatchListRow)g.DataContext).Battle.ReplayId=="CHALLENGE");
        Check(Descendants<StackPanel>(challengerGrid).Where(p=>p.Name is "LpLine" or "MrLine").All(p=>p.Visibility==Visibility.Collapsed),"Empty score lines collapse together with their spacing");
        var unranked=Descendants<TextBlock>(challengerGrid).Single(t=>t.Name=="RankText");
        Check(Math.Abs(unranked.TranslatePoint(new Point(0,unranked.ActualHeight/2),challengerGrid).Y-challengerGrid.ActualHeight/2)<1,"Single-line New Challenger is vertically centered in its row");
        var search=(TextBox)history.FindName("SearchBox");var outcome=(ListBox)history.FindName("OutcomeFilter");
        Check(Math.Abs(search.TranslatePoint(new Point(),history).Y-outcome.TranslatePoint(new Point(),history).Y)<4,"The result filter stays beside the other filters at minimum width");
        var categories=(ListBox)history.FindName("CategoryFilter");
        Check(categories.Items.OfType<CategoryChoice>().Any(c=>c.Key=="casual" && c.Count==0),"Locally empty categories remain available for initial loading");
        Save(history,"history-minimum.png");
        var headers=(HistoryTableGrid)history.FindName("ColumnHeaders");
        var ownHeader=headers.Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==1);
        Check(TextWidth(ownHeader)<=ownHeader.ActualWidth+.5 && rows.All(g=>g.ColumnDefinitions[1].ActualWidth<g.ColumnDefinitions[3].ActualWidth),"The own-player column is narrower while its full header remains readable at minimum width");
        Check(rows.SelectMany(g=>Descendants<TextBlock>(g.Children.OfType<FrameworkElement>().Single(e=>Grid.GetColumn(e)==1))).All(t=>TextWidth(t)<=t.ActualWidth+.5),"Four-character role names still fit the narrower own-player column");
        var tagHeader=headers.Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==7);
        var modeHeader=headers.Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==4);
        foreach(var key in new[]{"","rank","casual","custom","hub","favorites"})
        {
            categories.SelectedItem=categories.Items.OfType<CategoryChoice>().Single(c=>c.Key==key);Layout(history,716,590);
            var mixed=key is "" or "favorites";
            var categoryRows=Descendants<HistoryTableGrid>(history).Where(g=>g.DataContext is MatchListRow).ToList();
            Check(tagHeader.Visibility==Visibility.Visible && TextWidth(tagHeader)<=tagHeader.ActualWidth+.5,$"The tag header remains visible and uncut in category '{key}', including empty lists");
            Check(modeHeader.Visibility==(mixed ? Visibility.Visible : Visibility.Collapsed) && Math.Abs(headers.ColumnDefinitions[4].ActualWidth-(mixed ? 56 : 0))<.1 &&
                categoryRows.All(g=>Math.Abs(g.ColumnDefinitions[4].ActualWidth-(mixed ? 56 : 0))<.1 && g.Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==4).Visibility==modeHeader.Visibility),$"Single categories reclaim the mode column while mixed categories retain it: '{key}'");
            Save(history,$"history-{(key=="" ? "all" : key)}.png");
        }
        categories.SelectedItem=categories.Items.OfType<CategoryChoice>().Single(c=>c.Key=="custom");Layout(history,716,590);
        var emptyTagCells=Descendants<Button>(history).Where(b=>b.Name=="TagCell").ToList();
        Check(!history.HasTags && history.TagColumnWidth.Value<60 && emptyTagCells.All(b=>b.IsEnabled && b.ActualWidth>0 && Descendants<Border>(b).Any(t=>t.Name=="AddTagHint" && t.Visibility==Visibility.Visible && t.ActualWidth>0)),"An untagged view keeps a visible, compact add-tag action");

        var detail=new MatchDetailPanel {CurrentPlayerId=owner,FontFamily=history.FontFamily,FontSize=13};detail.Bind(archive);detail.Show(archive.Row(older));Layout(detail,324,650);
        var actions=Descendants<Button>(detail).Where(b=>b.Name=="ViewHistoryButton").ToList();
        Check(actions.Count==2 && !actions.Single(b=>((PlayerIdentity)b.Tag).Id==owner).IsEnabled && actions.Single(b=>((PlayerIdentity)b.Tag).Id==opponent).IsEnabled,"Only the currently viewed player's history action is disabled");
        var requestedAction=actions.Single(b=>((PlayerIdentity)b.Tag).Id==opponent);var actionWidth=requestedAction.ActualWidth;
        detail.PendingPlayerId=opponent;Layout(detail,324,650);
        Check(!requestedAction.IsEnabled && requestedAction.Content is "加载中" && requestedAction.ContentTemplate!=null && actions.Single(b=>((PlayerIdentity)b.Tag).Id==owner).Content is "查看战绩","Only the requested player's button shows immediate loading feedback and blocks a second click");
        var loadingContent=Descendants<StackPanel>(requestedAction).Single(p=>p.Orientation==Orientation.Horizontal);
        Check(Math.Abs(requestedAction.ActualWidth-actionWidth)<.1 && loadingContent.ActualWidth<=requestedAction.ActualWidth-requestedAction.Padding.Left-requestedAction.Padding.Right,"The spinner and loading label fit the existing button without moving the adjacent rank action");
        Save(detail,"detail-loading.png");
        detail.PendingPlayerId="";Layout(detail,324,650);
        Check(requestedAction.IsEnabled && requestedAction.Content is "查看战绩" && requestedAction.ContentTemplate==null,"The history action recovers when loading ends");
        detail.IsFavoritesView=true;Layout(detail,324,650);
        Check(actions.All(b=>b.IsEnabled),"Both players can open full history from favorites, including the current player");
        detail.IsFavoritesView=false;detail.CurrentPlayerId=opponent;Layout(detail,324,650);
        Check(actions.Single(b=>((PlayerIdentity)b.Tag).Id==owner).IsEnabled && !actions.Single(b=>((PlayerIdentity)b.Tag).Id==opponent).IsEnabled,"The disabled action follows the viewed player rather than the signed-in account or left column");
        var dates=Descendants<TextBlock>(detail).Where(t=>t.Text.StartsWith($"{BattleDateDisplay.CurrentYear-1}-01-02")).ToList();
        Check(dates.Count>=2 && dates.All(t=>t.ActualWidth>0),"Old dates include their year in the detail header and encounter history");
        Save(detail,"detail-cross-year.png");

        var settings=new SettingsWindow(null!,new ConnectionSettings {RefreshReminderSeconds=600});
        var settingsContent=(FrameworkElement)settings.Content;
        foreach(var name in new[]{"ApiIntervalPresets","ReminderPresets","AutoRefreshPresets"})
        {
            var presets=(ListBox)settings.FindName(name);
            for(var i=0;i<presets.Items.Count;i++)
            {
                presets.SelectedIndex=i;Layout(settingsContent,settings.Width-16,680);
                var item=(ListBoxItem)presets.Items[i];var text=Descendants<TextBlock>(item).First();
                Check(TextWidth(text)<=text.ActualWidth+.5,$"Selected preset {item.Content} renders without clipping");
            }
        }
        ((ListBox)settings.FindName("ReminderPresets")).SelectedIndex=4;Layout(settingsContent,settings.Width-16,680);Save(settingsContent,"settings.png");
        var scores=new ScoresView {FontFamily=history.FontFamily,FontSize=13};scores.Bind(archive);scores.Reload();
        Layout(scores,716,650);Check(Grid.GetRow((UIElement)scores.FindName("ScoreFilters"))==1,"Narrow score views put filters below the statistics");
        Layout(scores,976,650);Check(Grid.GetRow((UIElement)scores.FindName("ScoreFilters"))==0,"Wide score views put filters beside the statistics");
        Save(scores,"scores-wide.png");
        Check(App.DataDirectory=="" && new System.Windows.Interop.WindowInteropHelper(settings).Handle==IntPtr.Zero,"Layout checks use only synthetic data and never open a native window");
        return count;
    }
    private static void Layout(FrameworkElement element,double width,double height)
    {
        element.Measure(new Size(width,height));element.Arrange(new Rect(0,0,width,height));element.UpdateLayout();
        element.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);element.UpdateLayout();
    }
    private static double TextWidth(TextBlock text)=>new FormattedText(text.Text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
        new Typeface(text.FontFamily,text.FontStyle,text.FontWeight,text.FontStretch),text.FontSize,Brushes.Black,1).WidthIncludingTrailingWhitespace;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T item)yield return item;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
    private static void Save(FrameworkElement element,string name)
    {
        if(Environment.GetEnvironmentVariable("SF6_LAYOUT_CAPTURES") is not {Length:>0} directory)return;
        Directory.CreateDirectory(directory);
        var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,name));encoder.Save(file);
    }
}
