using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        var empty=new RecentMatchesView();
        empty.Reload(archive,DateTimeOffset.FromUnixTimeSeconds(at+3600));
        Check(empty.IsEmpty && empty.Height==RecentMatchesView.HeaderHeight && ((ScoreChart)empty.FindName("Chart")).Visibility==Visibility.Collapsed &&
            ((Button)empty.FindName("CollapseButton")).Visibility==Visibility.Collapsed && ((TextBlock)empty.FindName("EmptyText")).Visibility==Visibility.Visible,
            "Without a ranked match this session the panel keeps only its header and empty message");
        empty.Reload(archive,App.StartedAt);
        Check(!empty.IsEmpty && empty.Height==RecentMatchesView.ExpandedHeight && ((ScoreChart)empty.FindName("Chart")).Visibility==Visibility.Visible,
            "The first ranked match of the session expands the panel to its full height");
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
        File.WriteAllText(Path.Combine(path,"scores-"+owner+".json"),System.Text.Json.JsonSerializer.Serialize(
            new CurrentScores(DateTimeOffset.FromUnixTimeSeconds(at+121),1,[new(1,"RYU",1010,0,24,null)])));
        archive.Load(owner,false);Update();
        Check(chart.Points[^1] is {ReplayId:"second",Value:1010,Settled:true,Connect:true} && chart.Points[1].Connect,
            "Publishing a current score immediately connects the pending last point and retains the confirmed earlier segment");
        var settledChange = ((TextBlock)recent.FindName("ScoreChange")).Text;
        Set("_ready",false);((ListBox)main.FindName("PageTabs")).SelectedIndex=1;Set("_ready",true);Update();
        Check(recent.Visibility==Visibility.Collapsed && ((FrameworkElement)main.FindName("HistoryHost")).Visibility==Visibility.Collapsed,"The full Scores page does not overlap the recent-history panel");
        Set("_ready",false);((ListBox)main.FindName("PageTabs")).SelectedIndex=0;Set("_ready",true);navigation.Visit(new("9876543210","Other"));Update();
        Check(recent.Visibility==Visibility.Collapsed,"Browsing another player does not display the signed-in account's session under their records");
        var preferences = new ConnectionSettings();
        var stream = new RecentMatchesStreamWindow(owner, preferences);
        Set("_streamWindow", stream);
        recent.DisplayChanged += stream.Display;
        // The main archive now belongs to the other player. Only the signed-in account may go on air.
        archive.Load("9876543210", false);
        Update();
        var card = (OverlayMatchCard)stream.FindName("Card");
        var plot = (ScoreChart)card.FindName("Plot");
        Check(plot.Points[^1] is { ReplayId: "second", Value: 1010 } && stream.DataContext is RecentMatchDisplay { Unit: "LP" } display && display.Change == settledChange,
            "A hidden recent panel continues publishing the signed-in player's settled session on another player's page");
        var metric = (ListBox)recent.FindName("ScoreMetric");
        metric.SelectedIndex = 1;
        Check(plot.Unit == "MR" && plot.Points.Count == 0, "Switching the score metric updates the live display immediately");
        metric.SelectedIndex = 0;
        var panel = (FrameworkElement)stream.Content;
        var overlaySize = new Size(560,232);
        panel.Measure(overlaySize);
        panel.Arrange(new Rect(overlaySize));
        panel.UpdateLayout();
        stream.Dispatcher.Invoke(()=>{}, DispatcherPriority.ContextIdle);
        var bitmap = new RenderTargetBitmap((int)overlaySize.Width,(int)overlaySize.Height,96,96,PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(10,10,1,1),pixel,4,0);
        Check(pixel[3] is >=190 and <=192 && plot.ActualHeight >=100 && stream.Opacity == 1,
            "Only the stream background is translucent and the compact plot has enough height to render");
        if(Environment.GetEnvironmentVariable("SF6_LAYOUT_CAPTURES") is {Length:>0} captures)
        {
            Directory.CreateDirectory(captures);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(captures,"recent-stream.png")); png.Save(file);
        }
        var custom = preferences with {OverlayBackgroundOpacity = 37};
        Check(!stream.Topmost, "The overlay starts unpinned");
        var alwaysPinned = custom with {OverlayAlwaysOnTop = true};
        stream.ApplyBackground(alwaysPinned);
        var pin = (System.Windows.Controls.Primitives.ToggleButton)stream.FindName("PinButton");
        Check(stream.Topmost && pin.IsChecked == true, "The saved pin preference applies when no temporary override exists");
        pin.IsChecked = false;
        stream.ApplyBackground(alwaysPinned with {OverlayBackgroundOpacity = 42});
        Check(!stream.Topmost && pin.IsChecked == false, "Changing another preference preserves a manual unpin");
        var reopened = new RecentMatchesStreamWindow(owner, alwaysPinned);
        Check(reopened.Topmost, "Reopening restores the saved pin preference");
        reopened.Close();
        panel.Measure(new Size(stream.MinWidth,stream.MinHeight));
        panel.Arrange(new Rect(0,0,stream.MinWidth,stream.MinHeight));
        panel.UpdateLayout();
        Check(plot.ActualHeight >= 100 && plot.ActualWidth >= 150, "The plot still renders at the minimum overlay size");
        var menu = new OverlayContextMenu();
        menu.SetAppearance(custom with {OverlayGreenScreen = true});
        var picker = (OverlayOpacityPicker)menu.FindName("Picker");
        var opacitySlider = (Slider)picker.FindName("OpacitySlider");
        opacitySlider.ApplyTemplate();
        var marks = (System.Windows.Controls.Primitives.TickBar)opacitySlider.Template.FindName("OpacityMarks",opacitySlider);
        Check(marks.Ticks.SequenceEqual(new double[] {0,25,50,75,100}) && opacitySlider.TickFrequency == 1,
            "The five visual marks leave all integer opacity values selectable");
        Check(picker.Value == 37 && ((TextBlock)picker.FindName("GreenHint")).Visibility == Visibility.Visible,
            "Opening the quick controls with green screen on shows the saved opacity and explains when it applies");
        menu.SetAppearance(custom);
        Check(picker.Value == 37 && ((TextBlock)picker.FindName("GreenHint")).Visibility == Visibility.Collapsed,
            "The context menu restores the same custom opacity after green screen is disabled");
        Check(picker.FindName("CaptureHelp") == null, "The quick controls omit OBS troubleshooting");
        stream.ApplyBackground(custom);
        Check(((Border)card.FindName("Backdrop")).Opacity == 0.37,"Custom opacity only adjusts the backdrop");
        var keyed = new RecentMatchesStreamWindow(owner, custom with {OverlayGreenScreen = true});
        var backdrop = (Border)((OverlayMatchCard)keyed.FindName("Card")).FindName("Backdrop");
        Check(!keyed.AllowsTransparency && backdrop.Opacity == 1 && ((SolidColorBrush)backdrop.Background).Color == Colors.Lime,
            "Green-screen capture uses a non-layered window and a solid key color");
        keyed.Close();
        var settingsWindow = new SettingsWindow(null!, custom);
        settingsWindow.ShowCategory((FrameworkElement)settingsWindow.FindName("OverlayPage"));
        var sampleCard = (OverlayMatchCard)settingsWindow.FindName("SampleCard");
        var samplePlot = (ScoreChart)sampleCard.FindName("Plot");
        Check(samplePlot.Points.Count == 6 && samplePlot.Points.Any(p=>p.Kind=="loss") && samplePlot.Points.Any(p=>p.Kind=="win"),
            "The preview shows fixed sample results without opening an overlay");
        bool HasInteriorExtremes() => DateRanges.Extrema(samplePlot.Points.Select(p=>p.Value).ToArray())
            .All(i=>i>0 && i<samplePlot.Points.Count-1);
        Check(HasInteriorExtremes(),"The example distinguishes session extrema from the opening and latest scores");
        var limitInput = (TextBox)settingsWindow.FindName("OverlayLimit");
        var limitError = (TextBlock)settingsWindow.FindName("MatchLimitError");
        Check(limitInput.Text=="5" && limitError.Visibility==Visibility.Collapsed,
            "The recent match count defaults to five");
        foreach (var invalid in new[] { "", "1", "51", "abc", "5.5" })
        {
            limitInput.Text = invalid;
            Check(limitError.Visibility==Visibility.Visible && samplePlot.Points.Count==6,
                "Invalid input immediately shows the error and preserves the last valid preview");
        }
        ((Button)settingsWindow.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(limitError.Visibility==Visibility.Visible && settingsWindow.Result==null,"An invalid count keeps settings open with a field-level error");
        limitInput.Text = "2";
        Check(limitError.Visibility==Visibility.Collapsed && samplePlot.Points.Count==3 &&
            sampleCard.DataContext is RecentMatchDisplay {Summary.Matches.Count:2},
            "Two matches and their starting score appear immediately at the lower endpoint");
        limitInput.Text = "50";
        Check(limitError.Visibility==Visibility.Collapsed && samplePlot.Points.Count>21,
            "Entering the upper endpoint immediately clears the error and updates the preview");
        limitInput.Text = "5";
        Check(samplePlot.Points.Count == 6 && sampleCard.DataContext is RecentMatchDisplay {Summary.Matches.Count:5},
            "Five matches and their starting score drive both the preview plot and interval metrics");
        Check(HasInteriorExtremes(),"The five-match preview also places both extrema inside the chart");
        var metrics = (Grid)sampleCard.FindName("Metrics");
        var switches = new[] {"OverlayExtremes","OverlayChange","OverlayWinRate"}
            .Select(id => (CheckBox)settingsWindow.FindName(id)).ToArray();
        for (var mask = 7; mask > 0; mask--)
        {
            for (var i = 0; i < switches.Length; i++) switches[i].IsChecked = (mask & (1 << i)) != 0;
            foreach (var width in new[] {388d, 528d})
            {
                metrics.Measure(new Size(width, 72));
                metrics.Arrange(new Rect(0, 0, width, 72));
                var centers = metrics.Children.Cast<FrameworkElement>()
                    .Where(item => item.Visibility == Visibility.Visible)
                    .Select(item => item.TranslatePoint(new Point(item.ActualWidth / 2, 0), metrics).X).ToArray();
                Check(Math.Abs((centers[0] + centers[^1]) / 2 - width / 2) <= 1 &&
                    centers.Zip(centers.Skip(1), (left, right) => right - left).All(gap => Math.Abs(gap - width / 4) <= 1),
                    $"Adjacent visible metrics stay a quarter-width apart and centered at width {width}, mask {mask}");
            }
        }
        foreach(var toggle in switches) toggle.IsChecked=false;
        Check(((FrameworkElement)sampleCard.FindName("Metrics")).Visibility == Visibility.Collapsed,"Hiding all interval metrics removes the entire metrics row");
        var helpSection = (Expander)settingsWindow.FindName("OverlayCaptureHelp");
        var overlayPage = (StackPanel)settingsWindow.FindName("OverlayPage");
        Check(!helpSection.IsExpanded && overlayPage.Children[overlayPage.Children.Count - 1] == helpSection,
            "OBS troubleshooting starts collapsed at the bottom of overlay settings");
        helpSection.ApplyTemplate();
        var helpToggle = (System.Windows.Controls.Primitives.ToggleButton)helpSection.Template.FindName("HeaderSite", helpSection);
        var togglePeer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(helpToggle);
        ((System.Windows.Automation.Provider.IToggleProvider)togglePeer).Toggle();
        Check(helpSection.IsExpanded, "Activating the disclosure opens OBS troubleshooting");
        ((CheckBox)settingsWindow.FindName("OverlayStartup")).IsChecked = true;
        ((CheckBox)settingsWindow.FindName("OverlayTopmost")).IsChecked = true;
        ((CheckBox)settingsWindow.FindName("OverlayGreen")).IsChecked = true;
        var draft = (ConnectionSettings)typeof(SettingsWindow).GetMethod("Draft",flags)!.Invoke(settingsWindow,[true])!;
        Check(draft is {OverlayGreenScreen:true,OverlayBackgroundOpacity:37,OpenOverlayOnStartup:true,OverlayAlwaysOnTop:true,
            OverlayStyle.MatchLimit:5,OverlayStyle.ShowExtremes:false},
            "The dedicated settings page preserves custom opacity independently of the green screen and startup toggle");
        draft.Save(path);
        Check(ConnectionSettings.Load(path) == draft,"Overlay options survive relaunch");
        File.WriteAllText(Path.Combine(path,"settings.json"),"""{"StreamBackground":3}""");
        Check(ConnectionSettings.Load(path) is {OverlayGreenScreen:true, OverlayBackgroundOpacity:75,OpenOverlayOnStartup:false},
            "Existing green-screen preferences migrate with an independent opacity");
        settingsWindow.Close();
        Check(stream.AllowsTransparency && Strings.StreamWindowTitle == "SF6 Recorder OBS",
            "The translucent stream retains its capture title");
        archive.SignedInId = null;
        var closed = false; stream.Closed += (_,_)=>closed=true;
        Update();
        Check(closed, "Signing out closes the old account's stream");
        Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero && App.DataDirectory=="","Recent matches checks never create a native window or use user data");
        return count;
    }
}
