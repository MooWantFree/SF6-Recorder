using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Sf6Prototype;
using Localization=Sf6Prototype.Localization;

internal static class HeaderRefreshChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var main=new MainWindow();main.ApplyTemplate();
        void Set(string name,object value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(main,value);
        void Call(string name,params object[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(main,args);
        var model=(RefreshProgressState)typeof(MainWindow).GetField("_refreshProgress",flags)!.GetValue(main)!;
        var label=(TextBlock)main.FindName("UpdatedLabel");var bar=(ProgressBar)main.FindName("HeaderRefreshProgress");
        var toast=(StatusToast)main.FindName("Toast");
        const string owner="1234567890",other="2222222222",timestamp="更新于 11-15 12:34";
        Set("_headerViewOwner",owner);Set("_viewedUpdateText",timestamp);Call("UpdateHeaderRefreshStatus");
        var previousDirectory=App.DataDirectory;
        var dataDirectory=typeof(App).GetProperty(nameof(App.DataDirectory))!;
        try
        {
            Check(label.Text==timestamp && bar.Visibility==Visibility.Hidden,"Idle headers retain their viewed player's update time");
            void Start()
            {Call("SetBusy",true,false,false);Call("RequestProgress",new RequestActivity(RequestPhase.Sending));}
            Call("SetBusy",true,true,false);
            Check(label.Text==timestamp && bar.Visibility==Visibility.Hidden,"Local work does not expose header progress");
            Call("RequestProgress",new RequestActivity(RequestPhase.Sending));
            Check(label.Text.Contains("已用") && bar.Visibility==Visibility.Visible && bar.IsIndeterminate,
                "Sending the first request exposes progress and elapsed time without hover");
            model.Load(new("读取排位",.5,3,4,""),1);Call("UpdateButtonProgress");
            Check(label.Text.StartsWith("约剩 ") && !bar.IsIndeterminate && Math.Abs(bar.Value-model.Fraction!.Value)<.001,
                "Header text and the thin bar reflect the same estimated progress as the refresh ring");
            Call("RecordRefreshResult",owner,4);Call("FinishButtonProgress",true,false);Call("SetBusy",false,false,false);
            Check(label.Text==Localization.Format(Strings.NewMatchesCount,4) && toast.Visibility==Visibility.Collapsed,
                "Successful refreshes show their inserted count in the header without a completion toast");
            Call("HideButtonProgress");
            Check(label.Text.Contains("4") && bar.Visibility==Visibility.Hidden,"Inserted-count feedback survives the progress ring's completion flash");
            Call("SetBusy",true,true,false);
            Check(label.Text==Localization.Format(Strings.NewMatchesCount,4) && bar.Visibility==Visibility.Hidden,
                "Following local pagination preserves the preceding network refresh's inserted-count feedback");
            Call("SetBusy",false,false,false);
            Call("ClearRefreshResult");Check(label.Text==timestamp,"Expiring inserted-count feedback restores the current viewed timestamp");
            Start();Call("RecordRefreshResult",owner,0);Call("FinishButtonProgress",true,false);Call("SetBusy",false,false,false);
            Check(label.Text==timestamp && toast.Visibility==Visibility.Collapsed,"A refresh with no insertions finishes quietly");
            Start();Call("RecordRefreshResult",other,9);Call("FinishButtonProgress",true,false);Call("SetBusy",false,false,false);
            Check(label.Text==timestamp,"A background account's inserted count cannot be attributed to a different viewed player");
            Set("_headerViewOwner",other);Call("UpdateHeaderRefreshStatus");
            Check(label.Text==Localization.Format(Strings.NewMatchesCount,9),"The completion count follows its own account when it is viewed");
            Start();Call("RecordRefreshResult",other,7);Call("FinishButtonProgress",false,true);Call("SetBusy",false,false,false);
            Check(label.Text==timestamp && bar.Visibility==Visibility.Hidden,"Cancellation cannot present a successful inserted-count result");
            foreach(var stage in new[]{"error","login_required","sync_complete_scores_pending"})
            {
                // Each status write uses a synthetic directory and cannot touch live sessions.
                var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);dataDirectory.SetValue(null,root);
                Call("SetStatus",stage,"Synthetic refresh failure");
                Check(toast.Visibility==Visibility.Visible,$"Refresh failure feedback remains visible for {stage}");
                if(stage=="login_required") Check(((Button)toast.FindName("ActionButton")).Visibility==Visibility.Visible,
                    "Expired sign-in retains an actionable sign-in button");
                toast.Dismiss();
            }
            Check(new WindowInteropHelper(main).Handle==IntPtr.Zero,"Header status checks do not create a native window or issue requests");
        }
        finally
        {
            Call("ClearRefreshResult");Call("HideButtonProgress");
            dataDirectory.SetValue(null,previousDirectory);
        }
        return count;
    }
}
