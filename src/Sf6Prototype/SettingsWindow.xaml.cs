using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using System.ComponentModel;
using Microsoft.Win32;

namespace Sf6Prototype;

public partial class SettingsWindow : Window
{
    private readonly ConnectionSettings _current;
    private readonly CancellationTokenSource _closing = new();
    private bool _syncingIntervals;
    private Task _transfer=Task.CompletedTask;
    private bool _waitingToClose;
    public Func<Task>? BeforeImport { get; set; }
    public event Action? DatabaseImported;
    public Task PendingTransfer=>_transfer;
    public void CancelTransfer()=>_closing.Cancel();
    public ConnectionSettings? Result { get; private set; }
    public SettingsWindow(Window owner, ConnectionSettings current)
    {
        InitializeComponent(); Owner = owner; _current = current;
        ProxyEnabled.IsChecked = current.UseProxy; ProxyAddressBox.Text = current.ProxyAddress;
        ReminderSecondsBox.Text=current.RefreshReminderSeconds.ToString(CultureInfo.InvariantCulture);
        AutoRefreshSecondsBox.Text=current.AutoRefreshSeconds.ToString(CultureInfo.InvariantCulture);
        ApiIntervalBox.Text=current.ApiIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        CloseActionBox.SelectedIndex=(int)current.WindowCloseAction;
        ContentRendered += (_, _) => UiCapture.SaveLater((FrameworkElement)Content, "ui-settings.png");
        Closed += (_, _) => { _closing.Cancel(); _closing.Dispose(); };
    }
    private ConnectionSettings Draft(bool includeIntervals=true)
    {
        var settings = _current with { UseProxy = ProxyEnabled.IsChecked == true, ProxyAddress = ProxyAddressBox.Text.Trim(),
            WindowCloseAction=(CloseAction)CloseActionBox.SelectedIndex,
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
    { BackupButton.IsEnabled=ImportButton.IsEnabled=SaveButton.IsEnabled=!busy; }
    private async void Backup_Click(object sender,RoutedEventArgs e)
    {
        var stamp=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd-HHmmss");
        var dialog=new SaveFileDialog {Title="备份数据库",FileName=$"SF6-{stamp}.sqlite",DefaultExt=".sqlite",Filter="SQLite 数据库|*.sqlite;*.db",AddExtension=true,OverwritePrompt=true};
        if(dialog.ShowDialog(this)!=true) return;
        await (_transfer=TransferAsync(dialog.FileName,false));
    }
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog {Title="导入数据库",Filter="SQLite 数据库|*.sqlite;*.db",CheckFileExists=true,Multiselect=false};
        if(dialog.ShowDialog(this)!=true) return;
        await (_transfer=TransferAsync(dialog.FileName,true));
    }
    private async Task TransferAsync(string path,bool import)
    {
        SetTransferBusy(true); ShowError(null); DatabaseStatus.Text=import ? "导入中…" : "备份中…";
        try
        {
            var database=Path.Combine(App.DataDirectory,"battles.sqlite");
            if(import)
            {
                if(BeforeImport!=null) await BeforeImport();
                var result=await DatabaseTransfer.ImportAsync(path,database,Path.Combine(App.DataDirectory,"Backups"),_closing.Token);
                DatabaseStatus.Text=$"新增 {result.Added:N0} 场";
                DatabaseStatus.ToolTip=$"已跳过 {result.Existing:N0} 场重复战绩\n导入前备份：{result.BackupPath}";
                DatabaseImported?.Invoke();
            }
            else
            {
                await DatabaseTransfer.BackupAsync(database,path,_closing.Token);
                DatabaseStatus.Text="已备份"; DatabaseStatus.ToolTip=path;
            }
        }
        catch(OperationCanceledException) when(_closing.IsCancellationRequested) { }
        catch(Exception ex)
        {
            DatabaseStatus.Text="";
            if(!_closing.IsCancellationRequested) ShowError(ex is ArgumentException or InvalidDataException ? ex.Message :
                import ? "导入失败，请检查数据库文件和目录权限。现有战绩未被替换。" : "备份失败，请检查保存位置和目录权限。");
        }
        finally { SetTransferBusy(false); }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if(!_transfer.IsCompleted)
        {
            e.Cancel=true; _closing.Cancel();
            if(!_waitingToClose) { _waitingToClose=true; _=CloseAfterTransferAsync(); }
        }
        base.OnClosing(e);
    }
    private async Task CloseAfterTransferAsync() { await _transfer; Close(); }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { Result = Draft(); DialogResult = true; }
        catch (ArgumentException ex) { ShowError(ex.Message); }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        ConnectionSettings settings;
        try { settings = Draft(includeIntervals:false); ShowError(null); } catch (ArgumentException ex) { ShowError(ex.Message); return; }
        TestButton.IsEnabled = false; TestResult.Text = "测试中…";
        try
        {
            var ms = await settings.TestGoogleAsync(_closing.Token);
            TestResult.Text = $"{ms:N0} ms";
            File.WriteAllText(Path.Combine(App.DataDirectory, "proxy-test.json"), JsonSerializer.Serialize(new { success = true, milliseconds = ms, proxy = settings.UseProxy, endpoint = "https://www.google.com/generate_204", at = DateTimeOffset.UtcNow }));
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return; }
        catch (Exception ex) { TestResult.Text = ex is TaskCanceledException ? "连接超时" : "连接失败，请检查代理设置"; }
        TestButton.IsEnabled = true;
    }
}
