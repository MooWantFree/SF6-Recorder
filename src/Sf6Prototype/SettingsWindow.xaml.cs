using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Shape = System.Windows.Shapes.Shape;
using Microsoft.Win32;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class SettingsWindow : ChromeWindow
{
    private ConnectionSettings _current;
    private readonly CancellationTokenSource _closing = new();
    private bool _syncingIntervals;
    // Reopening settings in the same session returns to the page the user last viewed.
    private static int _lastCategory;
    private Task _transfer=Task.CompletedTask;
    private bool _waitingToClose;
    internal static UpdateRelease? AvailableUpdate { get; set; }
    private bool _checkingUpdates;
    internal bool UpdateInProgress => _checkingUpdates || _updateCancellation != null;
    internal event Action? UpdateRequested;
    internal event Action<UpdateReminder>? UpdateReminderChanged;
    private CancellationTokenSource? _updateCancellation;
    private Task _updateDownload=Task.CompletedTask;
    public Func<UpdateRelease,Task>? ApplyUpdate {get;set;}
    public Action<string>? ChangeLanguage {get;set;}
    public event Action? OverlayPreviewChanged;
    private readonly DispatcherTimer _gameStatusTimer=new() {Interval=TimeSpan.FromSeconds(2)};
    public Func<Task>? BeforeImport { get; set; }
    public event Action? DatabaseImported;
    public Task PendingTransfer=>WaitForTransfersAsync();
    private async Task WaitForTransfersAsync()
    { try {await Task.WhenAll(_transfer,_updateDownload);} catch { /* Individual operations report their own errors. */ } }
    public void CancelTransfer()=>_closing.Cancel();
    public ConnectionSettings? Result { get; private set; }
    public SettingsWindow(Window owner, ConnectionSettings current)
    {
        InitializeComponent(); InitializeWindowChrome(WindowSurface,TitleBar); Owner = owner; _current = current;
        UpdateReleaseButton();
        ProxyEnabled.IsChecked = current.UseProxy; ProxyAddressBox.Text = current.ProxyAddress;
        StartupUpdateEnabled.IsChecked = current.CheckForUpdatesOnStartup;
        InitializeOverlay(current);
        InitializeQuickLaunch(current.QuickLaunch);
        ReminderSecondsBox.Text=current.RefreshReminderSeconds.ToString(CultureInfo.InvariantCulture);
        AutoRefreshSecondsBox.Text=current.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        RefreshAfterMatchEnabled.IsChecked=current.RefreshAfterMatch;
        FocusLatestMatchEnabled.IsChecked=current.FocusLatestMatchAfterRefresh;
        MatchEndWaitBox.Text=current.MatchEndWaitSeconds.ToString(CultureInfo.InvariantCulture);
        MatchEndRetryBox.Text=current.MatchEndRetrySeconds.ToString(CultureInfo.InvariantCulture);
        MatchEndFrequencyBox.Text=current.MatchEndSamplesPerSecond.ToString(CultureInfo.InvariantCulture);
        ApiIntervalBox.Text=current.ApiIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        CloseActionBox.SelectedIndex=(int)current.WindowCloseAction;
        LanguageBox.SelectedValue=Localization.Normalize(current.Language);
        LanguageBox.SelectionChanged+=Language_Changed;
        CategoryNav.SelectedIndex=Math.Clamp(_lastCategory,0,CategoryNav.Items.Count-1);
        ContentRendered += async (_, _) =>
        {
            UiCapture.SaveLater((FrameworkElement)Content, "ui-settings.png");
            if(App.Capture) await CaptureCategoriesAsync();
        };
        _gameStatusTimer.Tick+=(_,_)=>UpdateGameStatus();
        RefreshAfterMatchEnabled.Checked+=(_,_)=>UpdateGameStatusTimer();
        RefreshAfterMatchEnabled.Unchecked+=(_,_)=>UpdateGameStatusTimer();
        Loaded+=(_,_)=>UpdateGameStatusTimer();
        Closed += (_, _) => { _gameStatusTimer.Stop();_closing.Cancel(); _closing.Dispose(); };
    }
    private void Category_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(CategoryNav.SelectedItem is not ListBoxItem {Tag:string selected}) return;
        foreach(var page in Pages.Children.OfType<FrameworkElement>())
            page.Visibility=page.Name==selected ? Visibility.Visible : Visibility.Hidden;
        _lastCategory=CategoryNav.SelectedIndex;
    }
    public void ShowCategory(FrameworkElement page)=>
        CategoryNav.SelectedItem=CategoryNav.Items.OfType<ListBoxItem>().FirstOrDefault(i=>i.Tag as string==page.Name) ?? CategoryNav.SelectedItem;
    private async Task CaptureCategoriesAsync()
    {
        for(var i=0;i<CategoryNav.Items.Count;i++)
        {
            await Task.Delay(150);
            if(!IsLoaded) return;
            CategoryNav.SelectedIndex=i;
            UiCapture.SaveLater((FrameworkElement)Content,$"ui-settings-{i}.png");
            // Finish the queued capture before switching pages, even during a slow render.
            await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
        }
    }
    /// <summary>The page holding the first value that would make saving fail.</summary>
    private FrameworkElement? InvalidPage()
    {
        if (LaunchNeedsAccelerator) return GeneralPage;
        if (!ValidOverlayLimit()) { TrendTab.IsSelected = true; return OverlayPage; }
        try
        {
            if(ProxyEnabled.IsChecked==true) (_current with {UseProxy=true,ProxyAddress=ProxyAddressBox.Text.Trim()}).ValidateProxy();
            ConnectionSettings.ParseApiIntervalSeconds(ApiIntervalBox.Text);
        }
        catch(ArgumentException) {return NetworkPage;}
        try
        {
            ConnectionSettings.ParseReminderSeconds(ReminderSecondsBox.Text);ConnectionSettings.ParseAutoRefreshSeconds(AutoRefreshSecondsBox.Text);
            ConnectionSettings.ParseMatchEndSeconds(MatchEndWaitBox.Text);ConnectionSettings.ParseMatchEndSeconds(MatchEndRetryBox.Text,retry:true);
            ConnectionSettings.ParseMatchEndFrequency(MatchEndFrequencyBox.Text);
        }
        catch(ArgumentException) {return RefreshPage;}
        return null;
    }
    private void Language_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(LanguageBox.SelectedValue is not string language || language==_current.Language) return;
        try
        {
            // Commit only the language. Other controls may contain incomplete, unsaved edits.
            if(ChangeLanguage is { } change) change(language);
            else LocalizationWpf.Apply(language);
            _current=_current with {Language=language};
            DisplaySample();
            CheckUpdatesButton.ToolTip = AvailableUpdate?.AvailableMessage;
            AvailableUpdateNotice.RefreshLanguage();
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Diagnostics.Write("settings","language_save_failed",error);
            LanguageBox.SelectedValue=_current.Language;
            ShowError(Strings.LanguageSaveFailed);
        }
    }
    private void UpdateGameStatusTimer()
    {
        _gameStatusTimer.Stop();
        if(!IsLoaded || RefreshAfterMatchEnabled.IsChecked!=true) return;
        UpdateGameStatus();_gameStatusTimer.Start();
    }
    private void UpdateGameStatus()
    {
        if(RefreshAfterMatchEnabled.IsChecked!=true) {_gameStatusTimer.Stop();return;}
        var running=Sf6GameProbe.Find()!=null;
        GameRunningHint.SetResourceReference(TextBlock.TextProperty,running ? "I18n.Sf6Running" : "I18n.Sf6NotRunning");
        // Match the title-bar indicator: filled green while running, a neutral outline otherwise.
        GameRunningLight.SetResourceReference(Shape.StrokeProperty,running ? "Win" : "Ink3");
        if(running) GameRunningLight.SetResourceReference(Shape.FillProperty,"Win");
        else GameRunningLight.Fill=System.Windows.Media.Brushes.Transparent;
    }
    private void OpenLogs_Click(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(App.DataDirectory)) return;
        try
        {
            Directory.CreateDirectory(App.DataDirectory);
            Process.Start(new ProcessStartInfo {FileName=App.DataDirectory,UseShellExecute=true});
        }
        catch(Exception ex) when(ex is Win32Exception or IOException or UnauthorizedAccessException)
        {ShowError(Strings.OpenLogFolderFailed);}
    }
    internal void UpdateReleaseButton()
    {
        CheckUpdatesButton.SetResourceReference(ContentControl.ContentProperty,
            "I18n.CheckUpdates");
        if (AvailableUpdate == null) AvailableUpdateNotice.Dismiss();
        CheckUpdatesButton.ToolTip = AvailableUpdate?.AvailableMessage;
        InstallUpdateButton.Visibility = AvailableUpdate == null ? Visibility.Collapsed : Visibility.Visible;
    }
    internal bool OfferStartupUpdate(UpdateRelease release)
    {
        UpdateReleaseButton();
        if (!InstallUpdateButton.IsEnabled || _closing.IsCancellationRequested) return false;
        ShowUpdatePrompt(release);
        return true;
    }
    private void ShowUpdatePrompt(UpdateRelease release)
    {
        UpdateToast.Dismiss();
        AvailableUpdateNotice.Show(release, () => _ = InstallAvailableUpdateAsync(), reminder =>
        {
            UpdateReminderChanged?.Invoke(reminder);
            _current = _current with { UpdateReminder = reminder };
        });
    }
    private async void CheckUpdates_Click(object sender,RoutedEventArgs e)
    {
        if (!CheckUpdatesButton.IsEnabled) return;
        ConnectionSettings settings;
        try { settings = Draft(includeIntervals:false); }
        catch (ArgumentException ex) { UpdateToast.Show(ex.Message,true); return; }
        await CheckForUpdatesAsync(() => GitHubUpdates.CheckAsync(settings,_closing.Token));
    }
    private async Task CheckForUpdatesAsync(Func<Task<UpdateCheckResult>> check)
    {
        _checkingUpdates = true;
        UpdateRequested?.Invoke();
        AvailableUpdateNotice.Dismiss();
        UpdateToast.Dismiss();
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        ImportButton.IsEnabled=BackupButton.IsEnabled=false;
        CheckUpdatesButton.SetResourceReference(ContentControl.ContentProperty,"I18n.CheckingUpdates");
        try
        {
            var result = await check();
            if (_closing.IsCancellationRequested) return;
            if (result is {Status:UpdateStatus.Available,Release:{ } release})
            {
                AvailableUpdate = release;
                ShowUpdatePrompt(release);
            }
            else
            {
                AvailableUpdate = null;
                UpdateToast.Show(result.Status switch {UpdateStatus.NoRelease=>Strings.NoUpdatePackage,
                    UpdateStatus.NotInstalled=>Strings.UpdatePortableRequired,_=>Strings.NoUpdates});
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            if (!_closing.IsCancellationRequested)
                UpdateToast.Show(ex is HttpRequestException {StatusCode:HttpStatusCode.TooManyRequests}
                    ? Strings.UpdateCheckRateLimited : Strings.UpdateCheckFailed,true);
        }
        finally
        {
            _checkingUpdates = false;
            if (!_closing.IsCancellationRequested)
            { CheckUpdatesButton.IsEnabled = InstallUpdateButton.IsEnabled = ImportButton.IsEnabled = BackupButton.IsEnabled = true; UpdateReleaseButton(); }
        }
    }
    private async void InstallUpdate_Click(object sender,RoutedEventArgs e)
    {
        if(_updateCancellation != null) {_updateCancellation.Cancel();return;}
        await InstallAvailableUpdateAsync();
    }
    internal async Task InstallAvailableUpdateAsync()
    {
        if(AvailableUpdate is not { } release || ApplyUpdate == null || !InstallUpdateButton.IsEnabled || _updateCancellation != null) return;
        UpdateRequested?.Invoke();
        AvailableUpdateNotice.Dismiss();
        ShowCategory(AboutPage);
        ConnectionSettings settings;
        try {settings=Draft(includeIntervals:false);}
        catch(ArgumentException ex) {UpdateToast.Show(ex.Message,true);return;}
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _updateCancellation=cancellation;
        ImportButton.IsEnabled=BackupButton.IsEnabled=SaveButton.IsEnabled=false;
        CheckUpdatesButton.IsEnabled=false;
        InstallUpdateButton.SetResourceReference(ContentControl.ContentProperty,"I18n.Cancel");
        UpdateToast.BeginProgress(Localization.Format(Strings.DownloadingUpdate,0));
        try
        {
            var progress=new Progress<int>(value=>
            {if(!cancellation.IsCancellationRequested) UpdateToast.UpdateProgress(Localization.Format(Strings.DownloadingUpdate,value));});
            _updateDownload=GitHubUpdates.DownloadAsync(release,settings,progress,cancellation.Token);
            await _updateDownload;
            cancellation.Token.ThrowIfCancellationRequested();
            InstallUpdateButton.IsEnabled=false;
            UpdateToast.UpdateProgress(Strings.ApplyingUpdate);
            await ApplyUpdate(release);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {if(!_closing.IsCancellationRequested) UpdateToast.CompleteProgress(Strings.UpdateCancelled);}
        catch(Exception)
        {if(!_closing.IsCancellationRequested) UpdateToast.CompleteProgress(Strings.UpdateInstallFailed,true,Strings.ViewUpdate,()=>OpenWebPage(release.Page));}
        finally
        {
            _updateCancellation=null;
            if(!_closing.IsCancellationRequested)
            {
                CheckUpdatesButton.IsEnabled=InstallUpdateButton.IsEnabled=true;
                ImportButton.IsEnabled=BackupButton.IsEnabled=SaveButton.IsEnabled=true;
                InstallUpdateButton.SetResourceReference(ContentControl.ContentProperty,"I18n.InstallUpdate");
            }
        }
    }
    private void GitHub_Click(object sender,RoutedEventArgs e) => OpenWebPage(new Uri(GitHubUpdates.RepositoryUrl));
    private void OpenWebPage(Uri page)
    {
        try { Process.Start(new ProcessStartInfo {FileName=page.AbsoluteUri,UseShellExecute=true}); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { UpdateToast.Show(Strings.OpenWebPageFailed,true); }
    }
    private ConnectionSettings Draft(bool includeIntervals=true)
    {
        if (includeIntervals && LaunchNeedsAccelerator) throw new ArgumentException(Strings.QuickLaunchChooseAccelerator);
        if (includeIntervals && !ValidOverlayLimit()) throw new ArgumentException(Strings.OverlayLimitError);
        var settings = OverlayAppearance(_current) with { UseProxy = ProxyEnabled.IsChecked == true, ProxyAddress = ProxyAddressBox.Text.Trim(),
            WindowCloseAction=(CloseAction)CloseActionBox.SelectedIndex, QuickLaunch=_launchDraft,
            OpenOverlayOnStartup=OverlayStartup.IsChecked==true,
            CheckForUpdatesOnStartup=StartupUpdateEnabled.IsChecked==true,
            Language=LanguageBox.SelectedValue as string ?? Localization.SystemLanguage,
            RefreshAfterMatch=RefreshAfterMatchEnabled.IsChecked==true,
            FocusLatestMatchAfterRefresh=FocusLatestMatchEnabled.IsChecked==true,
            MatchEndWaitSeconds=includeIntervals ? ConnectionSettings.ParseMatchEndSeconds(MatchEndWaitBox.Text) : _current.MatchEndWaitSeconds,
            MatchEndRetrySeconds=includeIntervals ? ConnectionSettings.ParseMatchEndSeconds(MatchEndRetryBox.Text,retry:true) : _current.MatchEndRetrySeconds,
            MatchEndSamplesPerSecond=includeIntervals ? ConnectionSettings.ParseMatchEndFrequency(MatchEndFrequencyBox.Text) : _current.MatchEndSamplesPerSecond,
            RefreshReminderSeconds=includeIntervals ? ConnectionSettings.ParseReminderSeconds(ReminderSecondsBox.Text) : _current.RefreshReminderSeconds,
            ApiIntervalSeconds=includeIntervals ? ConnectionSettings.ParseApiIntervalSeconds(ApiIntervalBox.Text) : _current.ApiIntervalSeconds,
            AutoRefreshSeconds=includeIntervals ? ConnectionSettings.ParseAutoRefreshSeconds(AutoRefreshSecondsBox.Text) : _current.AutoRefreshSeconds };
        if (settings.UseProxy) settings.ValidateProxy(); return settings;
    }
    private void ReminderSeconds_Changed(object sender,TextChangedEventArgs e) => MatchPreset(ReminderSecondsBox,ReminderPresets);
    private void AutoRefreshSeconds_Changed(object sender,TextChangedEventArgs e) => MatchPreset(AutoRefreshSecondsBox,AutoRefreshPresets);
    private void ApiInterval_Changed(object sender,TextChangedEventArgs e)
    {
        if(ApiIntervalBox==null || ApiIntervalWarning==null) return;
        var valid=double.TryParse(ApiIntervalBox.Text.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var seconds);
        ApiIntervalWarning.Visibility=valid && ConnectionSettings.LowApiInterval(seconds) ? Visibility.Visible : Visibility.Collapsed;
        if(_syncingIntervals || ApiIntervalPresets==null) return;
        _syncingIntervals=true;
        try {ApiIntervalPresets.SelectedItem=valid ? ApiIntervalPresets.Items.OfType<ListBoxItem>().FirstOrDefault(i=>double.Parse(i.Tag!.ToString()!,CultureInfo.InvariantCulture)==seconds) : null;}
        finally {_syncingIntervals=false;}
    }
    private void ApiIntervalPreset_Changed(object sender,SelectionChangedEventArgs e) => FillPreset(ApiIntervalBox,ApiIntervalPresets);
    private void MatchPreset(TextBox? input,ListBox? presets)
    {
        if(_syncingIntervals || input==null || presets==null) return;
        _syncingIntervals=true;
        try
        {
            var valid=int.TryParse(input.Text.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var seconds);
            presets.SelectedItem=valid ? presets.Items.OfType<ListBoxItem>().FirstOrDefault(i=>i.Tag?.ToString()==seconds.ToString(CultureInfo.InvariantCulture)) : null;
        }
        finally { _syncingIntervals=false; }
    }
    private void ReminderPreset_Changed(object sender,SelectionChangedEventArgs e) => FillPreset(ReminderSecondsBox,ReminderPresets);
    private void AutoRefreshPreset_Changed(object sender,SelectionChangedEventArgs e) => FillPreset(AutoRefreshSecondsBox,AutoRefreshPresets);
    private void FillPreset(TextBox? input,ListBox? presets)
    {
        if(_syncingIntervals || input==null || presets?.SelectedItem is not ListBoxItem {Tag:string seconds}) return;
        _syncingIntervals=true;
        try { input.Text=seconds; }
        finally { _syncingIntervals=false; }
    }
    private void ShowError(string? message) { ErrorText.Text = message ?? ""; ErrorText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible; }
    private void SetTransferBusy(bool busy)
    { BackupButton.IsEnabled=ImportButton.IsEnabled=SaveButton.IsEnabled=InstallUpdateButton.IsEnabled=CheckUpdatesButton.IsEnabled=!busy; }
    private async void Backup_Click(object sender,RoutedEventArgs e)
    {
        var stamp=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss");
        var dialog=new SaveFileDialog {Title=Strings.BackupDatabase,FileName=$"SF6-{stamp}.sqlite",DefaultExt=".sqlite",Filter=Strings.DatabaseFileFilter,AddExtension=true,OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=true) return;
        await (_transfer=TransferAsync(dialog.FileName,false));
    }
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog {Title=Strings.ImportDatabase,Filter=Strings.DatabaseFileFilter,CheckFileExists=true,Multiselect=false};
        if(dialog.ShowDialog(this)!=true) return;
        await (_transfer=TransferAsync(dialog.FileName,true));
    }
    private async Task TransferAsync(string path,bool import)
    {
        SetTransferBusy(true); ShowError(null); DatabaseStatus.Text=import ? Strings.Importing : Strings.BackingUp;
        try
        {
            var database=Path.Combine(App.DataDirectory,"battles.sqlite");
            if(import)
            {
                if(BeforeImport!=null) await BeforeImport();
                var result=await DatabaseTransfer.ImportAsync(path,database,Path.Combine(App.DataDirectory,"Backups"),_closing.Token);
                DatabaseStatus.Text=Localization.Format(Strings.MatchesAdded, result.Added);
                DatabaseStatus.ToolTip=Localization.Format(Strings.ImportDetails, result.Existing, result.BackupPath);
                DatabaseImported?.Invoke();
            }
            else
            {
                await DatabaseTransfer.BackupAsync(database,path,_closing.Token);
                DatabaseStatus.Text=Strings.BackedUp; DatabaseStatus.ToolTip=path;
            }
        }
        catch(OperationCanceledException) when(_closing.IsCancellationRequested) { }
        catch(Exception ex)
        {
            DatabaseStatus.Text="";
            if(!_closing.IsCancellationRequested) ShowError(ex is ArgumentException or InvalidDataException ? ex.Message :
                import ? Strings.ImportFailed : Strings.BackupFailed);
        }
        finally { SetTransferBusy(false); }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if(!_transfer.IsCompleted || !_updateDownload.IsCompleted)
        {
            e.Cancel=true; _closing.Cancel();
            if(!_waitingToClose) { _waitingToClose=true; _=CloseAfterTransferAsync(); }
        }
        base.OnClosing(e);
    }
    private async Task CloseAfterTransferAsync() { await PendingTransfer; Close(); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { Result = Draft(); DialogResult = true; }
        catch (ArgumentException ex)
        {
            var page = InvalidPage();
            if (page != null) ShowCategory(page);
            if (page == OverlayPage)
            {
                ShowError(null);
                MatchLimitError.Visibility = Visibility.Visible;
                OverlayLimit.Focus();
            }
            else ShowError(ex.Message);
        }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        ConnectionSettings settings;
        try { settings = Draft(includeIntervals:false); ShowError(null); } catch (ArgumentException ex) { ShowError(ex.Message); return; }
        TestButton.IsEnabled = false; TestResult.Text = Strings.Testing;
        try
        {
            var ms = await settings.TestGoogleAsync(_closing.Token);
            TestResult.Text = $"{ms:N0} ms";
            File.WriteAllText(Path.Combine(App.DataDirectory, "proxy-test.json"), JsonSerializer.Serialize(new { success = true, milliseconds = ms, proxy = settings.UseProxy, endpoint = "https://www.google.com/generate_204", at = DateTimeOffset.UtcNow }));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return; }
        catch (Exception ex) { TestResult.Text = ex is TaskCanceledException ? Strings.ConnectionTimedOut : Strings.ConnectionFailed; }
        TestButton.IsEnabled = true;
    }
}
