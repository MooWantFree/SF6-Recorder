using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Sf6Prototype;

internal static class CancellationChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var previousContext=SynchronizationContext.Current;
        var main=new MainWindow();main.ApplyTemplate();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object? Call(object target,string name,params object?[] args)=>target.GetType().GetMethod(name,flags)!.Invoke(target,args);
        void Set(string field,object? value)=>typeof(MainWindow).GetField(field,flags)!.SetValue(main,value);
        T Get<T>(string field)=>(T)typeof(MainWindow).GetField(field,flags)!.GetValue(main)!;
        void Pump()=>main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
        void Complete(Task task) {Pump();Check(task.IsCompleted,"Headless operation completed without network or a native window");task.GetAwaiter().GetResult();}
        void MouseDown(UIElement element,MouseButton button=MouseButton.Left)=>element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button) {RoutedEvent=Mouse.PreviewMouseDownEvent});
        void Busy(bool busy)=>Call(main,"SetBusy",busy,false,false);
        var root=System.IO.Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(System.IO.Path.Combine(root,"interaction.sqlite"));
        var profiles=new ProfileCache(root);var archive=new Archive(store,profiles);
        var home=new PlayerIdentity("1234567890","本机用户");var other=new PlayerIdentity("2222222222","第二位");var third=new PlayerIdentity("3333333333","第三位");
        // Complete local snapshots ensure an accidental UI demand cannot reach the official site.
        foreach(var player in new[]{home,other,third})
        {
            store.SavePage(Enumerable.Range(0,3).Select(i=>new Battle {OwnerId=player.Id,PlayerName=player.Name,OpponentId="9999999999",Opponent="对手",ReplayId="R"+i,Category="rank",CharacterId=1,Character="隆",OpponentCharacter="春丽",PlayedAt=1000+i,Lp=10000+i*50,MyRounds=2,OpponentRounds=1}));
            foreach(var category in BattleCategory.All)store.CompleteCategory(player.Id,category.Key,null);
        }
        Set("_store",store);Set("_profiles",profiles);Set("_archive",archive);Set("_vault",new CookieVault(root));Set("_playerSearch",new PlayerSearchHistory(root));
        Set("_settings",new ConnectionSettings {PlayerId=home.Id});
        var navigation=Get<PlayerNavigation>("_navigation");navigation.SetHome(home);
        var history=(HistoryView)main.FindName("History");var scores=(ScoresView)main.FindName("Scores");var detail=(MatchDetailPanel)main.FindName("Detail");
        history.Bind(archive);scores.Bind(archive);detail.Bind(archive);
        Set("_ready",true);Call(main,"ReloadLocal",false,true,null,false);
        var ring=(RefreshProgressGlyph)main.FindName("SyncRing");var sync=(Button)main.FindName("SyncButton");
        var tokens=new List<CancellationTokenSource>();
        (CancellationTokenSource Token,TaskCompletionSource Work) Download()
        {
            var token=new CancellationTokenSource();tokens.Add(token);var work=new TaskCompletionSource();
            Set("_cancellation",token);Set("_foregroundTask",work.Task);
            token.Token.Register(()=>{Busy(false);work.TrySetResult();});Busy(true);
            return (token,work);
        }
        try
        {
            var first=Download();
            MouseDown((UIElement)main.Content);
            Check(!first.Token.IsCancellationRequested && ring.Visibility==Visibility.Visible,"Clicking blank content preserves the download and progress ring");
            MouseDown((Button)main.FindName("SettingsButton"));
            Check(!first.Token.IsCancellationRequested,"The settings button's input route does not cancel downloading");
            var list=(ListBox)history.FindName("MatchesList");list.SelectedIndex=1;MouseDown(list);
            Check(!first.Token.IsCancellationRequested && detail.Row?.Battle.ReplayId==history.Selected?.Battle.ReplayId,"Selecting an existing match opens its detail without cancelling");
            list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) {RoutedEvent=Mouse.PreviewMouseWheelEvent});
            Check(!first.Token.IsCancellationRequested,"Scrolling through existing matches does not cancel the active request");
            var categories=(ListBox)history.FindName("CategoryFilter");categories.SelectedItem=categories.Items.OfType<CategoryChoice>().Single(c=>c.Key=="rank");
            Check(!first.Token.IsCancellationRequested && history.Category=="rank","Changing the category keeps active downloading and applies the local filter");
            ((ListBox)main.FindName("PageTabs")).SelectedIndex=1;
            var chart=(ScoreChart)scores.FindName("Chart");Call(scores,"Chart_PointInvoked",chart,chart.Points.First());
            Check(!first.Token.IsCancellationRequested && detail.Row?.Battle.ReplayId==chart.Points.First().ReplayId,"Score point detail and switching pages retain the active download");
            ((ComboBox)scores.FindName("ScoreCount")).SelectedIndex=2;
            var queued=(Task)Call(main,"EnsureCurrentViewAsync")!;
            Check(!first.Token.IsCancellationRequested && !queued.IsCompleted,"A different score range waits behind the existing download instead of cancelling it");
            Check(((TextBlock)sync.ToolTip).Text.Contains("再次点击停止"),"The busy refresh tooltip advertises the click-to-stop action");
            var version=Get<int>("_viewLoadVersion");sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Check(first.Token.IsCancellationRequested && first.Work.Task.IsCompleted && queued.IsCompleted && Get<int>("_viewLoadVersion")>version && !Get<bool>("_busy"),"A second refresh click stops work and invalidates the queued range without restarting");
            Check(ring.Visibility==Visibility.Collapsed && ((TextBlock)main.FindName("NoticeText")).Text.Contains("已停止"),"An explicit stop restores the refresh icon and confirms saved records are retained");
            Check(store.Query(new(home.Id)).Count==3,"Stopping retains downloaded local matches");

            ((ListBox)main.FindName("PageTabs")).SelectedIndex=0;Pump();
            var waiting=Download();categories.SelectedItem=categories.Items.OfType<CategoryChoice>().Single(c=>c.Key=="casual");
            var earlier=(Task)Call(main,"EnsureCurrentViewAsync")!;
            categories.SelectedItem=categories.Items.OfType<CategoryChoice>().Single(c=>c.Key=="rank");
            var latest=(Task)Call(main,"EnsureCurrentViewAsync")!;
            Busy(false);waiting.Work.SetResult();Pump();
            Check(!waiting.Token.IsCancellationRequested && earlier.IsCompleted && latest.IsCompleted && history.Category=="rank","Queued category changes complete against the latest view without cancelling the old download");

            var same=Download();Complete((Task)Call(main,"ViewPlayerAsync",home)!);
            Complete((Task)Call(main,"SearchPlayerAsync",home.Id)!);
            Check(!same.Token.IsCancellationRequested,"Viewing or submitting the already displayed player does not interrupt that player's download");
            Complete((Task)Call(main,"ViewPlayerAsync",other)!);
            Check(same.Token.IsCancellationRequested && navigation.Current.Id==other.Id,"Switching players cancels the old download before changing the archive");
            var back=Download();MouseDown(main,MouseButton.XButton1);Pump();
            Check(back.Token.IsCancellationRequested && navigation.Current.Id==home.Id,"Mouse-side back cancels the active player's download and restores history");
            var forward=Download();Complete((Task)Call(main,"GoForwardAsync",1)!);
            Check(forward.Token.IsCancellationRequested && navigation.Current.Id==other.Id,"Forward navigation still cancels active downloading");
            var next=Download();Complete((Task)Call(main,"ViewPlayerAsync",third)!);
            Check(next.Token.IsCancellationRequested && navigation.Current.Id==third.Id,"A new player keeps normal navigation after repeated cancellations");
            var explicitStop=Download();Complete((Task)Call(main,"ToggleRefreshAsync")!);
            Check(explicitStop.Token.IsCancellationRequested && !Get<bool>("_busy"),"The shared button and Ctrl+R action stops rather than cancels-and-restarts");
            Check(App.DataDirectory=="" && !main.IsVisible && new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"Cancellation regression checks never show a window or access real user data");
        }
        finally
        {
            Set("_ready",false);Set("_cancellation",null);Busy(false);
            Call(main,"HideNotice");foreach(var token in tokens)token.Dispose();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        return count;
    }
}
