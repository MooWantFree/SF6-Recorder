using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsRefreshOverdueProperty=DependencyProperty.Register(nameof(IsRefreshOverdue),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool IsRefreshOverdue { get=>(bool)GetValue(IsRefreshOverdueProperty); private set=>SetValue(IsRefreshOverdueProperty,value); }
    public static readonly DependencyProperty IsRefreshReminderAnimatedProperty=DependencyProperty.Register(nameof(IsRefreshReminderAnimated),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool IsRefreshReminderAnimated { get=>(bool)GetValue(IsRefreshReminderAnimatedProperty); private set=>SetValue(IsRefreshReminderAnimatedProperty,value); }
    public static readonly DependencyProperty IsPlayerSearchActiveProperty=DependencyProperty.Register(nameof(IsPlayerSearchActive),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool IsPlayerSearchActive { get=>(bool)GetValue(IsPlayerSearchActiveProperty); private set=>SetValue(IsPlayerSearchActiveProperty,value); }
    private BattleStore _store = null!;
    private CookieVault _vault = null!;
    private ProfileCache _profiles = null!;
    private CharacterNameUpdater? _characterNames;
    private Archive _archive = null!;
    private ConnectionSettings _settings = new();
    private bool _ready, _busy, _closeRequested, _reloadingLocal, _restoringView;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _imageCancellation;
    private Task? _imageTask;
    private Task _foregroundTask = Task.CompletedTask;
    private int _navigationVersion;
    private int _pendingPlayerVersion;
    private int _viewLoadVersion;
    private bool _stoppingRefresh,_loadingInBackground;
    private bool _silentCancellation;
    private readonly List<ImageDownload> _pendingImages=[];
    private readonly Dictionary<string,BattlePager> _pagers=[];
    private BattlePager Pager => _pagers.TryGetValue(_navigation.Current.Id,out var pager) ? pager : _pagers[_navigation.Current.Id]=new(_store,_navigation.Current.Id);
    private SyncResult? _lastSync;
    private bool _savingSettings;
    private readonly DispatcherTimer _refreshReminderTimer = new() {Interval=TimeSpan.FromSeconds(30)};
    private readonly DispatcherTimer _autoRefreshTimer = new();
    private readonly AutoRefreshSchedule _autoRefresh = new();
    private DateTimeOffset? _lastViewedUpdate;
    private bool _viewingActiveAccount;
    private readonly Dictionary<string, OpponentWindow> _opponents = [];
    private readonly PlayerNavigation _navigation = new();
    private PlayerSearchHistory _playerSearch=null!;
    private TrayIcon? _tray;
    private bool _searchingPlayer,_exitRequested,_exitReady,_showingCloseChoice;
    private string? _lookupUid;
    private WindowState _restoreState=WindowState.Normal;
    private readonly List<Window> _hiddenOwnedWindows=[];

    public MainWindow()
    {
        InitializeComponent();
        InitializeWindowChrome();
        InitializeRefreshProgress();
        SourceInitialized+=(_,_)=> { if(!App.Capture) _tray=new TrayIcon(this,ShowMainPanel,()=>_=ExitApplicationAsync()); };
        StateChanged+=(_,_)=> { if(WindowState!=WindowState.Minimized) _restoreState=WindowState; };
        if (App.StartSize is { } size) { Width = size.Width; Height = size.Height; }
        AccountPopup.Opened += (_, _) => UiCapture.SaveLater((FrameworkElement)AccountPopup.Child, "ui-account-menu.png");
        PlayerSearchPopup.Closed+=(_,_)=>{IsPlayerSearchActive=false;if(_searchingPlayer) RequestCancellation();};
        Deactivated+=(_,_)=>ClosePlayerSearch();
        Loaded += async (_, _) => await InitializeAsync();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key==Key.BrowserBack || Keyboard.Modifiers == ModifierKeys.Alt && (e.Key == Key.Left || e.SystemKey == Key.Left))
            { e.Handled = true; await GoBackAsync(); return; }
            if (e.Key==Key.BrowserForward || Keyboard.Modifiers == ModifierKeys.Alt && (e.Key == Key.Right || e.SystemKey == Key.Right))
            { e.Handled=true;await GoForwardAsync();return; }
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.F) { PageTabs.SelectedIndex = 0; _ = Dispatcher.BeginInvoke(new Action(History.FocusSearch), DispatcherPriority.Input); e.Handled = true; }
            else if (e.Key == Key.R) { e.Handled = true; await ToggleRefreshAsync(); }
            else if (e.Key == Key.OemComma) { e.Handled = true; OpenSettings(); }
            else if (e.Key == Key.K) { e.Handled=true; OpenPlayerSearch(); }
        };
        _refreshReminderTimer.Tick += (_,_)=>UpdateRefreshReminder();
        _autoRefreshTimer.Tick += AutoRefresh_Tick;
        History.SelectionChanged += row => { if (!_reloadingLocal && PageTabs.SelectedIndex == 0) {if(History.ActiveArchive is { } archive) Detail.Bind(archive);Detail.Show(row);if(_ready) RefreshViewedPlayerUi();} };
        Scores.SelectionChanged += row => { if (!_reloadingLocal && PageTabs.SelectedIndex == 1) {Detail.Bind(_archive);Detail.Show(row);} };
        Detail.RanksRequested += OpenPlayerRanks;
        Detail.PlayerRequested += player => _ = ViewPlayerAsync(player);
        Detail.Notice += message => ShowNotice(message);
        History.MoreRequested += () => _ = BeginLoadAsync(more:true);
        History.CategoryChanged += () => _ = CategoryChangedAsync();
        History.PlayerChanged += RequestCancellation;
        History.TagsChanged += ()=>ReloadLocal(preserveViewport:true,refreshImages:false);
        Scores.DataRequested += () => _ = EnsureScoreRangeAsync();
        PreviewMouseDown += async (_,e) =>
        {
            if(e.ChangedButton==MouseButton.XButton1)
            { e.Handled=true; await GoBackAsync(); return; }
            if(e.ChangedButton==MouseButton.XButton2)
            {e.Handled=true;await GoForwardAsync();return;}
            var origin=e.OriginalSource as DependencyObject;
            if(IsSearchElement(origin)) {ActivatePlayerSearch();return;}
            ClosePlayerSearch();
        };
    }
    private async Task InitializeAsync()
    {
        _settings = ConnectionSettings.Load(App.DataDirectory);
        RequestGate.Shared.Interval=TimeSpan.FromSeconds(_settings.ApiIntervalSeconds);
        _vault = new CookieVault(App.DataDirectory);
        _playerSearch=new PlayerSearchHistory(App.DataDirectory);
        _profiles = new ProfileCache(App.DataDirectory);
        _characterNames=new(_profiles.Names,()=>
        {
            var active=_vault.Load();
            return active==null ? null : (new BucklerClient(_settings with {PlayerId=active.AccountId},active,_vault),active.AccountId);
        },async token=>
        {
            await Task.Delay(400,token);
            while(_busy || _savingSettings || _opponents.Values.Any(w=>w.QueryInProgress)) await Task.Delay(200,token);
        },RefreshCharacterNames);
        _vault.Accounts();
        _store = new BattleStore(Path.Combine(App.DataDirectory, "battles.sqlite"));
        _archive = new Archive(_store, _profiles); History.Bind(_archive); Scores.Bind(_archive); Detail.Bind(_archive);
        var session = _vault.Load();
        if (session != null) _settings = _settings with { PlayerId = session.AccountId };
        _navigation.SetHome(new(_settings.PlayerId, _profiles.Profile(_settings.PlayerId)?.Name ?? session?.AccountName ?? ""));
        RefreshAccountUi();
        _ready = true; ReloadLocal();
        ConfigureRefreshReminder();
        ConfigureAutoRefresh();
        SetStatus("ready", Strings.ReadyLocalData);
        if (App.AutoSync) await SynchronizeAsync();
        if (App.Capture) await CaptureAllAsync();
    }
    private void SetBusy(bool busy,bool quiet=false,bool background=false)
    {
        _loadingInBackground=busy && background;
        _busy = busy; History.SetLoading(busy && !background); Scores.SetLoading(busy && !background && PageTabs.SelectedIndex==1);
        // Keep hover/keyboard tooltips available while the button displays progress.
        SyncButton.IsHitTestVisible = SyncButton.Focusable = true;
        var announce=busy && !quiet;
        if(announce) BeginButtonProgress();
        else if(busy || !_progressFinished) HideButtonProgress();
        UpdateRefreshAction();
        CancelButton.Visibility = announce ? Visibility.Visible : Visibility.Collapsed;
        foreach (var window in _opponents.Values) window.Suspended = busy;
        UpdateRefreshReminder();
    }
    private BucklerClient CreateClient(SavedSession session,string? owner=null)=>new(
        owner==null ? _settings : _settings with {PlayerId=owner},session,_vault) {CharacterObserver=ObserveCharacterNames};
    private void ObserveCharacterNames(CharacterObservation observation)
    {
        if(!App.Capture && !_closeRequested) _characterNames?.Observe(observation);
    }
    private void RefreshCharacterNames()
    {
        if(!_ready || _exitRequested) return;
        var restoring=_restoringView;var reloading=_reloadingLocal;var detail=Detail.Row;
        _restoringView=_reloadingLocal=true;
        try
        {
            History.RefreshLanguage();Scores.RefreshLanguage();Detail.RefreshLanguage(detail);
            foreach(var window in _opponents.Values) window.RefreshCharacterNames();
        }
        finally {_restoringView=restoring;_reloadingLocal=reloading;}
    }
    private void ConfigureRefreshReminder()
    {
        _refreshReminderTimer.Stop();
        _refreshReminderTimer.Interval=TimeSpan.FromSeconds(Math.Clamp(_settings.RefreshReminderSeconds/10d,1,30));
        UpdateRefreshReminder();
        if(_settings.RefreshReminderSeconds>0) _refreshReminderTimer.Start();
    }
    private void UpdateRefreshReminder()
    {
        IsRefreshOverdue=_ready && _viewingActiveAccount && !_busy && !_progressVisible && DateRanges.IsRefreshOverdue(_lastViewedUpdate,DateTimeOffset.UtcNow,_settings.RefreshReminderSeconds);
        IsRefreshReminderAnimated=IsRefreshOverdue && SystemParameters.ClientAreaAnimation;
    }

    private void ConfigureAutoRefresh()
    {
        _autoRefreshTimer.Stop();
        _autoRefresh.Configure(_settings.AutoRefreshSeconds,DateTimeOffset.UtcNow);
        if(_settings.AutoRefreshSeconds==0 || App.Capture) return;
        _autoRefreshTimer.Interval=TimeSpan.FromSeconds(Math.Clamp(_settings.AutoRefreshSeconds/10d,1,30));
        _autoRefreshTimer.Start();
    }
    private async void AutoRefresh_Tick(object? sender,EventArgs e)
    {
        var now=DateTimeOffset.UtcNow;
        if(!_autoRefresh.IsDue(now) || !_ready || _busy || _closeRequested || _restoringView ||
            !IsEnabled || OwnedWindows.Count>0 || AccountPopup.IsOpen || PlayerSearchPopup.IsOpen || App.Capture) return;
        try
        {
            // Snapshot the signed-in account, never the player currently being browsed.
            var session=_vault.Load();
            if(!_autoRefresh.TryBegin(now,session!=null)) return;
            await (_foregroundTask=AutoRefreshAccountAsync(session!));
        }
        catch(Exception ex) { _autoRefresh.Pause(); ReportError(ex); }
    }
    private async Task AutoRefreshAccountAsync(SavedSession session)
    {
        SetBusy(true,quiet:true,background:true);
        _cancellation=new CancellationTokenSource(); _silentCancellation=true;
        var token=_cancellation.Token;
        var owner=session.AccountId;
        try
        {
            await StopBackgroundDownloadsAsync();
            using var client=CreateClient(session,owner);
            await client.ValidateAsync(token);
            if(client.AccountId!=owner) throw new LoginRequiredException(Strings.AccountChanged);
            var refreshed=await AccountRefresh.RunAsync(_store,client,owner,token);
            token.ThrowIfCancellationRequested();
            _pagers[owner]=refreshed.Pager;
            // Publish the merged heads together, so categories cannot insert rows in turns.
            if(_navigation.Current.Id==owner) ReloadLocal(preserveViewport:true,refreshImages:false,automaticRefresh:true);
            try
            {
                var images=await _profiles.RefreshMetadataAsync(client,owner,token,captureScores:refreshed.Pager.RankHeadFetched);
                token.ThrowIfCancellationRequested(); QueueImages(images);
            }
            catch(Exception ex) when(ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not LoginRequiredException)
            { /* Keep the saved matches; portraits can be retried on the next refresh. */ }
            if(_navigation.Current.Id==owner) ReloadLocal(preserveViewport:true);
            RefreshAccountUi();
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex)
        {
            if(ex is LoginRequiredException) _autoRefresh.Pause();
            ReportError(ex);
        }
        finally
        {
            _autoRefresh.Restart(DateTimeOffset.UtcNow);
            _cancellation.Dispose(); _cancellation=null; SetBusy(false);
            History.ContinueAutoLoad();
            if(!token.IsCancellationRequested && !_closeRequested) StartImageDownloads([],refreshOwner:owner);
            if(_closeRequested) Close();
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e) { await CancelDownloadsAsync(); await LoginAsync(); }
    private Task LoginAsync()
    {
        if(_busy || _exitRequested) return Task.CompletedTask;
        return _foregroundTask=LoginCoreAsync();
    }
    private async Task LoginCoreAsync()
    {
        AccountPopup.IsOpen = false;
        SetBusy(true);
        _cancellation=new CancellationTokenSource(); var token=_cancellation.Token;
        try
        {
            await StopBackgroundDownloadsAsync();
            var session = await LoginWindow.SignInAsync(this, _settings, _vault);
            token.ThrowIfCancellationRequested();
            if (session == null) { SetStatus("login_cancelled", Strings.SignInCancelledLocal); return; }
            SetActiveAccount(session, preserveView: session.AccountId == _settings.PlayerId); ReloadLocal();
            using var client = CreateClient(session);
            try
            {
                var images = await _profiles.RefreshMetadataAsync(client, session.AccountId, token);
                token.ThrowIfCancellationRequested();
                StartImageDownloads(images);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException) { SetStatus("profile_pending", Strings.SessionSavedImagesPending); RefreshAccountUi(); return; }
            RefreshAccountUi(); ReloadLocal(); SetStatus("logged_in", Strings.SessionSaved);
        }
        catch (OperationCanceledException) when(token.IsCancellationRequested) { }
        catch (Exception ex) { ReportError(ex); }
        finally { _cancellation.Dispose(); _cancellation=null; SetBusy(false); if (_closeRequested) Close(); }
    }
    private void RefreshAccountUi()
    {
        var session = _vault.Load();
        var info = session == null ? null : _profiles.Profile(session.AccountId);
        AccountName.Text = info?.Name ?? session?.AccountName ?? Strings.SignIn;
        var picture = Pictures.Load(info?.AvatarPath, 96);
        AccountAvatar.Fill = picture == null ? Brushes.Transparent : new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        AccountsList.ItemsSource = _vault.Accounts().Select(s => new AccountChoice(s.AccountId, _profiles.Profile(s.AccountId)?.Name ?? s.AccountName,
            Pictures.Load(_profiles.Profile(s.AccountId)?.AvatarPath, 96), s.AccountId == session?.AccountId)).ToList();
    }
    private void SetActiveAccount(SavedSession session, bool preserveView = false)
    {
        _autoRefresh.Resume(DateTimeOffset.UtcNow);
        _imageCancellation?.Cancel();
        var preserveContext=preserveView && session.AccountId==_settings.PlayerId;
        _settings = _settings with { PlayerId = session.AccountId }; _settings.Save(App.DataDirectory);
        _navigation.SetHome(new(session.AccountId, _profiles.Profile(session.AccountId)?.Name ?? session.AccountName), preserveView);
        if(!preserveContext) { History.ResetFilters(); Scores.ResetSelection(); }
        CloseOpponents();
        RefreshAccountUi();
    }
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_vault.Load() == null && _vault.Accounts().Count == 0) { Login_Click(sender, e); return; }
        RefreshAccountUi();
        var surface=(FrameworkElement)AccountPopup.Child;
        AccountPopup.HorizontalOffset = AccountButton.ActualWidth - surface.Width - surface.Margin.Left;
        AccountPopup.IsOpen = !AccountPopup.IsOpen;
    }
    private async void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        if(_vault.Load()?.AccountId==id) {AccountPopup.IsOpen=false;return;}
        var version=++_navigationVersion; await CancelDownloadsAsync(); if(version!=_navigationVersion) return;
        try { SetActiveAccount(_vault.Activate(id)); AccountPopup.IsOpen = false; ReloadLocal(); SetStatus("account_switched", Strings.AccountSwitched); }
        catch (Exception ex) { ReportError(ex); }
    }
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        var session = _vault.Accounts().FirstOrDefault(s => s.AccountId == id); if (session == null) return;
        AccountPopup.IsOpen = false;
        if (MessageBox.Show(this, Localization.Format(Strings.SignOutConfirmation, session.AccountName), Strings.SignOutAccount, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var version=++_navigationVersion; await CancelDownloadsAsync(); if(version!=_navigationVersion) return;
        _imageCancellation?.Cancel();
        var active = _vault.Load()?.AccountId == id; _vault.Remove(id); CloseOpponents();
        if (active && _vault.Accounts().FirstOrDefault() is { } next) SetActiveAccount(_vault.Activate(next.AccountId));
        else if (active) { _navigation.SetHome(_navigation.Home); History.ResetFilters(); Scores.ResetSelection(); }
        RefreshAccountUi(); ReloadLocal(); SetStatus("logged_out", Strings.SignedOutLocalKept);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private async void OpenSettings()
    {
        if (!_ready || _exitRequested || _savingSettings) return;
        var dialog = CreateSettingsWindow();
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        _savingSettings=true;
        try
        {
            var settings=dialog.Result with {PlayerId=_settings.PlayerId}; settings.Save(App.DataDirectory); _settings=settings;
            RequestGate.Shared.Interval=TimeSpan.FromSeconds(_settings.ApiIntervalSeconds);
            ConfigureRefreshReminder();
            ConfigureAutoRefresh();
            var language=Localization.Resolve(_settings.Language,Localization.SystemCulture).Name;
            var languageChanged=language!=Localization.Culture.Name;
            if(languageChanged)
            {
                await CancelDownloadsAsync();
                if(_exitRequested) return;
                ApplyLanguage();
            }
            SetStatus("settings_saved", _settings.UseProxy ? Strings.SettingsSavedProxy : Strings.SettingsSavedDirect);
            var roster=_profiles.Characters.Select(c=>(c.Id,c.Name,c.ToolName)).ToList();
            if(languageChanged && !App.Capture && _profiles.Names.NeedsLanguage(language,roster))
            {
                var session=_vault.Load();
                if(session==null)
                    ShowNotice(Strings.SavedCharacterNamesSignIn,false,Strings.SignIn,()=>_=LoginAsync());
                else
                    await (_foregroundTask=LoadLanguageNamesAsync(session,language,roster));
            }
        }
        catch (Exception ex) { ReportError(ex); }
        finally {_savingSettings=false;}
    }

    private async Task LoadLanguageNamesAsync(SavedSession session,string language,
        IReadOnlyList<(int Id,string Name,string ToolName)> roster)
    {
        SetBusy(true,quiet:true,background:true);
        _cancellation=new CancellationTokenSource();var token=_cancellation.Token;
        Toast.BeginProgress(Strings.SavedLoadingCharacterNames);
        try
        {
            using var client=CreateClient(session,session.AccountId);
            client.Activity+=activity=>Toast.UpdateProgress(activity.Phase==RequestPhase.Waiting
                ? Strings.SavedWaitingCharacterNames : Strings.SavedLoadingCharacterNames);
            await _profiles.Names.EnsureLanguageAsync(client,session.AccountId,language,roster,token);
            token.ThrowIfCancellationRequested();
            ApplyLanguage();
            Toast.CompleteProgress(Strings.SavedCharacterNamesReady);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {Toast.CompleteProgress(Strings.SavedCharacterNamesCancelled);}
        catch(LoginRequiredException)
        {Toast.CompleteProgress(Strings.SavedCharacterNamesSignIn,true,Strings.SignInAgain,()=>_=LoginAsync());}
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {Toast.CompleteProgress(Strings.SavedCharacterNamesFailed,true);}
        finally
        {
            _cancellation.Dispose();_cancellation=null;
            SetBusy(false);
        }
    }

    private void ApplyLanguage()
    {
        var detail=Detail.Row;
        // Rebuild only presentation state. Do not navigate, refresh, or reopen any remote page.
        _restoringView=true; _reloadingLocal=true;
        try
        {
            LocalizationWpf.Apply(_settings.Language);
            History.RefreshLanguage(); Scores.RefreshLanguage();
            Detail.RefreshLanguage(detail);
            RefreshAccountUi(); RefreshViewedPlayerUi(); UpdateSearchEmptyState();
            SetSearchError(null); HideNotice(); HideButtonProgress(); UpdateRefreshAction();
            foreach(var window in _opponents.Values) window.RefreshLanguage();
            _tray?.RefreshLanguage();
        }
        finally { _restoringView=false; _reloadingLocal=false; }
    }

    private SettingsWindow CreateSettingsWindow()
    {
        var dialog=new SettingsWindow(this,_settings) {BeforeImport=()=>CancelDownloadsAsync()};
        dialog.DatabaseImported+=()=>
        {
            _pagers.Clear(); History.AllowPageUpdates(); ReloadLocal(preserveViewport:true);
        };
        return dialog;
    }
    private void FindPlayer_Click(object sender,RoutedEventArgs e) => OpenPlayerSearch();
    private void OpenPlayerSearch()
    {
        if(!_ready || _exitRequested) return;
        ActivatePlayerSearch();
        Dispatcher.BeginInvoke(()=>{PlayerUidBox.Focus();PlayerUidBox.SelectAll();},DispatcherPriority.Input);
    }
    private void ActivatePlayerSearch()
    {
        if(!_ready || _exitRequested || IsPlayerSearchActive) return;
        AccountPopup.IsOpen=false;
        IsPlayerSearchActive=true;SetSearchError(null);UpdateSearchHistory();PlayerSearchPopup.IsOpen=true;
    }
    private void ClosePlayerSearch()
    {
        if(!IsPlayerSearchActive) return;
        IsPlayerSearchActive=false;PlayerSearchPopup.IsOpen=false;
        if(IsActive && PlayerSearchField.IsKeyboardFocusWithin) Focus();
    }
    private bool IsSearchElement(DependencyObject? node)
    {
        for(;node!=null;node=node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if(node==PlayerSearchField || node==PlayerSearchPopup.Child) return true;
        return false;
    }
    private void PlayerUid_Focused(object sender,KeyboardFocusChangedEventArgs e) => ActivatePlayerSearch();
    private void PlayerSearch_LostKeyboardFocus(object sender,KeyboardFocusChangedEventArgs e) => Dispatcher.BeginInvoke(()=>
    {
        if(IsPlayerSearchActive && !PlayerSearchField.IsKeyboardFocusWithin && !(PlayerSearchPopup.Child?.IsKeyboardFocusWithin ?? false)) ClosePlayerSearch();
    },DispatcherPriority.Input);
    private void ClearPlayerUid_Click(object sender,RoutedEventArgs e)
    {PlayerUidBox.Clear();PlayerUidBox.Focus();ActivatePlayerSearch();}
    private async void PlayerSearchSubmit_Click(object sender,RoutedEventArgs e) => await SearchPlayerAsync(PlayerUidBox.Text);
    private void SetSearchError(string? text)
    {
        PlayerSearchError.Text=text ?? ""; PlayerSearchError.Visibility=text==null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSearchEmptyState();
    }
    private void UpdateSearchEmptyState()
    {
        if(PlayerSearchEmpty==null || PlayerSearchHistoryList==null) return;
        PlayerSearchEmpty.Visibility=string.IsNullOrWhiteSpace(PlayerUidBox.Text) && PlayerSearchHistoryList.Items.Count==0 && !_searchingPlayer && PlayerSearchError.Visibility!=Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateSearchHistory()
    {
        if(_playerSearch==null || PlayerUidBox==null) return;
        var input=PlayerUidBox.Text.Trim();
        var entries=_playerSearch.Entries.Where(p=>p.Id.Contains(input,StringComparison.Ordinal) || p.Name.Contains(input,StringComparison.OrdinalIgnoreCase)).ToList();
        PlayerSearchHistoryList.ItemsSource=entries;
        PlayerSearchHistoryLabel.Visibility=entries.Count==0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSearchEmptyState();
    }
    private void PlayerUid_Changed(object sender,TextChangedEventArgs e)
    {
        if(_searchingPlayer) RequestCancellation();
        if(PlayerSearchError!=null) SetSearchError(null);
        UpdateSearchHistory();
    }
    private async void PlayerUid_KeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter) {e.Handled=true;await SearchPlayerAsync(PlayerUidBox.Text);}
        else if(e.Key==Key.Escape) {e.Handled=true;ClosePlayerSearch();}
    }
    private async void PlayerHistory_Click(object sender,RoutedEventArgs e)
    {
        if((sender as Button)?.Tag is not PlayerIdentity player) return;
        PlayerUidBox.Text=player.Id; await SearchPlayerAsync(player.Id);
    }
    private async Task SearchPlayerAsync(string input)
    {
        if(!_ready || _exitRequested || App.Capture) return;
        string id;
        try { id=PlayerLookup.ParseUid(input); } catch(ArgumentException ex) {SetSearchError(ex.Message);return;}
        if(_busy && !_searchingPlayer && id==_navigation.Current.Id) {ClosePlayerSearch();return;}
        if(_searchingPlayer && _lookupUid==id && _cancellation?.IsCancellationRequested==false) return;
        var version=++_navigationVersion; await CancelDownloadsAsync(); if(version!=_navigationVersion || _exitRequested) return;
        var session=_vault.Load();
        if(session==null) {SetSearchError(Strings.SignInBeforeSearch);return;}
        var lookup=LookupPlayerCoreAsync(id,session); _foregroundTask=lookup;
        var player=await lookup;
        if(player==null || version!=_navigationVersion || _exitRequested) return;
        try
        {
            _playerSearch.Remember(player); _profiles.RememberIdentity(player);
            ClosePlayerSearch();
            if(_navigation.Current.Id==player.Id)
            {
                RefreshViewedPlayerUi();
                if(PageTabs.SelectedIndex==1) await BeginLoadAsync(scoreRange:true);
                else if(Pager.NeedsInitial(History.Category)) await BeginLoadAsync();
                return;
            }
            await ViewPlayerAsync(player);
        }
        catch(Exception ex) { SetSearchError(Strings.SearchSaveFailed); if(!PlayerSearchPopup.IsOpen) ReportError(ex); }
    }
    private async Task<PlayerIdentity?> LookupPlayerCoreAsync(string id,SavedSession session)
    {
        SetBusy(true,quiet:true,background:true); _searchingPlayer=true; _lookupUid=id; SetSearchError(null);
        PlayerSearchSubmit.IsEnabled=false;
        PlayerSearchStatus.Visibility=Visibility.Visible; _cancellation=new CancellationTokenSource();
        var token=_cancellation.Token;
        try
        {
            using var client=CreateClient(session);
            return await PlayerLookup.FindAsync(client,id,token);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) {return null;}
        catch(Exception ex)
        {
            SetSearchError(ex is InvalidDataException or ArgumentException ? ex.Message : ex is LoginRequiredException ? Strings.SessionExpired : Strings.SearchFailed);
            return null;
        }
        finally
        {
            _cancellation.Dispose();_cancellation=null;_searchingPlayer=false;_lookupUid=null;
            PlayerSearchStatus.Visibility=Visibility.Collapsed;PlayerSearchSubmit.IsEnabled=true;UpdateSearchEmptyState();SetBusy(false);
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await ToggleRefreshAsync();
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await StopRefreshAsync();
    private async Task ToggleRefreshAsync()
    {
        if(_stoppingRefresh) return;
        if(_busy) await StopRefreshAsync();
        else await SynchronizeAsync();
    }
    private async Task StopRefreshAsync()
    {
        if(_stoppingRefresh) return;
        _stoppingRefresh=true;
        try
        {
            ProgressStage(Strings.Stopping);
            await CancelDownloadsAsync(silent:false);
            if(!_closeRequested) ShowNotice(Strings.StoppedRecordsKept);
        }
        finally {_stoppingRefresh=false;}
    }
    private Task SynchronizeAsync(bool full = false)
    {
        ClosePlayerSearch();
        if(_ready && PageTabs.SelectedIndex==0 && History.FavoritesOnly) {ReloadLocal(preserveViewport:true);return Task.CompletedTask;}
        return BeginLoadAsync(reset:true);
    }
    private Task BeginLoadAsync(bool reset=false,bool more=false,bool scoreRange=false)
    {
        if(_busy || !_ready || _exitRequested || _stoppingRefresh) return Task.CompletedTask;
        if(PageTabs.SelectedIndex==0 && History.FavoritesOnly) return Task.CompletedTask;
        if(scoreRange && (PageTabs.SelectedIndex!=1 || !Pager.NeedsScoreRange(Scores.Range))) return Task.CompletedTask;
        History.AllowPageUpdates();
        return _foregroundTask=LoadPagesAsync(reset,more,scoreRange);
    }
    private async Task LoadPagesAsync(bool reset,bool more,bool scoreRange)
    {
        SetBusy(true,quiet:!reset && PageTabs.SelectedIndex!=1); HideNotice(); _cancellation = new CancellationTokenSource(); _silentCancellation=false;
        var token=_cancellation.Token;
        var pager=Pager; var category=PageTabs.SelectedIndex==1 ? "rank" : History.Category;
        var fillScores=PageTabs.SelectedIndex==1;
        var requestedScores=Scores.Range;
        var completed=false;
        BattlePager? observedPager=null;
        void PageProgress(BattleLoadProgress progress)
        {
            if(!_progressVisible)return;
            _refreshProgress.Load(progress,!more && (!scoreRange || pager.RankHeadFetched) ? 2 : 0);
            UpdateButtonProgress();
        }
        if(reset) pager.Reset(category);
        async Task PageSaved()
        {
            token.ThrowIfCancellationRequested();
            ReloadLocal(preserveViewport:true,refreshImages:false);
            await Dispatcher.InvokeAsync(() => { },DispatcherPriority.Background);
        }
        try
        {
            await StopBackgroundDownloadsAsync();
            if(more && !pager.MoreNeedsNetwork(History.Filtered,category))
            {
                await pager.LoadMoreAsync(null,category,History.Filtered,token,PageSaved);
                completed=true;
                return;
            }
            var session = _vault.Load();
            if (session == null)
            {
                SetStatus("login_needed", Strings.CompleteCapcomSignIn);
                ProgressStage(Strings.WaitingForSignIn);
                session = await LoginWindow.SignInAsync(this, _settings, _vault);
                if (session == null) { SetStatus("login_cancelled", Strings.SignInCancelled); return; }
                var sameAccount=session.AccountId==_settings.PlayerId;
                SetActiveAccount(session, preserveView:sameAccount);
                if(!sameAccount) requestedScores=Scores.Range;
                pager=Pager;
                if(reset) pager.Reset(category);
            }
            _cancellation.Token.ThrowIfCancellationRequested();
            using var client = CreateClient(session);
            client.Activity+=RequestProgress;
            ProgressStage(Strings.CheckingSession);
            SetStatus("validating", Strings.CheckingSessionProgress);
            if(!more) await client.ValidateAsync(token);
            if (client.AccountId != _settings.PlayerId && _vault.Load() is { } validated)
            { SetActiveAccount(validated); requestedScores=Scores.Range; if(reset) Pager.Reset(category); }
            pager=Pager; var target = pager.Owner;
            observedPager=pager;pager.ProgressChanged+=PageProgress;
            RefreshAccountUi();
            _lastSync = fillScores ? await pager.LoadScoreRangeAsync(client,requestedScores,token,PageSaved,forceHead:reset)
                : more ? await pager.LoadMoreAsync(client,category,History.Filtered,token,PageSaved)
                : await pager.LoadInitialAsync(client,category,token,PageSaved);
            ReloadLocal(preserveViewport: true, refreshImages: false);
            var profileMessage = "";
            try
            {
                SetStatus("profiles", Strings.UpdatingProfilesProgress);
                if(!more && (!scoreRange || pager.RankHeadFetched))
                {
                    if(_progressVisible) {_refreshProgress.Metadata();UpdateButtonProgress();}
                    var images = await _profiles.RefreshMetadataAsync(client, target, token, captureScores: pager.RankHeadFetched);
                    token.ThrowIfCancellationRequested();
                    QueueImages(images);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            { profileMessage = Strings.ImagesPendingSuffix; }
            _lastSync = _lastSync with { Requests = client.Requests };
            ReloadLocal(preserveViewport: true); RefreshAccountUi();
            SetStatus(profileMessage.Length>0 ? "sync_complete_images_pending" : "sync_complete", Localization.Format(Strings.RefreshCompleteSummary, _lastSync.Inserted, _lastSync.Pages, _lastSync.Requests, profileMessage));
            completed=true;
            if(target==_settings.PlayerId) _autoRefresh.Resume(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if(!_silentCancellation) SetStatus("cancelled", Strings.CancelledLoadedSaved); }
        catch (Exception ex) { ReloadLocal(preserveViewport: true); ReportError(ex); }
        finally
        {
            if(observedPager!=null) observedPager.ProgressChanged-=PageProgress;
            FinishButtonProgress(completed,token.IsCancellationRequested);
            if(reset && pager.Owner==_settings.PlayerId) _autoRefresh.Restart(DateTimeOffset.UtcNow);
            _cancellation.Dispose(); _cancellation = null; SetBusy(false);
            if(completed && !token.IsCancellationRequested && !_closeRequested) History.ContinueAutoLoad();
            else History.StopAutoLoad();
            if(!token.IsCancellationRequested && !_closeRequested) StartImageDownloads([]);
            if (App.Capture) { await Task.Delay(200); Capture(); }
            if (_closeRequested) Close();
        }
    }
    private void ReportError(Exception ex)
    {
        var text = ex switch
        {
            LoginRequiredException => ex.Message,
            System.Net.Http.HttpRequestException => ex.Message,
            InvalidDataException => ex.Message,
            ArgumentException => ex.Message,
            TaskCanceledException => Strings.RequestTimedOut,
            _ => Localization.Format(Strings.OperationFailed, ex.GetType().Name)
        };
        SetStatus(ex is LoginRequiredException ? "login_required" : "error", text);
    }
    private void ReloadLocal(bool preserveViewport = false, bool refreshImages = true, PlayerViewState? restoredView=null,bool automaticRefresh=false)
    {
        var previousDetail=Detail.Row?.Battle;
        var previousSelection=(PageTabs.SelectedIndex==1 ? Scores.Selected : History.Selected)?.Battle;
        History.Pager=Pager;
        _archive.SignedInId=_vault.Load()?.AccountId;
        _archive.Load(_navigation.Current.Id, refreshImages);
        History.DefaultFavoritePlayer=_navigation.Home;
        _reloadingLocal=true;
        try { History.Reload(preserveViewport,restoredView?.History,automaticRefresh); Scores.Reload(restoredView?.Scores); }
        finally { _reloadingLocal=false; }
        var selected=PageTabs.SelectedIndex==1 ? Scores.Selected : History.Selected;
        var active=PageTabs.SelectedIndex==1 ? _archive : History.ActiveArchive ?? _archive;
        Detail.Bind(active);
        var retained=restoredView!=null ? active.All.FirstOrDefault(b=>b.ReplayId==restoredView.DetailReplay) :
            previousDetail?.OwnerId==active.Owner && previousSelection!=null &&
            selected?.Battle.OwnerId==previousSelection.OwnerId && selected?.Battle.ReplayId==previousSelection.ReplayId
            ? active.All.FirstOrDefault(b=>b.ReplayId==previousDetail!.ReplayId) : null;
        Detail.Show(retained!=null ? active.Row(retained) : selected);
        RefreshViewedPlayerUi();
    }

    private void QueueImages(IReadOnlyList<ImageDownload> images)
    {
        foreach(var image in images) { _pendingImages.RemoveAll(i=>i.Destination==image.Destination); _pendingImages.Add(image); }
    }
    private void StartImageDownloads(IReadOnlyList<ImageDownload> images,string? refreshOwner=null)
    {
        QueueImages(images);
        if (_vault.Load() is not { } session || _pendingImages.Count == 0 || _closeRequested || _imageTask is {IsCompleted:false}) return;
        _imageCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _imageCancellation = cancellation;
        _imageTask = DownloadImagesAsync(_pendingImages.ToList(), session, cancellation.Token,refreshOwner);
    }
    private async Task DownloadImagesAsync(IReadOnlyList<ImageDownload> images, SavedSession session, CancellationToken token,string? refreshOwner)
    {
        try
        {
            using var client = new ImageClient(_settings, session);
            await ImageDownloads.RunAsync(images, client, image =>
            {
                _pendingImages.Remove(image);
                if (!token.IsCancellationRequested && !_closeRequested)
                {
                    if(refreshOwner==null || _navigation.Current.Id==refreshOwner) ReloadLocal(preserveViewport:true);
                    RefreshAccountUi();
                }
                return Task.CompletedTask;
            }, token);
            foreach(var image in images) _pendingImages.Remove(image);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closeRequested && !token.IsCancellationRequested) SetStatus("images_pending", Localization.Format(Strings.ImageRetryPending, ex.GetType().Name));
        }
    }
    private async Task StopBackgroundDownloadsAsync()
    {
        if(_characterNames!=null) await _characterNames.CancelAsync();
        var cancellation = _imageCancellation; var task = _imageTask;
        cancellation?.Cancel();
        if (task != null) await task;
        cancellation?.Dispose(); _imageCancellation = null; _imageTask = null;
    }
    private void RequestCancellation()
    {
        // Invalidate any view change waiting behind this download before cancelling it.
        _viewLoadVersion++;
        History.StopAutoLoad();
        _silentCancellation=true; _cancellation?.Cancel(); _imageCancellation?.Cancel();
        foreach(var login in OwnedWindows.OfType<LoginWindow>().ToArray()) login.Close();
    }
    private async Task CancelDownloadsAsync(bool silent=true)
    {
        RequestCancellation();
        _silentCancellation=silent;
        await _foregroundTask;
        await StopBackgroundDownloadsAsync();
    }
    private async Task CategoryChangedAsync()
    {
        if(!_ready) return;
        _navigation.SelectCategory(History.Category);
        await EnsureCurrentViewAsync();
    }
    private async Task EnsureScoreRangeAsync()
    {
        if(!_ready || _restoringView || App.Capture || PageTabs.SelectedIndex!=1) return;
        await EnsureCurrentViewAsync();
    }
    private async Task EnsureCurrentViewAsync()
    {
        if(!_ready || _restoringView || _exitRequested || _stoppingRefresh || App.Capture) return;
        var version=++_viewLoadVersion;var navigationVersion=_navigationVersion;
        // Filters update locally now; the newest view gets its turn after active work finishes.
        if(_busy) await _foregroundTask;
        if(version!=_viewLoadVersion || navigationVersion!=_navigationVersion || _busy || _exitRequested) return;
        if(PageTabs.SelectedIndex==1) await BeginLoadAsync(scoreRange:true);
        else if(!History.FavoritesOnly && Pager.NeedsInitial(History.Category)) await BeginLoadAsync();
    }

    private void RefreshViewedPlayerUi()
    {
        _navigation.UpdateName(_profiles.Profile(_navigation.Current.Id)?.Name ?? _archive.All.FirstOrDefault()?.PlayerName);
        var viewed=PageTabs.SelectedIndex==0 && History.FavoritesOnly ? History.FavoritePlayer ?? _navigation.Current : _navigation.Current;
        var signedInId=_vault.Load()?.AccountId;
        ViewedPlayerName.Text = viewed.Id==signedInId ? Strings.MyRecords : viewed.DisplayName;
        ViewedPlayerName.ToolTip = $"{viewed.DisplayName}\nUID {viewed.Id}";
        Detail.CurrentPlayerId=_navigation.Current.Id;
        Detail.IsFavoritesView=History.FavoritesOnly;
        BackButton.IsEnabled=_navigation.CanGoBack;
        ForwardButton.IsEnabled=_navigation.CanGoForward;
        HomeButton.IsEnabled=!_navigation.IsHome || History.FavoritesOnly;
        var lastUpdated = _store.LastSuccessfulLoad(viewed.Id);
        _viewingActiveAccount=viewed.Id==signedInId;
        _lastViewedUpdate=lastUpdated; UpdateRefreshReminder();
        UpdatedLabel.Text = lastUpdated == null ? "" : Strings.UpdatedAtPrefix + BattleDateDisplay.Timestamp(lastUpdated.Value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm"));
    }
    private async Task ViewPlayerAsync(PlayerIdentity player)
    {
        if (!_ready || _exitRequested) return;
        if (!player.IsValid) { ShowNotice(Strings.NoPlayerUid); return; }
        if(player.Id==_navigation.Current.Id && !History.FavoritesOnly) return;
        if(Detail.PendingPlayerId==player.Id) return;
        var version=++_navigationVersion;
        _pendingPlayerVersion=version; Detail.PendingPlayerId=player.Id;
        try
        {
            // Paint click feedback before cancellation or local archive work can delay the UI.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if(version!=_navigationVersion || _exitRequested) return;
            await CancelDownloadsAsync(); if(version!=_navigationVersion || _exitRequested) return;
            RememberCurrentView();
            if (!_navigation.Visit(player) && !History.FavoritesOnly) return;
            // The destination list owns further loading; never transfer feedback to its detail buttons.
            Detail.PendingPlayerId="";
            HideNotice(); History.ResetFilters(); Scores.ResetSelection(); ReloadLocal();
            // A successful empty history is cached too. A failed/partial first load is retried on the next visit.
            if(PageTabs.SelectedIndex==1) await BeginLoadAsync(scoreRange:true);
            else if(Pager.NeedsInitial(History.Category)) await BeginLoadAsync();
        }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            if(_pendingPlayerVersion==version) {Detail.PendingPlayerId="";_pendingPlayerVersion=0;}
        }
    }
    private Task GoBackAsync(int steps=1) => TraverseHistoryAsync(false,steps);
    private Task GoForwardAsync(int steps=1) => TraverseHistoryAsync(true,steps);
    private async Task TraverseHistoryAsync(bool forward,int steps)
    {
        if (!_ready || _exitRequested || !(forward ? _navigation.CanGoForward : _navigation.CanGoBack)) return;
        ClosePlayerSearch();
        var version=++_navigationVersion; await CancelDownloadsAsync(); if(version!=_navigationVersion) return;
        RememberCurrentView();
        if(forward) _navigation.Forward(steps); else _navigation.Back(steps);
        RestoreCurrentView();
    }
    private async void Back_Click(object sender, RoutedEventArgs e) => await GoBackAsync();
    private async void Forward_Click(object sender,RoutedEventArgs e) => await GoForwardAsync();
    private void NavigationMenu_Opening(object sender,ContextMenuEventArgs e)
    {
        if(!_ready || sender is not Button button) {e.Handled=true;return;}
        var forward=button==ForwardButton;
        var entries=forward ? _navigation.ForwardHistory : _navigation.BackHistory;
        if(entries.Count==0) {e.Handled=true;return;}
        button.ContextMenu.Items.Clear();
        foreach(var entry in entries)
        {
            var item=new MenuItem {Header=entry.Title,ToolTip=entry.Title,Tag=entry.Steps};
            item.Click+=async (_,_)=>await TraverseHistoryAsync(forward,entry.Steps);
            button.ContextMenu.Items.Add(item);
        }
    }
    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _exitRequested) return;
        ClosePlayerSearch();
        var version=++_navigationVersion; await CancelDownloadsAsync(); if(version!=_navigationVersion) return;
        RememberCurrentView(); _navigation.ReturnHome(); RestoreCurrentView();
        if(History.FavoritesOnly) {History.ResetFilters();ReloadLocal();}
    }

    private void RememberCurrentView() => _navigation.RememberView(new()
    { History=History.CaptureState(), Scores=Scores.CaptureState(), PageIndex=PageTabs.SelectedIndex, DetailReplay=Detail.Row?.Battle.ReplayId });
    private void RestoreCurrentView()
    {
        _restoringView=true;
        try { HideNotice(); PageTabs.SelectedIndex=_navigation.View.PageIndex; ReloadLocal(restoredView:_navigation.View); }
        finally { _restoringView=false; }
        if(PageTabs.SelectedIndex==1) _=EnsureScoreRangeAsync();
    }

    private void OpenPlayerRanks(PlayerIdentity player)
    {
        if(!player.IsValid) { ShowNotice(Strings.NoPlayerUid); return; }
        var session = _vault.Load();
        if (session == null) { ShowNotice(Strings.SignInFirst, true, Strings.SignIn, () => _ = LoginAsync()); return; }
        var id = player.Id;
        if (_opponents.TryGetValue(id, out var open)) { open.Activate(); return; }
        var window = new OpponentWindow(this, id, player.DisplayName, _settings, session, _vault, _profiles,ObserveCharacterNames) {Suspended=_busy};
        _opponents[id] = window; window.Closed += (_, _) => _opponents.Remove(id);
        window.Show();
    }
    private void CloseOpponents() { foreach (var window in _opponents.Values.ToArray()) window.Close(); }

    private void PageTabs_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (History == null || Scores == null || Detail == null) return;
        var scores = PageTabs.SelectedIndex == 1;
        if(_ready) Scores.SetLoading(scores && _busy && !_loadingInBackground);
        History.Visibility = scores ? Visibility.Collapsed : Visibility.Visible;
        Scores.Visibility = scores ? Visibility.Visible : Visibility.Collapsed;
        if(_ready) Detail.Bind(scores ? _archive : History.ActiveArchive ?? _archive);
        Detail.Show(scores ? Scores.Selected : History.Selected);
        if(_ready) RefreshViewedPlayerUi();
        if(_ready && !_restoringView) _=EnsureCurrentViewAsync();
        if (App.Capture && _ready) Dispatcher.BeginInvoke(new Action(Capture), DispatcherPriority.ContextIdle);
    }

    private void ShowNotice(string text, bool danger = false, string? actionText = null, Action? action = null)
        =>Toast.Show(text,danger,actionText,action);
    private void HideNotice()=>Toast.Dismiss();
    private void SetStatus(string stage, string message)
    {
        switch (stage)
        {
            case "error": ShowNotice(message, true); break;
            case "login_required": ShowNotice(Strings.SessionExpired, true, Strings.SignInAgain, () => _ = LoginAsync()); break;
            case "login_needed": ShowNotice(Strings.SignInFirst); break;
            case "settings_saved": ShowNotice(Strings.Saved); break;
            case "cancelled": ShowNotice(Strings.CancelledFetchedSaved); break;
            case "profile_pending": ShowNotice(Strings.PortraitsNotUpdated); break;
            case "sync_complete_images_pending":
                ShowNotice(Strings.RecordsUpdatedImagesPending);
                break;
        }
        var path = Path.Combine(App.DataDirectory, "status.json");
        var value = new { pid = Environment.ProcessId, stage, playerId = _navigation.Current.Id, accountId = _settings.PlayerId, transport = "HttpClient", storedMatches = _archive?.All.Count ?? 0,
            lastSync = _lastSync, updatedAt = DateTimeOffset.UtcNow, status = message };
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    private async Task CaptureAllAsync()
    {
        await Task.Delay(400); Capture();
        PageTabs.SelectedIndex = 1; await Task.Delay(400);
        PageTabs.SelectedIndex = 0; await Task.Delay(200);
        var settings = CreateSettingsWindow(); settings.Show(); await Task.Delay(600); settings.Close();
    }
    private void Capture()
    {
        if (Content is not FrameworkElement element || element.ActualWidth <= 0) return;
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(App.DataDirectory, $"ui-{PageTabs.SelectedIndex}.png")); png.Save(file);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if(!_exitReady && !App.IsShuttingDown)
        { e.Cancel=true; if(!_exitRequested && !_showingCloseChoice) Dispatcher.BeginInvoke(()=>_=HandleCloseAsync()); }
        base.OnClosing(e);
    }
    private async Task HandleCloseAsync()
    {
        if(_exitRequested || _showingCloseChoice) return;
        var choice=App.Capture || _tray==null ? CloseAction.Exit : _settings.WindowCloseAction;
        if(choice==CloseAction.Ask)
        {
            _showingCloseChoice=true;
            try
            {
                var dialog=new CloseChoiceWindow(this);
                if(dialog.ShowDialog()!=true) return;
                choice=dialog.Choice;
                if(dialog.Remember)
                {
                    var settings=_settings with {WindowCloseAction=choice};
                    try {settings.Save(App.DataDirectory);_settings=settings;}
                    catch {ShowNotice(Strings.CloseActionSaveFailed,true);}
                }
            }
            finally {_showingCloseChoice=false;}
        }
        if(choice==CloseAction.MinimizeToTray) MinimizeToTray();
        else await ExitApplicationAsync();
    }
    private void MinimizeToTray()
    {
        AccountPopup.IsOpen=false;ClosePlayerSearch();
        _hiddenOwnedWindows.Clear();
        foreach(var window in OwnedWindows.OfType<Window>().Where(w=>w.IsVisible).ToArray()) {_hiddenOwnedWindows.Add(window);window.Hide();}
        Hide();
    }
    private void ShowMainPanel()
    {
        if(_exitRequested) return;
        Show(); WindowState=_restoreState; Activate();
        foreach(var window in _hiddenOwnedWindows.Where(w=>OwnedWindows.OfType<Window>().Contains(w)).ToArray()) window.Show();
        _hiddenOwnedWindows.Clear();
    }
    public async Task ExitApplicationAsync()
    {
        if(_exitRequested) return;
        _exitRequested=true;_closeRequested=true;
        _autoRefreshTimer.Stop();_refreshReminderTimer.Stop();
        AccountPopup.IsOpen=PlayerSearchPopup.IsOpen=false;
        var settings=OwnedWindows.OfType<SettingsWindow>().ToArray();
        foreach(var dialog in settings) dialog.CancelTransfer();
        await CancelDownloadsAsync();
        await Task.WhenAll(settings.Select(w=>w.PendingTransfer));
        CloseOpponents();
        _exitReady=true;Close();
    }
    protected override void OnClosed(EventArgs e)
    { _tray?.Dispose();_refreshReminderTimer.Stop();_autoRefreshTimer.Stop();_progressTimer.Stop();base.OnClosed(e); }
}
