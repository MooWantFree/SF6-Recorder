using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class MainWindow : Window
{
    private BattleStore _store = null!;
    private CookieVault _vault = null!;
    private ProfileCache _profiles = null!;
    private ConnectionSettings _settings = new();
    private List<Battle> _all = [], _filtered = [];
    private bool _ready, _busy, _closeRequested;
    private int _page;
    private CancellationTokenSource? _cancellation;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _stage = "starting";
    private SyncResult? _lastSync;
    private const int PageSize = 50;

    public MainWindow()
    {
        InitializeComponent();
        AccountPopup.Opened += (_,_) => UiCapture.SaveLater((FrameworkElement)AccountPopup.Child,"ui-account-menu.png");
        Loaded += async (_, _) => await InitializeAsync();
        PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.F) { Navigation.SelectedIndex = 0; SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.R && !_busy) { e.Handled = true; await SynchronizeAsync(); }
            else if (e.Key == Key.OemComma) { Navigation.SelectedIndex = 2; e.Handled = true; }
        };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
    }
    private async Task InitializeAsync()
    {
        _settings = ConnectionSettings.Load(App.DataDirectory);
        _vault = new CookieVault(App.DataDirectory);
        _profiles = new ProfileCache(App.DataDirectory);
        _vault.Accounts();
        _store = new BattleStore(Path.Combine(App.DataDirectory, "battles.sqlite"));
        CategoryFilter.ItemsSource = new[] { new BattleCategory("", "全部分类") }.Concat(BattleCategory.All);
        CategoryFilter.SelectedIndex = 0;
        OutcomeFilter.ItemsSource = new[] { "全部结果", "胜", "负", "平", "待确认" }; OutcomeFilter.SelectedIndex = 0;
        ProxyEnabled.IsChecked = _settings.UseProxy; ProxyAddressBox.Text = _settings.ProxyAddress;
        var session = _vault.Load();
        if (session != null) _settings = _settings with { PlayerId = session.AccountId };
        RefreshAccountUi();
        _ready = true; ReloadLocal();
        var lastUpdated = _profiles.Profile(_settings.PlayerId)?.UpdatedAt;
        if (lastUpdated != null) UpdatedLabel.Text = "更新于 " + lastUpdated.Value.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm");
        ProxyAddressBox.ToolTip = "支持无认证 HTTP、HTTPS 和 SOCKS5 代理";
        ScoreTitle.ToolTip = "每点对应一场对局，按顺序等距排列。差值相对于上一条已保存的同角色记录，可能跨越未收录的对局。";
        SetStatus("ready", "就绪 · 浏览和筛选使用本地数据");
        if (App.AutoSync) await SynchronizeAsync();
        if (App.Capture) { await Task.Delay(350); Capture(); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; LoginButton.IsEnabled = SyncButton.IsEnabled = SettingsPanel.IsEnabled = !busy;
        SyncButton.Content = busy ? "刷新中…" : "刷新";
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SyncProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        AccountPopup.IsOpen = false;
        SetBusy(true);
        try
        {
            var session = await LoginWindow.SignInAsync(this, _settings, _vault);
            if (session == null) { SetStatus("login_cancelled", "登录已取消，本地战绩仍可查看。"); return; }
            SetActiveAccount(session); ReloadLocal();
            using var client = new BucklerClient(_settings, session, _vault);
            try { await _profiles.RefreshAsync(client,session.AccountId,CancellationToken.None); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("profile_pending", "登录已保存；头像暂未更新，下次刷新重试。"); RefreshAccountUi(); return; }
            RefreshAccountUi(); ReloadLocal(); SetStatus("logged_in", "登录已保存。");
        }
        catch (Exception ex) { ReportError(ex); }
        finally { SetBusy(false); if (_closeRequested) Close(); }
    }
    private void RefreshAccountUi()
    {
        var session = _vault.Load();
        var info = session == null ? null : _profiles.Profile(session.AccountId);
        AccountName.Text = info?.Name ?? session?.AccountName ?? "登录";
        var picture = Pictures.Load(info?.AvatarPath);
        AccountAvatar.Fill = picture == null ? Brushes.Transparent : new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        AccountLabel.Text = session == null ? "离线档案" : $"{AccountName.Text} · 本地战绩";
        AccountsList.ItemsSource = _vault.Accounts().Select(s => new AccountChoice(s.AccountId,_profiles.Profile(s.AccountId)?.Name ?? s.AccountName,
            Pictures.Load(_profiles.Profile(s.AccountId)?.AvatarPath),s.AccountId==session?.AccountId)).ToList();
    }
    private void SetActiveAccount(SavedSession session)
    {
        _settings = _settings with { PlayerId = session.AccountId }; _settings.Save(App.DataDirectory);
        _ready = false; DateRange.SetRange(null,null); SearchBox.Text = ""; CategoryFilter.SelectedIndex = OutcomeFilter.SelectedIndex = 0; _ready = true;
        RefreshAccountUi();
    }
    private void Account_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_vault.Load() == null && _vault.Accounts().Count == 0) { Login_Click(sender,e); return; }
        RefreshAccountUi(); AccountPopup.IsOpen = !AccountPopup.IsOpen;
    }
    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as Button)?.Tag is not string id) return;
        try { SetActiveAccount(_vault.Activate(id)); AccountPopup.IsOpen = false; ReloadLocal(); SetStatus("account_switched","已切换账号。"); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as Button)?.Tag is not string id) return;
        var session = _vault.Accounts().FirstOrDefault(s=>s.AccountId==id); if (session==null) return;
        AccountPopup.IsOpen = false;
        if (MessageBox.Show(this,$"是否登出「{session.AccountName}」？\n\n将移除此设备保存的登录，本地战绩保留。","登出账号",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
        var active = _vault.Load()?.AccountId == id; _vault.Remove(id);
        if (active && _vault.Accounts().FirstOrDefault() is { } next) SetActiveAccount(_vault.Activate(next.AccountId));
        RefreshAccountUi(); ReloadLocal(); SetStatus("logged_out","账号已登出，本地战绩已保留。");
    }
    private async void Sync_Click(object sender, RoutedEventArgs e) => await SynchronizeAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        foreach (var window in OwnedWindows.Cast<Window>().ToArray()) window.Close();
    }
    private async Task SynchronizeAsync(bool full = false)
    {
        if (_busy || !_ready) return;
        SetBusy(true); _cancellation = new CancellationTokenSource(); _lastSync = null;
        try
        {
            var session = _vault.Load();
            if (session == null)
            {
                SetStatus("login_needed", "请完成 CAPCOM 登录。");
                session = await LoginWindow.SignInAsync(this, _settings, _vault);
                if (session == null) { SetStatus("login_cancelled", "登录已取消。"); return; }
                SetActiveAccount(session);
            }
            _cancellation.Token.ThrowIfCancellationRequested();
            using var client = new BucklerClient(_settings, session, _vault);
            SetStatus("validating", "正在检查登录状态…");
            await client.ValidateAsync(_cancellation.Token);
            RefreshAccountUi();
            var progress = new Progress<SyncProgress>(p =>
            {
                SetStatus("syncing", $"{p.Category} · 第 {p.Page}/{Math.Max(1,p.TotalPages)} 页 · 新增 {p.Inserted} 条 · {p.Message}");
            });
            _lastSync = await new SyncService(_store, client).RunAsync(_settings.PlayerId, progress, _cancellation.Token, full);
            var profileMessage = "";
            try
            {
                SetStatus("profiles","正在更新角色资料和头像…");
                await _profiles.RefreshAsync(client,_settings.PlayerId,_cancellation.Token,message=>SetStatus("profiles",message));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            { profileMessage = " · 头像更新未完成，下次刷新重试"; }
            _lastSync = _lastSync with { Requests = client.Requests };
            ReloadLocal(); RefreshAccountUi();
            SetStatus("sync_complete", $"刷新完成 · 新增 {_lastSync.Inserted} 条 · {_lastSync.Pages} 页 · {_lastSync.Requests} 次 HTTP 请求{profileMessage}");
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        { ReloadLocal(); SetStatus("cancelled", "同步已取消，已获取的战绩已保存；下次会继续检查尚未完成的分类。"); }
        catch (Exception ex) { ReloadLocal(); ReportError(ex); }
        finally
        {
            _cancellation.Dispose(); _cancellation = null; SetBusy(false);
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
            TaskCanceledException => "请求超时，请检查代理或网络连接后重试。",
            _ => $"操作失败：{ex.GetType().Name}。已保存的数据不会丢失。"
        };
        SetStatus(ex is LoginRequiredException ? "login_required" : "error", text);
    }
    private void ReloadLocal()
    {
        var oldCharacter = CharacterFilter.SelectedItem as string;
        var oldScore = (CharacterGrid.SelectedItem as CharacterChoice)?.Id;
        _all = _store.Query(new(_settings.PlayerId));
        _ready = false;
        CharacterFilter.ItemsSource = new[] { "全部角色" }.Concat(_all.Select(b => b.Character).Distinct().Order());
        CharacterFilter.SelectedItem = oldCharacter != null && _all.Any(b => b.Character == oldCharacter) ? oldCharacter : "全部角色";
        var summaries = BattleStore.Summaries(_all);
        var choices = summaries.Select(s=>new CharacterChoice(s,Pictures.Load(_profiles.CharacterImage(s.CharacterId,s.Character)))).ToList();
        CharacterGrid.ItemsSource = choices;
        CharacterGrid.SelectedItem = choices.FirstOrDefault(s => s.Id == oldScore) ?? choices.FirstOrDefault();
        ArchiveCount.Text = $"{_all.Count:N0} 条战绩";
        RangeMetric.Text = _all.Count == 0 ? "尚无记录" : $"{_all[^1].Time[..10]} — {_all[0].Time[..10]}";
        _ready = true; ApplyFilter(); UpdateScores();
    }
    private void ApplyFilter()
    {
        if (!_ready) return;
        static long? Epoch(DateTime? value) => value == null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var cat = (CategoryFilter.SelectedItem as BattleCategory)?.Key;
        var character = CharacterFilter.SelectedIndex > 0 ? CharacterFilter.SelectedItem as string : null;
        var outcome = OutcomeFilter.SelectedIndex > 0 ? OutcomeFilter.SelectedItem as string : null;
        _filtered = _store.Query(new(_settings.PlayerId, string.IsNullOrEmpty(cat) ? null : cat, character, outcome, SearchBox.Text,
            Epoch(DateRange.Start), Epoch(DateRange.End?.AddDays(1))));
        var wins = _filtered.Count(b => b.Outcome == "胜"); var losses = _filtered.Count(b => b.Outcome == "负"); var draws = _filtered.Count(b => b.Outcome == "平");
        TotalMetric.Text = _filtered.Count.ToString("N0");
        WinRateMetric.Text = wins + losses + draws == 0 ? "—" : $"{100d * wins / (wins + losses + draws):F1}%";
        ResultsMetric.Text = $"{wins} / {losses} / {draws}";
        _page = 0; ShowPage();
    }
    private void ShowPage()
    {
        var count = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize));
        _page = Math.Clamp(_page, 0, count - 1);
        var images = new Dictionary<string,ImageSource?>();
        ImageSource? GetImage(int id,string name)
        {
            var path = _profiles.CharacterImage(id,name); if(path == null) return null;
            if(!images.TryGetValue(path,out var value)) { value = Pictures.Load(path); images[path] = value; } return value;
        }
        MatchesGrid.ItemsSource = _filtered.Skip(_page * PageSize).Take(PageSize).Select(b=>new MatchListRow(b,GetImage(b.CharacterId,b.Character),GetImage(-1,b.OpponentCharacter))).ToList();
        PageLabel.Text = $"{_page + 1} / {count} · {_filtered.Count:N0} 条";
        PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = _page + 1 < count;
        EmptyHint.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _all.Count == 0 ? "暂无战绩，点击「刷新」开始保存。" : "没有符合筛选条件的战绩。";
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Date_Changed(object? sender, EventArgs e) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) { _searchTimer.Stop(); _searchTimer.Start(); } }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false; CategoryFilter.SelectedIndex = CharacterFilter.SelectedIndex = OutcomeFilter.SelectedIndex = 0;
        DateRange.SetRange(null,null); SearchBox.Text = ""; _ready = true; ApplyFilter();
    }
    private void Previous_Click(object sender, RoutedEventArgs e) { _page--; ShowPage(); }
    private void Next_Click(object sender, RoutedEventArgs e) { _page++; ShowPage(); }
    private void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryView == null || ScoresView == null || SettingsView == null) return;
        HistoryView.Visibility = Navigation.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScoresView.Visibility = Navigation.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = Navigation.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = Navigation.SelectedIndex switch { 1 => "角色分数", 2 => "连接设置", _ => "战绩总览" };
        if (App.Capture && _ready) Dispatcher.BeginInvoke(new Action(Capture), DispatcherPriority.ContextIdle);
    }
    private void ScoreCharacter_Changed(object sender, SelectionChangedEventArgs e) => UpdateScores();
    private void Metric_Changed(object sender, SelectionChangedEventArgs e) => UpdateScores();
    private void UpdateScores()
    {
        if (!_ready || Chart == null) return;
        var character = (CharacterGrid.SelectedItem as CharacterChoice)?.Summary;
        var points = character == null ? [] : BattleStore.Scores(_all.Where(b => b.CharacterId == character.CharacterId));
        var count = int.TryParse((ScoreCount.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var n) ? n : 20;
        var recent = DateRanges.Recent(points,count);
        ScoreTitle.Text = character == null ? "角色分数" : $"{character.Character} · {recent.Count} 条分数记录";
        CharacterStats.Text = character == null ? "仅显示本地战绩中使用过的角色" : $"共 {character.Count} 场 · 胜率 {character.WinRate} · 最新记录 {character.LpText} LP";
        ScoreGrid.ItemsSource = recent.Reverse().ToList();
        Chart.Points = recent; Chart.ShowMr = ScoreMetric.SelectedIndex == 1; Chart.InvalidateVisual();
    }
    private ConnectionSettings DraftSettings()
    {
        var settings = new ConnectionSettings { PlayerId = _settings.PlayerId, UseProxy = ProxyEnabled.IsChecked == true, ProxyAddress = ProxyAddressBox.Text.Trim() };
        if (settings.UseProxy) settings.ValidateProxy(); return settings;
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = DraftSettings(); _settings.Save(App.DataDirectory); ReloadLocal();
            RefreshAccountUi();
            SetStatus("settings_saved", _settings.UseProxy ? "设置已保存。下次登录和同步均通过所选代理。" : "设置已保存。下次登录和同步均直连。");
        }
        catch (Exception ex) { ReportError(ex); }
    }
    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        TestProxyButton.IsEnabled = false; ProxyTestResult.Text = "测试中…";
        try
        {
            var settings = DraftSettings();
            var ms = await settings.TestGoogleAsync(CancellationToken.None);
            ProxyTestResult.Text = $"{ms:N0} ms";
            File.WriteAllText(Path.Combine(App.DataDirectory,"proxy-test.json"), JsonSerializer.Serialize(new { success=true, milliseconds=ms, proxy=settings.UseProxy, endpoint="https://www.google.com/generate_204", at=DateTimeOffset.UtcNow }));
        }
        catch (Exception ex)
        {
            ProxyTestResult.Text = ex is TaskCanceledException ? "连接超时" : "连接失败，请检查代理设置";
        }
        finally { TestProxyButton.IsEnabled = true; }
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { App.DataDirectory } });
    private void Match_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MatchesGrid.SelectedItem is not MatchListRow row) return;
        var b = row.Battle;
        var text = $"{b.Time} · {b.Mode}\n\n{b.PlayerName}（{b.Character}）  {b.Score}  {b.Opponent}（{b.OpponentCharacter}）\n\n结果：{b.Outcome}\n自己的 LP：{b.LpText}    MR：{b.MrText}\n对手 LP：{b.OpponentLp?.ToString("N0") ?? "—"}    MR：{(b.OpponentMr > 0 ? b.OpponentMr.Value.ToString("N0") : "—")}\n回放 ID：{b.ReplayId}";
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Margin = new Thickness(22), Background = Brushes.White };
        new Window { Owner = this, Title = "战绩详情", Width = 650, Height = 350, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private void SetStatus(string stage, string message)
    {
        _stage = stage;
        StatusText.Text = stage switch
        {
            "error" => message.Contains("限流") ? "请求过于频繁，请稍后刷新。" : message.StartsWith("请输入") ? message : "暂时无法更新，请检查网络后重试。",
            "login_required" => "登录已失效，请重新登录。",
            "login_needed" => "请先登录。",
            "settings_saved" => "已保存",
            "cancelled" => "已取消",
            "profile_pending" => "头像暂未更新，可稍后刷新。",
            "sync_complete" when message.Contains("头像更新未完成") => "战绩已更新，头像可稍后刷新。",
            _ => ""
        };
        StatusText.Visibility = StatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if(stage=="sync_complete") UpdatedLabel.Text = "更新于 " + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm");
        var path = Path.Combine(App.DataDirectory,"status.json");
        var value = new { pid=Environment.ProcessId, stage, playerId=_settings.PlayerId, transport="HttpClient", storedMatches=_all.Count,
            lastSync=_lastSync, updatedAt=DateTimeOffset.UtcNow, status=message };
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,new JsonSerializerOptions { WriteIndented=true }));
        File.Move(path+".tmp",path,true);
    }
    private void Capture()
    {
        if (Content is not FrameworkElement element || element.ActualWidth <= 0) return;
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(element); var png=new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(App.DataDirectory,$"ui-{Navigation.SelectedIndex}.png")); png.Save(file);
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy)
        {
            _closeRequested = true; e.Cancel=true; _cancellation?.Cancel();
            foreach(var w in OwnedWindows.Cast<Window>().ToArray()) w.Close();
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { _searchTimer.Stop(); base.OnClosed(e); }
}

