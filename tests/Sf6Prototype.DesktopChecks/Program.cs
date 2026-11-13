using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sf6Prototype;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var args=Environment.GetCommandLineArgs();var inspect=Array.IndexOf(args,"--match-end-inspect");
        if(inspect>=0) {MatchEndVisionChecks.Inspect(args.Skip(inspect+1));return;}
        if(Environment.GetCommandLineArgs().Contains("--match-end-bench"))
        {MatchEndVisionChecks.Benchmark(Path.Combine(Environment.CurrentDirectory,".local","match-detection"));return;}
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        // Construct compiled controls without Show(), EnsureHandle(), startup, real data or network.
        // A plain WPF host is essential when pumping bindings: the real App.OnStartup
        // would otherwise be dispatched and attempt to open the user's data directory.
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        LocalizationWpf.Apply("zh");
        var assembly=Uri.EscapeDataString(typeof(MainWindow).Assembly.GetName().Name!);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {Source=new Uri($"pack://application:,,,/{assembly};component/Themes/Utility.xaml")});
        if(args.Contains("--match-end-follow-capture")) {MatchEndFollowChecks.Capture();app.Shutdown();return;}
        if(args.Contains("--cancellation-checks")) {Console.WriteLine($"Passed {CancellationChecks.Run()} cancellation checks.");app.Shutdown();return;}
        var main=new MainWindow();
        main.ApplyTemplate();main.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);
        Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"Control checks do not create a native window or tray icon");
        Check(((TextBlock)main.FindName("ViewedPlayerName")).FontSize==16,"The active player uses the enlarged header typography");
        Check(main.FindName("PlayerUidBox") is TextBox && main.FindName("PlayerSearchHistoryList") is ItemsControl,"Compiled player-search controls load with their resources and handlers");
        var navigation=(StackPanel)main.FindName("NavigationBar");
        Check(navigation.Children.OfType<Button>().Take(4).Select(b=>b.Name).SequenceEqual(["BackButton","ForwardButton","SyncButton","HomeButton"]),"The header navigation order is back, forward, refresh, home");
        Check(((Button)main.FindName("BackButton")).ContextMenu!=null && ((Button)main.FindName("ForwardButton")).ContextMenu!=null,"Both directional buttons support a right-click history menu");
        var uid=(TextBox)main.FindName("PlayerUidBox");var clear=(Button)main.FindName("ClearPlayerUidButton");var submit=(Button)main.FindName("PlayerSearchSubmit");
        Check(submit.Visibility==Visibility.Collapsed && clear.Visibility==Visibility.Collapsed,$"Inactive search keeps its auxiliary actions hidden ({submit.Visibility}/{clear.Visibility})");
        main.SetValue(MainWindow.IsPlayerSearchActiveProperty,true);uid.Text="1234567890";
        main.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);
        System.Windows.Data.BindingOperations.GetBindingExpression(submit,UIElement.VisibilityProperty)?.UpdateTarget();
        Check(submit.Visibility==Visibility.Visible && clear.Visibility==Visibility.Visible,"Activating a populated UID field shows search and clear actions");
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(uid.Text=="" && clear.Visibility==Visibility.Collapsed,"The clear action empties the field and hides its own button");
        main.SetValue(MainWindow.IsPlayerSearchActiveProperty,false);
        Check(((ListBox)main.FindName("PageTabs")).Items.Count==2,"Bookmarks remain a history filter without creating another page");
        var dataRoot=System.IO.Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(System.IO.Path.Combine(dataRoot,"ui.sqlite"));
        const string player="1234567890";
        var day=new DateTime(2026,11,1);var epoch=DateRanges.Epoch(day)!.Value;
        store.SavePage(Enumerable.Range(0,3).Select(i=>new Battle {OwnerId=player,ReplayId="R"+i,Category="rank",PlayedAt=epoch+i*86400,CharacterId=1,Character="隆",OpponentCharacter=i==1 ? "肯" : "春丽",Lp=10000+i*50,MyRounds=2,OpponentRounds=0}));
        store.AddTag(player,"R1","连招");store.AddTag(player,"R1","防空");store.AddTag(player,"R2","防空");
        var archive=new Archive(store,new ProfileCache(dataRoot));archive.Load(player);
        var history=(HistoryView)main.FindName("History");history.Bind(archive);
        history.Reload(restore:new() {OpponentCharacter="肯",FavoritesOnly=true,Tags=["连招","防空"],MatchAllTags=true});
        Check(history.Filtered.Select(b=>b.ReplayId).SequenceEqual(["R1"]) && history.Selected?.Tags.Count==2,"Compiled history combines opponent and all-tag filters and exposes persisted capsules");
        Check(history.CaptureState() is {OpponentCharacter:"肯",FavoritesOnly:true,MatchAllTags:true} && ((ItemsControl)history.FindName("SelectedTags")).Items.Count==2,"The selected tag strip and navigation state retain multi-selection");
        var rowGrid=new HistoryTableGrid();
        Check(rowGrid.ColumnDefinitions.Count==8 && rowGrid.ColumnDefinitions[7].Width==rowGrid.TagWidth,"The shared header and row grid includes an aligned tag column");
        var picker=new TagPicker();
        Check(!picker.IsOpen && picker.Child is ContentControl,"Tag picker styles construct without opening a popup");
        Check(picker.HorizontalOffset==0 && picker.VerticalOffset==4 && ((ContentControl)picker.Child).Margin==new Thickness(0),"Tag popups use a positive gap and no negative margin compensation");
        var uidPopup=(System.Windows.Controls.Primitives.Popup)main.FindName("PlayerSearchPopup");
        Check(uidPopup.HorizontalOffset==0 && uidPopup.VerticalOffset==4 && ((ContentControl)uidPopup.Child).Margin==new Thickness(0),"The UID menu surface starts directly below its field without an implicit inset");
        var pickerContent=(StackPanel)((ContentControl)picker.Child).Content;
        Check(pickerContent.Children.OfType<ScrollViewer>().Single().Content is WrapPanel && !pickerContent.Children.OfType<CheckBox>().Any(),"Tag choices wrap compact capsules with no collection enable switch");
        Check(((ListBox)history.FindName("CategoryFilter")).Items.OfType<CategoryChoice>().Last().Key=="favorites","Favorites appears after the official battle categories");
        var normalHistory=new HistoryView();normalHistory.Bind(archive);normalHistory.DefaultFavoritePlayer=new(player,"登录用户");normalHistory.Reload();
        Check(((Button)normalHistory.FindName("FavoritesButton")).Visibility==Visibility.Collapsed && ((Button)normalHistory.FindName("FavoriteUserButton")).Visibility==Visibility.Collapsed,"Normal battle categories contain no bookmark or user filter");
        var categories=(ListBox)normalHistory.FindName("CategoryFilter");categories.SelectedItem=categories.Items.OfType<CategoryChoice>().Single(c=>c.Key=="favorites");
        Check(normalHistory.FavoritesOnly && normalHistory.FavoritePlayer?.Id==player && normalHistory.Filtered.Count==2,"Entering the collection defaults to the signed-in player's favorites");
        const string opponent="9999999999";
        var foreign=new Battle {OwnerId=player,OpponentId=opponent,PlayerName="登录用户",Opponent="对手",ReplayId="R3",Category="rank",CharacterId=1,Character="隆",OpponentCharacter="春丽",OpponentLp=15000,MyRounds=0,OpponentRounds=2,PlayedAt=epoch};
        store.SavePage([foreign]);store.AddTag(player,"R3","复盘");
        normalHistory.Reload(restore:new() {FavoritesOnly=true,FavoritePlayer=new(opponent,"对手")});
        Check(normalHistory.Selected is {Battle: {OwnerId:opponent,Character:"春丽",MyRounds:2,Lp:15000},Point:{LpDelta:null}} && normalHistory.ActiveArchive?.Owner==opponent,"Collection rows and their detail archive share the chosen perspective without inventing settlements");
        var colors=Enumerable.Range(0,24).Select(i=>((System.Windows.Media.SolidColorBrush)TagBrushConverter.Brush(i,true)).Color).ToArray();
        Check(colors.Distinct().Count()==24,"Tag colors remain distinct beyond the initial theme palette");
        Check(colors.All(c=>System.Drawing.Color.FromArgb(c.R,c.G,c.B).GetHue() is <220 or >315),"Tag colors avoid the purple hue used by selected battle rows");
        var scores=(ScoresView)main.FindName("Scores");scores.Bind(archive);
        scores.Reload(new(1,false,20,"R1",day.AddDays(1),day.AddDays(1)));
        Check(((ScoreChart)scores.FindName("Chart")).Points.Select(p=>p.ReplayId).SequenceEqual(["R1"]),"Score dates filter the chart before applying its record count");
        Check(scores.CaptureState() is {SelectedReplay:"R1"} state && state.Start==day.AddDays(1) && state.End==day.AddDays(1),"Chart selection and dates survive restoring a saved view");
        var setBusy=typeof(MainWindow).GetMethod("SetBusy",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var sync=(Button)main.FindName("SyncButton");var ring=(RefreshProgressGlyph)main.FindName("SyncRing");
        setBusy.Invoke(main,[true,false,false]);
        Check(ring.Visibility==Visibility.Visible && ring.Indeterminate && sync.IsHitTestVisible && sync.Focusable && sync.ToolTip is TextBlock,"Refresh remains hoverable and shows an indeterminate ring before page totals are known");
        var model=(RefreshProgressState)typeof(MainWindow).GetField("_refreshProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main)!;
        model.Load(new("读取排位",.5,1,1,"已准备 10 / 20 场"),2);
        typeof(MainWindow).GetMethod("UpdateButtonProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,null);
        Check(!ring.Indeterminate && ((TextBlock)sync.ToolTip).Text.Contains("10 / 20") && (double)ring.GetAnimationBaseValue(RefreshProgressGlyph.ValueProperty)>0,"The same button switches to determinate progress with live detailed hover text");
        ring.Measure(new Size(32,32));ring.Arrange(new Rect(0,0,28,28));
        Check(ring.DesiredSize.Width<=sync.Width && ring.DesiredSize.Height<=sync.Height,"The progress ring fits inside the existing navigation button");
        typeof(MainWindow).GetMethod("FinishButtonProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,[true,false]);
        setBusy.Invoke(main,[false,false,false]);
        Check(ring.Visibility==Visibility.Visible && (double)ring.GetAnimationBaseValue(RefreshProgressGlyph.ValueProperty)==1,"Successful completion briefly retains the full ring");
        setBusy.Invoke(main,[true,true,false]);
        Check(ring.Visibility==Visibility.Visible && ring.Indeterminate && sync.ToolTip is TextBlock,"Pagination and implicit history loads expose the same progress and stop action");
        setBusy.Invoke(main,[false,false,false]);
        var opened=0;var exited=0;var settingsOpened=0;
        using(var menu=new ModernTrayMenu(()=>opened++,()=>settingsOpened++,()=>exited++))
        {
            Check(!menu.ShowImageMargin && !menu.ShowCheckMargin && !menu.IsHandleCreated,"Modern tray menus remove native gutters without opening a test menu");
            var initialMenuSize=menu.Size;
            menu.ApplyDpi((uint)menu.DeviceDpi);
            Check(menu.Size==initialMenuSize,"The first tray menu already has its display-DPI size before WinForms calculates its cursor anchor");
            menu.Items[0].PerformClick();menu.Items[1].PerformClick();menu.Items[3].PerformClick();
            Check(opened==1 && settingsOpened==1 && exited==1,"Tray commands invoke open, settings and exit independently");
            menu.ApplyDpi(192);var tall=menu.Items[0].Height;menu.ApplyDpi(96);
            Check(tall==64 && menu.Items[0].Height==32,"Menu dimensions adapt to DPI without compounding repeated scaling");
            foreach(var dpi in new uint[]{96,120,144,192,240,288,96})
            {
                menu.ApplyDpi(dpi);
                Check(menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().All(item=>menu.TextBounds(item).Width>=menu.MeasureLabel(item).Width && item.Height>=menu.MeasureLabel(item).Height && menu.Width>=item.Bounds.Right+menu.Padding.Right),$"Menu text and items fit the measured surface at {dpi} DPI");
                var inset=(int)Math.Round(5*dpi/96d);
                var bounds=menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(item=>item.Bounds).ToArray();
                // Native separators have a full-width, non-selectable surface;
                // the action rows define the visible hover and content insets.
                var rows=menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Select(item=>item.Bounds).ToArray();
                Check(rows.All(b=>b.Left==inset && menu.ClientSize.Width-b.Right==inset) && rows[0].Top==inset &&
                    menu.ClientSize.Height-rows[^1].Bottom==inset,$"Tray content has equal outer insets at {dpi} DPI (client={menu.ClientSize}, rows={string.Join("; ",rows)})");
                menu.Items[0].Select();menu.PerformLayout();menu.Items[3].Select();menu.PerformLayout();
                Check(menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Select(item=>item.Bounds).SequenceEqual(bounds),
                    $"Hover selection and repeated layout preserve the aligned item bounds at {dpi} DPI");
                Check(menu.GetItemAt(new System.Drawing.Point(inset-1,bounds[0].Top+bounds[0].Height/2))==null &&
                    menu.GetItemAt(new System.Drawing.Point(bounds[0].Left+1,bounds[0].Top+bounds[0].Height/2))==menu.Items[0],
                    $"The hover hit area follows the inset content instead of covering the outer gutter at {dpi} DPI");
            }
        }
        var settings=new SettingsWindow(null!,new ConnectionSettings());
        var reminder=(TextBox)settings.FindName("ReminderSecondsBox");
        var reminderPresets=(ListBox)settings.FindName("ReminderPresets");
        reminder.Text="0";
        Check((reminderPresets.SelectedItem as ListBoxItem)?.Tag?.ToString()=="0","Zero input selects the disabled reminder preset");
        var autoPresets=(ListBox)settings.FindName("AutoRefreshPresets");autoPresets.SelectedIndex=2;
        Check(((TextBox)settings.FindName("AutoRefreshSecondsBox")).Text=="1200" && reminder.Text=="0","Automatic refresh presets do not overwrite the separate reminder field");
        Check(settings.FindName("BackupButton") is Button && settings.FindName("ImportButton") is Button && ((ComboBox)settings.FindName("CloseActionBox")).SelectedIndex==0,"Backup, merge import and the resettable close preference exist in compiled settings");
        var api=(TextBox)settings.FindName("ApiIntervalBox");var apiPresets=(ListBox)settings.FindName("ApiIntervalPresets");var warning=(TextBlock)settings.FindName("ApiIntervalWarning");
        Check(api.Text=="2" && (apiPresets.SelectedItem as ListBoxItem)?.Tag?.ToString()=="2" && warning.Visibility==Visibility.Collapsed,"The two-second default selects its preset without a warning");
        apiPresets.SelectedIndex=0;
        Check(api.Text=="1" && warning.Visibility==Visibility.Visible && reminder.Text=="0","Low presets populate the interval and warn without changing reminder settings");
        api.Text="2.5";
        Check(apiPresets.SelectedItem==null && warning.Visibility==Visibility.Collapsed,"Custom intervals above two seconds do not show the warning");
        var draft=typeof(SettingsWindow).GetMethod("Draft",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var matchSwitch=(CheckBox)settings.FindName("RefreshAfterMatchEnabled");
        var matchWait=(TextBox)settings.FindName("MatchEndWaitBox");var matchRetry=(TextBox)settings.FindName("MatchEndRetryBox");
        var matchFrequency=(TextBox)settings.FindName("MatchEndFrequencyBox");
        Check(matchFrequency.Text=="4","Match detection defaults to four samples per second");
        Check(matchSwitch.IsChecked==false && matchSwitch.IsEnabled && matchWait.Text=="5" && matchRetry.Text=="8","The match-end switch remains usable without a running game, with requested default delays");
        matchSwitch.IsChecked=true;matchWait.Text="9";matchRetry.Text="11";
        matchFrequency.Text="2.5";
        Check((ConnectionSettings)draft.Invoke(settings,[true])! is {RefreshAfterMatch:true,MatchEndWaitSeconds:9,MatchEndRetrySeconds:11},"Settings save the switch and custom wait/retry intervals");
        Check(((ConnectionSettings)draft.Invoke(settings,[true])!).MatchEndSamplesPerSecond==2.5,"Settings save a custom sampling frequency");
        ((CheckBox)settings.FindName("FocusLatestMatchEnabled")).IsChecked=false;
        Check(!((ConnectionSettings)draft.Invoke(settings,[true])!).FocusLatestMatchAfterRefresh,"The settings switch can independently disable following the new match");
        Check(((ConnectionSettings)draft.Invoke(settings,[true])!).ApiIntervalSeconds==2.5,"Saving settings preserves custom decimal API intervals");
        api.Text="";
        Check(((ConnectionSettings)draft.Invoke(settings,[false])!).ApiIntervalSeconds==2,"A partially edited API interval does not block the independent Google proxy test");
        apiPresets.SelectedIndex=2;
        var settingsContent=(FrameworkElement)settings.Content;settingsContent.Measure(new Size(settings.Width-16,double.PositiveInfinity));settingsContent.Arrange(new Rect(settingsContent.DesiredSize));
        var intervalRight=api.TranslatePoint(new Point(api.ActualWidth,0),settingsContent).X;
        var presetsLeft=apiPresets.TranslatePoint(new Point(0,0),settingsContent).X;
        // The integrated title bar is already part of the measured content.
        var settingsFrameHeight=2*SystemParameters.ResizeFrameHorizontalBorderHeight;
        Check(settingsContent.DesiredSize.Height+settingsFrameHeight<=720 && intervalRight<=presetsLeft,$"The settings window fits the minimum height without overlapping its preset group (contentHeight={settingsContent.DesiredSize.Height:F2}, frameHeight={settingsFrameHeight:F2}, intervalRight={intervalRight:F2}, presetsLeft={presetsLeft:F2}, font={settings.FontFamily})");
        var choice=new CloseChoiceWindow(null!);
        Check(choice.Choice==CloseAction.Ask && !choice.Remember,"Dismissed close choices cannot silently become a remembered action");
        var loader=typeof(TrayIcon).GetMethod("LoadIcon",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var size in new[]{16,20,24,32,40,48,64})
        {
            using var icon=(System.Drawing.Icon)loader.Invoke(null,[size])!;
            Check(icon.Width==size && icon.Height==size && icon.Handle!=IntPtr.Zero,$"Packaged ICO provides a native {size}-pixel icon without shrinking a single frame");
        }
        Check(App.DataDirectory=="" && !main.IsVisible && !settings.IsVisible && !choice.IsVisible && new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero && new System.Windows.Interop.WindowInteropHelper(settings).Handle==IntPtr.Zero && new System.Windows.Interop.WindowInteropHelper(choice).Handle==IntPtr.Zero,"Desktop checks never start the application, create native windows or use user data");
        count+=ArchiveSnapshotChecks.Run();
        count+=CancellationChecks.Run();
        count+=RefreshReminderChecks.Run();
        count+=MatchEndFailureImageChecks.Run();
        count+=MatchEndVisionChecks.Run();
        count+=MatchEndFollowChecks.Run();
        count+=RecentMatchesChecks.Run();
        count+=PresentationLayoutChecks.Run();
        count+=HistoryAlignmentChecks.Run();
        count+=TitleBarChecks.Run();
        count+=OpponentChromeChecks.Run();
        count+=AuxiliaryChromeChecks.Run();
        count+=LocalizationChecks.Run();
        count+=ToastChecks.Run();
        count+=UpdateUiChecks.Run();
        Console.WriteLine($"Passed {count} headless desktop checks.");
    }
}
