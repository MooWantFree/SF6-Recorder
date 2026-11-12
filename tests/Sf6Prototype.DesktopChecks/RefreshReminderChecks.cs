using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sf6Prototype;

internal static class RefreshReminderChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var main=new MainWindow();main.ApplyTemplate();
        var button=(Button)main.FindName("SyncButton");button.ApplyTemplate();
        var icon=(TextBlock)main.FindName("SyncIcon");
        bool ColorIs(Brush brush,string key)=>((SolidColorBrush)brush).Color==((SolidColorBrush)main.FindResource(key)).Color;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string field,object? value)=>typeof(MainWindow).GetField(field,flags)!.SetValue(main,value);
        void Call(string method,params object?[] args)=>typeof(MainWindow).GetMethod(method,flags)!.Invoke(main,args);
        void Pump()=>main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
        void Configure(int seconds)
        {
            Set("_settings",new ConnectionSettings {RefreshReminderSeconds=seconds});
            Call("ConfigureRefreshReminder");Pump();
        }
        Set("_ready",true);Set("_viewingActiveAccount",true);Set("_lastViewedUpdate",DateTimeOffset.UtcNow);
        try
        {
            Configure(1);
            Check(!main.IsRefreshOverdue && !icon.RenderTransform.HasAnimatedProperties && ColorIs(button.Foreground,"Ink2"),"Fresh data leaves the refresh icon still and neutral");
            Set("_lastViewedUpdate",DateTimeOffset.UtcNow.AddSeconds(-5));
            var frame=new DispatcherFrame();
            var wait=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(1200)};
            wait.Tick+=(_,_)=>{wait.Stop();frame.Continue=false;};
            wait.Start();Dispatcher.PushFrame(frame);
            Check(main.IsRefreshOverdue && main.IsRefreshReminderAnimated==SystemParameters.ClientAreaAnimation,"The running timer detects expiry while motion respects the system setting");
            Check(ColorIs(button.Background,"SurfaceSelected") && ColorIs(icon.Foreground,"AccentText"),"Overdue reminders keep their themed background and icon color even with reduced motion");

            // Exercise motion on systems with reduced motion, too. Seeking the clock
            // started by the real style verifies it without a native render window.
            main.SetValue(MainWindow.IsRefreshReminderAnimatedProperty,true);Pump();
            Check(icon.RenderTransform.HasAnimatedProperties && button.RenderTransform.Value.IsIdentity,"The overdue state animates the icon without moving the button's background or hit area");
            var trigger=icon.Style.Triggers.OfType<DataTrigger>().Single();
            var motion=trigger.EnterActions.OfType<BeginStoryboard>().Single().Storyboard;
            void Seek(double milliseconds)=>motion.SeekAlignedToLastTick(icon,TimeSpan.FromMilliseconds(milliseconds),TimeSeekOrigin.BeginTime);
            bool MotionRemoved()
            {
                try {motion.GetCurrentState(icon);return false;}
                catch(InvalidOperationException) {return true;}
            }
            Seek(100);
            Check(Math.Abs(((RotateTransform)icon.RenderTransform).Angle+25)<.01,"The reminder visibly rotates the icon to the left");
            Seek(200);
            Check(Math.Abs(((RotateTransform)icon.RenderTransform).Angle-25)<.01,"The reminder then rotates the icon to the right");
            Seek(1500);
            Check(((RotateTransform)icon.RenderTransform).Angle==0 && ColorIs(button.Background,"SurfaceSelected"),"The icon rests between bursts while the overdue highlight stays visible");
            Seek(3100);
            Check(Math.Abs(((RotateTransform)icon.RenderTransform).Angle+25)<.01,"The reminder repeats after the three-second cycle");

            Call("SetBusy",true,false,false);Pump();
            Check(!main.IsRefreshOverdue && MotionRemoved() && ColorIs(button.Foreground,"Ink2") && ((SolidColorBrush)button.Background).Color.A==0,"Downloading removes the reminder's animation and highlight");
            Call("SetBusy",false,false,false);Pump();
            Check(main.IsRefreshOverdue && main.IsRefreshReminderAnimated==SystemParameters.ClientAreaAnimation && ColorIs(button.Background,"SurfaceSelected"),"Ending a download without fresh data restores the overdue reminder");
            Set("_lastViewedUpdate",DateTimeOffset.UtcNow);Call("UpdateRefreshReminder");Pump();
            Check(!main.IsRefreshOverdue && MotionRemoved(),"A successful update clears the running reminder");
            Set("_lastViewedUpdate",DateTimeOffset.UtcNow.AddHours(-2));Configure(600);
            Check(main.IsRefreshOverdue,"Applying an interval to already stale data immediately updates the reminder");
            Configure(0);
            Check(!main.IsRefreshOverdue && MotionRemoved(),"Disabling reminders stops the animation even with old data");
            Check(App.DataDirectory=="" && new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"Reminder checks use no real data or native windows");
        }
        finally {Set("_ready",false);Configure(0);Call("SetBusy",false,false,false);}
        return count+AccountScopeChecks();
    }

    private static int AccountScopeChecks()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var main=new MainWindow();main.ApplyTemplate();
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Set(string field,object? value)=>typeof(MainWindow).GetField(field,flags)!.SetValue(main,value);
        void Call(string method)=>typeof(MainWindow).GetMethod(method,flags)!.Invoke(main,null);
        var root=System.IO.Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var path=System.IO.Path.Combine(root,"reminders.sqlite");
        var store=new BattleStore(path);var profiles=new ProfileCache(root);var archive=new Archive(store,profiles);var vault=new CookieVault(root);
        var account=new PlayerIdentity("1234567890","当前账号");var second=new PlayerIdentity("2222222222","另一个账号");var other=new PlayerIdentity("3333333333","其他玩家");
        foreach(var player in new[]{account,second,other})store.FinishRun(store.BeginRun(player.Id),"completed",0,0,"");
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            db.Open();using var command=db.CreateCommand();
            command.CommandText="UPDATE sync_runs SET finished_at=$time";
            command.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.AddHours(-2).ToString("O"));command.ExecuteNonQuery();
        }
        // Synthetic empty sessions live only in the test directory; no client is opened.
        vault.Save(new(second.Id,second.Name,"","","",[]));vault.Save(new(account.Id,account.Name,"","","",[]));
        Set("_store",store);Set("_profiles",profiles);Set("_archive",archive);Set("_vault",vault);
        Set("_settings",new ConnectionSettings {PlayerId=account.Id,RefreshReminderSeconds=600});
        var navigation=(PlayerNavigation)typeof(MainWindow).GetField("_navigation",flags)!.GetValue(main)!;
        navigation.SetHome(account);
        var history=(HistoryView)main.FindName("History");history.Bind(archive);
        ((ScoresView)main.FindName("Scores")).Bind(archive);((MatchDetailPanel)main.FindName("Detail")).Bind(archive);
        var button=(Button)main.FindName("SyncButton");button.ApplyTemplate();
        void Show(PlayerIdentity player,int page=0,PlayerIdentity? favorite=null)
        {
            // Change only local views with request-producing handlers disabled.
            Set("_ready",false);navigation.Visit(player);archive.Load(player.Id,refreshImages:false);
            history.Reload(restore:new() {FavoritesOnly=favorite!=null,FavoritePlayer=favorite});
            ((ListBox)main.FindName("PageTabs")).SelectedIndex=page;
            Set("_ready",true);Call("RefreshViewedPlayerUi");
            main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
        }
        bool NoReminder()=>!main.IsRefreshOverdue && !main.IsRefreshReminderAnimated && ((SolidColorBrush)button.Background).Color.A==0;
        try
        {
            Show(account);Check(main.IsRefreshOverdue,"The selected signed-in account receives its overdue reminder");
            Show(other);Call("UpdateRefreshReminder");
            Check(NoReminder() && ((TextBlock)main.FindName("UpdatedLabel")).Text.StartsWith("更新于 "),"Other players retain their update timestamp but no highlight or motion, including later timer ticks");
            Show(second);Check(NoReminder(),"An inactive saved login does not qualify as the currently selected account");
            Show(account,page:1);Check(main.IsRefreshOverdue,"The active account's score page retains its reminder");
            Show(other,page:1);Check(NoReminder(),"Another player's score page never reminds");
            Show(account,favorite:other);Check(NoReminder(),"Another player's favorites cannot inherit the home account's reminder");
            Show(account,favorite:account);Check(main.IsRefreshOverdue,"Returning to the active account's favorites restores its overdue state");
            vault.Activate(second.Id);navigation.SetHome(second);
            Set("_settings",new ConnectionSettings {PlayerId=second.Id,RefreshReminderSeconds=600});
            Show(account);Check(NoReminder(),"Switching accounts removes reminder eligibility from the previously active account");
            Show(second);Check(main.IsRefreshOverdue,"Switching accounts applies the newly active account's own overdue timestamp");
            store.FinishRun(store.BeginRun(second.Id),"completed",0,0,"");Show(second);
            Check(NoReminder(),"Fresh data for the selected account clears its reminder while other accounts remain stale");
            vault.Remove(second.Id);Show(account);Check(NoReminder(),"No selected login means no reminder even when a remembered account has stale data");
            Check(App.DataDirectory=="" && new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,"Account-scope checks never start the app or use the real session");
        }
        finally {Set("_ready",false);Call("UpdateRefreshReminder");}
        return count;
    }
}
