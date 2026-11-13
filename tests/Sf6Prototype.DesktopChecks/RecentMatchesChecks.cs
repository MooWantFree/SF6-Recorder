using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sf6Prototype;

internal static class RecentMatchesChecks
{
    public static int Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var path=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(path,"recent.sqlite"));const string owner="1234567890";
        var at=App.StartedAt.ToUnixTimeSeconds();
        var first=new Battle {OwnerId=owner,ReplayId="first",Category="rank",CharacterId=1,Character="隆",PlayedAt=at+60,Lp=1000,MyRounds=2,OpponentRounds=0};
        var second=first with {ReplayId="second",PlayedAt=at+120,Lp=1050,MyRounds=0,OpponentRounds=2};
        store.SavePage([first,second]);store.SaveRankLinks(owner,[second,first],new());
        var archive=new Archive(store,new ProfileCache(path)) {SignedInId=owner};archive.Load(owner,false);
        var main=new MainWindow();main.ApplyTemplate();const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string name,object value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(main,value);
        void Update()=>typeof(MainWindow).GetMethod("UpdateRecentMatches",flags)!.Invoke(main,null);
        Set("_archive",archive);Set("_settings",new ConnectionSettings {RefreshAfterMatch=true});Set("_ready",true);
        var navigation=(PlayerNavigation)typeof(MainWindow).GetField("_navigation",flags)!.GetValue(main)!;navigation.SetHome(new(owner,"Me"));
        var recent=(RecentMatchesView)main.FindName("RecentMatches");var history=(HistoryView)main.FindName("History");
        var chart=(ScoreChart)recent.FindName("Chart");var root=(FrameworkElement)main.Content;
        void Layout(double width,double height)
        {
            root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
            main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);root.UpdateLayout();
        }
        Update();
        foreach(var size in new[]{new Size(1380,880),new Size(1120,720)})
        {
            Layout(size.Width,size.Height);
            Check(recent.Visibility==Visibility.Visible && chart.ActualHeight>=100 && history.ActualHeight>300,$"The compact plot and usable history coexist at {size}");
            Check(recent.TranslatePoint(new Point(),root).Y>=history.TranslatePoint(new Point(0,history.ActualHeight),root).Y,"The panel occupies a separate region below the history list");
        }
        Check(chart.Compact && chart.Points.Count==3 && chart.Points[0].Caption==Strings.SessionBaseline && chart.Points[1].Connect && !chart.Points[2].Settled,"The Scores chart is reused with a baseline, connected confirmed score and pending last point");
        Check(((TextBlock)recent.FindName("HighestScore")).Text=="1,050" && ((TextBlock)recent.FindName("LowestScore")).Text=="1,000" &&
            ((TextBlock)recent.FindName("ScoreChange")).Text=="+50*" && ((TextBlock)recent.FindName("ScoreWinRate")).Text=="50.0%","Visible interval metrics preserve unknown settlements and match outcomes");
        var before=chart.Points.Select(p=>p.ReplayId).ToArray();
        var collapse=(Button)recent.FindName("CollapseButton");var expandedHeight=history.ActualHeight;
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(1120,720);
        Check(recent.IsCollapsed && recent.ActualHeight==48 && chart.Visibility==Visibility.Collapsed && history.ActualHeight>expandedHeight+100,
            "The collapse button retains just the header and returns the plot's height to history");
        Update();Layout(1120,720);
        Check(recent.IsCollapsed && chart.Points.Select(p=>p.ReplayId).SequenceEqual(before),"Refreshes preserve both the collapsed state and the original session range");
        collapse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Layout(1120,720);
        Check(!recent.IsCollapsed && recent.ActualHeight==208 && chart.Visibility==Visibility.Visible && chart.Points.Select(p=>p.ReplayId).SequenceEqual(before),
            "Expanding restores the approved height and session data");
        Set("_settings",new ConnectionSettings {RefreshAfterMatch=false});Update();Layout(1120,720);
        Check(recent.Visibility==Visibility.Collapsed && history.ActualHeight>500,"Disabling match-end refresh returns the chart's space to the history list");
        Set("_settings",new ConnectionSettings {RefreshAfterMatch=true});Update();Layout(1120,720);
        Check(chart.Points.Select(p=>p.ReplayId).SequenceEqual(before),"Toggling detection does not reset the app session boundary");
        string? invoked=null;recent.SelectionChanged+=row=>invoked=row.Battle.ReplayId;
        typeof(ScoreChart).GetMethod("Invoke",flags)!.Invoke(chart,[1]);
        Check(invoked=="first","Selecting a chart point exposes that actual match for the detail panel");
        Set("_ready",false);((ListBox)main.FindName("PageTabs")).SelectedIndex=1;Set("_ready",true);Update();
        Check(recent.Visibility==Visibility.Collapsed && ((FrameworkElement)main.FindName("HistoryHost")).Visibility==Visibility.Collapsed,"The full Scores page does not overlap the recent-history panel");
        Set("_ready",false);((ListBox)main.FindName("PageTabs")).SelectedIndex=0;Set("_ready",true);navigation.Visit(new("9876543210","Other"));Update();
        Check(recent.Visibility==Visibility.Collapsed,"Browsing another player does not display the signed-in account's session under their records");
        Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero && App.DataDirectory=="","Recent matches checks never create a native window or use user data");
        return count;
    }
}
