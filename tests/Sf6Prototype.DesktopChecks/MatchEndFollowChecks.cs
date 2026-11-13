using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sf6Prototype;

internal static class MatchEndFollowChecks
{
    public static void Capture()
    {
        // Optional visual probe only: a plain WPF host with synthetic local data.
        // Keep it off-screen and inactive so the running game retains focus.
        var root=Path.Combine(Environment.CurrentDirectory,".local","capture-data","match-end-follow-motion",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"motion.sqlite"));const string owner="1234567890";
        var at=DateRanges.Epoch(new DateTime(2026,11,14))!.Value;
        store.SavePage(Enumerable.Range(0,60).Select(i=>new Battle {OwnerId=owner,ReplayId=$"R{i:00}",Category="rank",PlayedAt=at-i*60,
            CharacterId=1,Character="隆",OpponentCharacterId=2,OpponentCharacter="肯",Lp=12000,MyRounds=2,OpponentRounds=0}));
        var archive=new Archive(store,new ProfileCache(root)) {SignedInId=owner};archive.Load(owner,false);
        var history=new HistoryView();history.Bind(archive);history.Reload();
        var window=new Window {Content=history,Width=900,Height=660,Left=-10000,Top=0,ShowActivated=false,ShowInTaskbar=false,
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize};
        void Pump(int milliseconds=1)
        {
            var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(milliseconds)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);history.UpdateLayout();
        }
        void Save(string name)
        {
            var image=new RenderTargetBitmap((int)history.ActualWidth,(int)history.ActualHeight,96,96,PixelFormats.Pbgra32);
            image.Render(history);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream=File.Create(Path.Combine(root,name+".png"));encoder.Save(stream);
        }
        window.Show();
        try
        {
            Pump(50);var list=(ListBox)history.FindName("MatchesList");var viewer=Descendants<ScrollViewer>(list).First();
            list.SelectedIndex=30;viewer.ScrollToVerticalOffset(1200);Pump(40);Save("before");
            store.SavePage([new Battle {OwnerId=owner,ReplayId="NEW",Category="rank",PlayedAt=at+60,
                CharacterId=1,Character="隆",OpponentCharacter="肯",Lp=12000,MyRounds=2,OpponentRounds=0}]);
            archive.Load(owner,false);history.Reload(preserveViewport:true,automaticRefresh:true);Pump(30);
            var from=viewer.VerticalOffset;history.RevealLatestMatch(owner,"NEW",history.InteractionVersion);Pump(120);
            var middle=viewer.VerticalOffset;Save("middle");Pump(240);var end=viewer.VerticalOffset;Save("after");
            if(SystemParameters.ClientAreaAnimation && !(from>middle && middle>0)) throw new Exception($"Scroll animation did not move smoothly: {from}, {middle}");
            if(end>1 || history.Selected?.Battle.ReplayId!="NEW") throw new Exception("Animated following did not finish on the new match");
            list.SelectedIndex=30;viewer.ScrollToVerticalOffset(1200);Pump(30);
            history.RevealLatestMatch(owner,"NEW",history.InteractionVersion);Pump(80);
            list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,120) {RoutedEvent=UIElement.PreviewMouseWheelEvent});
            var interrupted=viewer.VerticalOffset;Pump(300);
            if(SystemParameters.ClientAreaAnimation && (Math.Abs(interrupted-viewer.VerticalOffset)>1 || history.Selected?.Battle.ReplayId=="NEW"))
                throw new Exception("User scrolling did not cancel automatic motion and deferred selection");
            var report=new {from,middle,end,selected="NEW",interrupted,afterInterruption=viewer.VerticalOffset,window.IsActive,
                reducedMotion=!SystemParameters.ClientAreaAnimation,root};
            File.WriteAllText(Path.Combine(root,"result.json"),System.Text.Json.JsonSerializer.Serialize(report));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
        }
        finally {window.Close();}
    }
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var main=new MainWindow();main.ApplyTemplate();
        var history=(HistoryView)main.FindName("History");var list=(ListBox)history.FindName("MatchesList");
        var root=(FrameworkElement)main.Content;
        void Set(string field,object value)=>typeof(MainWindow).GetField(field,flags)!.SetValue(main,value);
        object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(main,args);
        void Layout()
        {
            root.Measure(new Size(1380,880));root.Arrange(new Rect(0,0,1380,880));root.UpdateLayout();
            main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);root.UpdateLayout();
        }
        var path=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(path,"follow.sqlite"));const string owner="1234567890";
        var at=DateRanges.Epoch(new DateTime(2026,11,14))!.Value;
        var modes=new[]{"rank","casual","custom","hub"};
        store.SavePage(modes.SelectMany((mode,m)=>Enumerable.Range(0,40).Select(i=>new Battle {
            OwnerId=owner,ReplayId=$"{mode}-{i:00}",Category=mode,PlayedAt=at-i*60-m,
            CharacterId=1,Character="隆",OpponentCharacterId=2,OpponentCharacter="肯",MyRounds=2,OpponentRounds=0,Lp=12000})));
        var archive=new Archive(store,new ProfileCache(path)) {SignedInId=owner};archive.Load(owner,false);
        var navigation=(PlayerNavigation)typeof(MainWindow).GetField("_navigation",flags)!.GetValue(main)!;
        navigation.SetHome(new(owner,"Me"));Set("_archive",archive);history.Bind(archive);
        Set("_settings",new ConnectionSettings {RefreshAfterMatch=true});
        var serial=0;
        void View(string category)
        {
            history.ResetFilters(category);history.Reload();Layout();
            list.SelectedIndex=Math.Min(20,list.Items.Count-1);
            Descendants<ScrollViewer>(list).First().ScrollToVerticalOffset(500);Layout();
        }
        Battle Add(string category,string character="隆")
        {
            var match=new Battle {OwnerId=owner,ReplayId=$"new-{++serial}",Category=category,PlayedAt=at+serial*60,
                CharacterId=character=="隆" ? 1 : 2,Character=character,OpponentCharacter="肯",MyRounds=2,OpponentRounds=0,Lp=12000};
            store.SavePage([match]);archive.Load(owner,false);history.Reload(preserveViewport:true,automaticRefresh:true);return match;
        }
        foreach(var category in modes.Prepend(""))
        {
            View(category);var before=Call("CaptureMatchEndFollow",owner);var match=Add(category=="" ? "hub" : category);
            Call("FollowRefreshedMatch",before);Layout();
            Check(history.Selected?.Battle.ReplayId==match.ReplayId && Descendants<ScrollViewer>(list).First().VerticalOffset<1,
                $"A new {category} match scrolls to the first row and becomes selected");
            Check(((MatchDetailPanel)main.FindName("Detail")).Row?.Battle.ReplayId==match.ReplayId,"Following a new match updates the detail panel too");
        }
        View("rank");var selected=history.Selected?.Battle.ReplayId;var follow=Call("CaptureMatchEndFollow",owner);
        Add("custom");Call("FollowRefreshedMatch",follow);Layout();
        Check(history.Selected?.Battle.ReplayId==selected,"A different mode never moves the ranked selection");
        View("rank");follow=Call("CaptureMatchEndFollow",owner);selected=history.Selected?.Battle.ReplayId;
        Call("FollowRefreshedMatch",follow);Layout();
        Check(history.Selected?.Battle.ReplayId==selected,"A refresh with no new match preserves selection");
        View("rank");follow=Call("CaptureMatchEndFollow",owner);Add("rank");
        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,120) {RoutedEvent=UIElement.PreviewMouseWheelEvent});
        selected=history.Selected?.Battle.ReplayId;Call("FollowRefreshedMatch",follow);Layout();
        Check(history.Selected?.Battle.ReplayId==selected,"User scrolling while refresh finishes cancels automatic following");
        View("rank");follow=Call("CaptureMatchEndFollow",owner);history.ResetFilters("hub");history.Reload();selected=history.Selected?.Battle.ReplayId;
        Add("rank");Call("FollowRefreshedMatch",follow);Layout();
        Check(history.Selected?.Battle.ReplayId==selected,"Changing modes during refresh preserves the new view");
        View("rank");((ComboBox)history.FindName("CharacterFilter")).SelectedIndex=1;Layout();
        follow=Call("CaptureMatchEndFollow",owner);selected=history.Selected?.Battle.ReplayId;Add("rank","肯");
        Call("FollowRefreshedMatch",follow);Layout();
        Check(history.Selected?.Battle.ReplayId==selected,"A new match excluded by a character filter does not clear the filter or select an older match");
        Set("_settings",new ConnectionSettings {RefreshAfterMatch=true,FocusLatestMatchAfterRefresh=false});
        Check(Call("CaptureMatchEndFollow",owner)==null,"Disabling the setting preserves the existing automatic refresh behavior");
        Set("_settings",new ConnectionSettings {RefreshAfterMatch=true});
        navigation.Visit(new("9876543210","Other"));
        Check(Call("CaptureMatchEndFollow",owner)==null,"An account refresh cannot move another player's history");
        navigation.ReturnHome();
        ((ListBox)main.FindName("PageTabs")).SelectedIndex=1;
        Check(Call("CaptureMatchEndFollow",owner)==null,"The scores page is never switched to history by match-end refresh");
        ((ListBox)main.FindName("PageTabs")).SelectedIndex=0;
        var schedule=(MatchEndRefreshSchedule)typeof(MainWindow).GetField("_matchEndRefresh",flags)!.GetValue(main)!;
        var scale=(ScaleTransform)main.FindName("MatchEndStatusScale");
        AnimationClock? Clock()=>(AnimationClock?)typeof(MainWindow).GetField("_matchEndStatusClock",flags)!.GetValue(main);
        schedule.Queue(owner,DateTimeOffset.UtcNow);Call("UpdateMatchEndStatusAnimation");Layout();
        if(SystemParameters.ClientAreaAnimation)
        {
            var clock=Clock()!;
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(180),TimeSeekOrigin.BeginTime);
            Check(Math.Abs(scale.ScaleY-.35)<.001,"Detection immediately starts the light's squash animation during the five-second wait");
            clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(420),TimeSeekOrigin.BeginTime);
            Check(Math.Abs(scale.ScaleY-1)<.001,"The light returns to a circle before repeating");
            schedule.Complete(schedule.Begin(),true,false,DateTimeOffset.UtcNow);Call("UpdateMatchEndStatusAnimation");
            Check(ReferenceEquals(clock,Clock()),"The same animation continues across the settlement retry wait");
            var oldVersion=schedule.Begin();schedule.Queue(owner,DateTimeOffset.UtcNow);
            schedule.Complete(oldVersion,true,true,DateTimeOffset.UtcNow);Call("UpdateMatchEndStatusAnimation");
            Check(schedule.Owner==owner && ReferenceEquals(clock,Clock()),"An older refresh finishing keeps the light animated for the newer pending detection");
        }
        else Check(Clock()==null,"The light respects Windows reduced-motion preferences");
        Set("_matchEndRefreshInFlight",true);schedule.Clear();Call("UpdateMatchEndStatusAnimation");
        Check(!SystemParameters.ClientAreaAnimation || Clock()!=null,"A refresh still running keeps the animation alive if the pending schedule is cleared");
        Set("_matchEndRefreshInFlight",false);Call("UpdateMatchEndStatusAnimation");Layout();
        Check(Clock()==null && !scale.HasAnimatedProperties && scale.ScaleY==1,"Completion removes the animation and restores the round light");
        schedule.Queue(owner,DateTimeOffset.UtcNow);Call("UpdateMatchEndStatusAnimation");
        Set("_settings",new ConnectionSettings {RefreshAfterMatch=false});Call("UpdateMatchEndStatusAnimation");
        Check(Clock()==null && scale.ScaleY==1,"Disabling match-end refresh also stops the status animation");
        schedule.Clear();
        Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero && App.DataDirectory=="","Follow checks never start a native window or access user data");
        return count;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T item)yield return item;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
}
