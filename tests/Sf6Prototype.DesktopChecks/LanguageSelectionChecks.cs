using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sf6Prototype;
using Localization=Sf6Prototype.Localization;

internal static class LanguageSelectionChecks
{
    public static int Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var main=new MainWindow();main.ApplyTemplate();
        object? Call(string name,params object?[] args)=>typeof(MainWindow).GetMethod(name,flags)!.Invoke(main,args);
        void Set(string name,object? value)=>typeof(MainWindow).GetField(name,flags)!.SetValue(main,value);
        T Get<T>(string name)=>(T)typeof(MainWindow).GetField(name,flags)!.GetValue(main)!;
        void Pump()=>main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
        var dataDirectory=typeof(App).GetProperty(nameof(App.DataDirectory))!;var previousDirectory=App.DataDirectory;
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        var store=new BattleStore(Path.Combine(root,"archive.sqlite"));var profiles=new ProfileCache(root);var archive=new Archive(store,profiles);
        const string owner="1234567890";
        var committed=new ConnectionSettings {PlayerId=owner,Language="zh-Hans",UseProxy=false,ApiIntervalSeconds=2,RefreshAfterMatch=false};
        committed.Save(root);dataDirectory.SetValue(null,root);
        Set("_settings",committed);Set("_store",store);Set("_profiles",profiles);Set("_archive",archive);
        Set("_vault",new CookieVault(root));Set("_playerSearch",new PlayerSearchHistory(root));
        Get<PlayerNavigation>("_navigation").SetHome(new(owner,"Fixture"));
        ((HistoryView)main.FindName("History")).Bind(archive);((ScoresView)main.FindName("Scores")).Bind(archive);
        ((MatchDetailPanel)main.FindName("Detail")).Bind(archive);
        Set("_ready",true);Call("ReloadLocal",false,true,null,false);
        var held=new TaskCompletionSource();var token=CancellationToken.None;
        var coordinator=Get<SyncCoordinator>("_sync");
        var work=coordinator.RunAsync(new(SyncOperation.ManualRefresh),async context=>{token=context.Token;await held.Task;context.Complete();});
        // A headless WPF window cannot own another window until its native handle exists.
        // Use the production callback with an unowned settings window to keep this test offline.
        var apply=typeof(MainWindow).GetMethod("ChangeLanguage",flags)!.CreateDelegate<Action<string>>(main);
        var dialog=new SettingsWindow(null!,committed) {ChangeLanguage=apply};
        var language=(ComboBox)dialog.FindName("LanguageBox");var api=(TextBox)dialog.FindName("ApiIntervalBox");
        var proxy=(TextBox)dialog.FindName("ProxyAddressBox");var enabled=(CheckBox)dialog.FindName("ProxyEnabled");
        var save=(Button)dialog.FindName("SaveButton");var error=(TextBlock)dialog.FindName("ErrorText");
        var toast=(StatusToast)main.FindName("Toast");var ring=(RefreshProgressGlyph)main.FindName("SyncRing");
        var applied=0;
        dialog.ChangeLanguage=value=>{apply(value);applied++;};
        try
        {
            Check(applied==0 && ConnectionSettings.Load(root)==committed,"Opening settings does not apply a language selection");
            api.Text="unfinished";api.Select(2,3);enabled.IsChecked=true;proxy.Text="invalid proxy draft";
            foreach(var (culture,title,label) in new[]{("ja","設定","保存"),("en","Settings","Save"),("zh-Hant","設定","儲存"),("zh-Hans","设置","保存")})
            {
                language.SelectedValue=culture;Pump();
                Check(ConnectionSettings.Load(root)==committed with {Language=culture} && Get<ConnectionSettings>("_settings").Language==culture,
                    "Selecting a language persists only that preference and updates the active application immediately");
                Check(Localization.Culture.Name==culture && dialog.Title==title && save.Content?.ToString()==label && dialog.Result==null,
                    "The open settings window translates before Save is clicked");
                Check(api.Text=="unfinished" && api.SelectionStart==2 && api.SelectionLength==3 && enabled.IsChecked==true && proxy.Text=="invalid proxy draft",
                    "Incomplete unsaved fields and their selection survive language changes");
                Check(!token.IsCancellationRequested && !work.IsCompleted && coordinator.Active!=null && ring.Visibility==Visibility.Visible,
                    "Immediate language changes preserve the active refresh and its visible progress");
                Check(toast.Visibility==Visibility.Collapsed && !((StatusToast)dialog.FindName("UpdateToast")).IsLoading,
                    "Changing language starts no role-name download or confirmation toast");
            }
            language.SelectedValue="system";Pump();
            Check(ConnectionSettings.Load(root).Language=="system" && Localization.Culture.Name==Localization.Resolve("system",Localization.SystemCulture).Name,
                "Selecting the system default immediately resolves the original Windows UI language");
            var saves=applied;language.SelectedValue="system";Pump();Check(applied==saves,"Reselecting the current language does not save again");
            var before=File.ReadAllText(Path.Combine(root,"settings.json"));
            dialog.ChangeLanguage=_=>throw new IOException("fixture");language.SelectedValue="ja";Pump();
            Check(language.SelectedValue?.ToString()=="system" && File.ReadAllText(Path.Combine(root,"settings.json"))==before && error.Text==Strings.LanguageSaveFailed,
                "A failed language save restores the selection, preserves preferences and reports the failure inline");
            dialog.ChangeLanguage=apply;
            api.Text="3";enabled.IsChecked=false;
            var draft=(ConnectionSettings)typeof(SettingsWindow).GetMethod("Draft",flags)!.Invoke(dialog,[true])!;
            Check(draft.Language=="system" && draft.ApiIntervalSeconds==3 && ConnectionSettings.Load(root).ApiIntervalSeconds==2,
                "Other settings remain drafts for the existing Save action after an immediate language change");
            dialog.Close();
            var reopened=new SettingsWindow(null!,ConnectionSettings.Load(root));
            Check(((ComboBox)reopened.FindName("LanguageBox")).SelectedValue?.ToString()=="system" && ConnectionSettings.Load(root).ApiIntervalSeconds==2,
                "Closing without Save retains the language and discards unrelated draft edits");
            reopened.Close();
            Call("SetStatus","profile_pending",Strings.SessionSavedImagesPending);
            Check(toast.Visibility==Visibility.Collapsed,"Optional portrait failures do not display a toast");
            Call("SetStatus","sync_complete_scores_pending","");
            Check(toast.Visibility==Visibility.Visible && ((TextBlock)toast.FindName("MessageText")).Text==Strings.RecordsUpdatedScoresPending,
                "A failed current-score update is identified separately from a portrait failure");
            Check(new System.Windows.Interop.WindowInteropHelper(main).Handle==IntPtr.Zero,
                "Language-selection checks never open the real application or a native window");
        }
        finally
        {
            held.TrySetResult();
            var deadline=DateTime.UtcNow.AddSeconds(5);while(!work.IsCompleted && DateTime.UtcNow<deadline)Pump();
            work.GetAwaiter().GetResult();
            toast.Dismiss();Call("HideButtonProgress");Set("_ready",false);
            dataDirectory.SetValue(null,previousDirectory);SynchronizationContext.SetSynchronizationContext(previousContext);
            LocalizationWpf.Apply("zh-Hans");
        }
        return count;
    }
}
