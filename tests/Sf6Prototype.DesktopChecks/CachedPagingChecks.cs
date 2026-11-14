using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Sf6Prototype;

internal static class CachedPagingChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var previousContext=SynchronizationContext.Current;
        var main=new MainWindow();main.ApplyTemplate();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        void Set(string field,object value)=>typeof(MainWindow).GetField(field,flags)!.SetValue(main,value);
        object? Call(string method,params object[] args)=>typeof(MainWindow).GetMethod(method,flags)!.Invoke(main,args);
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"cached-paging.sqlite"));
        const string owner="1234567890";
        store.SavePage(Enumerable.Range(0,30).Select(i=>new Battle {OwnerId=owner,PlayerName="缓存玩家",ReplayId=$"CACHE-{i}",Category="rank",
            CharacterId=1,Character="隆",OpponentCharacter="春丽",PlayedAt=2000-i,Lp=10000+i,MyRounds=2,OpponentRounds=1}));
        foreach(var category in BattleCategory.All.Select(c=>c.Key).Prepend(""))store.CompleteCategory(owner,category,null);
        var profiles=new ProfileCache(root);var archive=new Archive(store,profiles);
        Set("_store",store);Set("_profiles",profiles);Set("_archive",archive);Set("_vault",new CookieVault(root));
        Set("_settings",new ConnectionSettings {PlayerId=owner,RefreshReminderSeconds=0});
        ((PlayerNavigation)typeof(MainWindow).GetField("_navigation",flags)!.GetValue(main)!).SetHome(new(owner,"缓存玩家"));
        var history=(HistoryView)main.FindName("History");history.Bind(archive);
        ((ScoresView)main.FindName("Scores")).Bind(archive);((MatchDetailPanel)main.FindName("Detail")).Bind(archive);
        Set("_ready",true);Call("ReloadLocal",false,false,null!,false);
        var list=(ListBox)history.FindName("MatchesList");var ring=(RefreshProgressGlyph)main.FindName("SyncRing");
        var coordinator=(SyncCoordinator)typeof(MainWindow).GetField("_sync",flags)!.GetValue(main)!;
        var requests=0;coordinator.RequestChanged+=_=>requests++;
        var showedProgress=false;
        var visibility=DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty,typeof(RefreshProgressGlyph));
        EventHandler observe=(_,_)=>showedProgress|=ring.Visibility==Visibility.Visible;
        visibility.AddValueChanged(ring,observe);
        try
        {
            Check(list.Items.OfType<MatchListRow>().Count()==20 && !history.Pager!.MoreNeedsNetwork(history.Filtered,""),
                "The fixture has twenty visible matches and another local page ready without a session");
            var work=(Task)Call("BeginLoadAsync",false,true,false)!;
            var deadline=DateTime.UtcNow.AddSeconds(10);
            do {main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);Thread.Yield();} while(!work.IsCompleted && DateTime.UtcNow<deadline);
            Check(work.IsCompleted,"Cached pagination completes without a network source or login window");work.GetAwaiter().GetResult();
            Check(list.Items.OfType<MatchListRow>().Count()==30,"Loading more actually appends the cached page");
            Check(requests==0 && !showedProgress && ring.Visibility==Visibility.Collapsed,
                "A complete cached load never exposes the refresh animation, including a completion flash");
            Check(!coordinator.IsBusy && ((Button)main.FindName("CancelButton")).Visibility==Visibility.Hidden,
                "Cached pagination releases busy state without exposing a cancel action");
            Check(App.DataDirectory=="" && new WindowInteropHelper(main).Handle==IntPtr.Zero,
                "Cached paging checks use synthetic data and never open the application or a native window");
        }
        finally
        {
            visibility.RemoveValueChanged(ring,observe);Set("_ready",false);Call("HideButtonProgress");
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        return count;
    }
}
