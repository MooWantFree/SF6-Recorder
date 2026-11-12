using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Sf6Prototype;

public sealed class LoginWindow : Window
{
    private readonly WebView2 _browser = new();
    private readonly TextBlock _address = new() { Margin = new Thickness(12), Text = "正在打开 CAPCOM 官网…", TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TaskCompletionSource<SavedSession?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConnectionSettings _settings;
    private readonly CookieVault _vault;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly string _nonce = Guid.NewGuid().ToString("N");
    private readonly List<Window> _popups = [];
    private CoreWebView2Environment? _environment;
    private bool _checking, _finishing, _closed;

    private LoginWindow(Window owner, ConnectionSettings settings, CookieVault vault)
    {
        Owner = owner; _settings = settings; _vault = vault;
        Icon = (System.Windows.Media.ImageSource)FindResource("AppIcon");
        Title = "登录 CAPCOM ID"; Width = 1100; Height = 820; MinWidth = 800; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        grid.Children.Add(_address); Grid.SetRow(_browser, 1); grid.Children.Add(_browser); Content = grid;
        Loaded += async (_, _) => await InitializeAsync();
        _timer.Tick += async (_, _) => await CheckAsync();
    }
    public static async Task<SavedSession?> SignInAsync(Window owner, ConnectionSettings settings, CookieVault vault)
    {
        var dialog = new LoginWindow(owner, settings, vault); dialog.Show();
        return await dialog._completion.Task;
    }
    private async Task InitializeAsync()
    {
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDirectory, "WebView2"), new CoreWebView2EnvironmentOptions(_settings.BrowserArguments));
            if (_closed) return;
            await _browser.EnsureCoreWebView2Async(_environment);
            // Explicit login always starts with a clean browser identity. Account sessions are
            // independently persisted in the DPAPI vault, never shared through browser SSO.
            await _browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            Configure(_browser.CoreWebView2);
            _browser.CoreWebView2.WebMessageReceived += Receive;
            _browser.CoreWebView2.NavigationCompleted += async (_, _) => await CheckAsync();
            _browser.CoreWebView2.Navigate($"{BucklerClient.Origin}/6/buckler/zh-hans/profile/{_settings.PlayerId}/battlelog");
            _timer.Start();
        }
        catch (Exception ex)
        {
            _address.Text = $"登录窗口无法启动：{ex.GetType().Name}。请检查 WebView2 Runtime 和代理设置。";
        }
    }
    private void Configure(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") { e.Cancel = true; return; }
            _address.Text = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath;
        };
        core.NewWindowRequested += async (_, e) =>
        {
            using var deferral = e.GetDeferral(); e.Handled = true;
            if (_closed || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
            var view = new WebView2();
            var popup = new Window { Owner = this, Icon = this.Icon, Title = "CAPCOM 官方登录", Width = 1000, Height = 800, Content = view, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            _popups.Add(popup);
            popup.Closed += (_, _) => { view.Dispose(); _popups.Remove(popup); if (!_closed && !_finishing) _browser.CoreWebView2.Reload(); };
            popup.Show();
            try
            {
                await view.EnsureCoreWebView2Async(_environment); Configure(view.CoreWebView2);
                view.CoreWebView2.WindowCloseRequested += (_, _) => popup.Close(); e.NewWindow = view.CoreWebView2;
            }
            catch { popup.Close(); _address.Text = "登录弹窗未能打开，请重试。"; }
        };
        core.DownloadStarting += (_, e) => e.Cancel = true;
    }
    private async Task CheckAsync()
    {
        if (_closed || _checking || _finishing || _browser.CoreWebView2 == null ||
            !Uri.TryCreate(_browser.CoreWebView2.Source, UriKind.Absolute, out var uri) || uri.Host != "www.streetfighter.com" || !uri.AbsolutePath.StartsWith("/6/buckler/")) return;
        _checking = true;
        try
        {
            await _browser.CoreWebView2.ExecuteScriptAsync($$$$"""
            (() => {
              const node=document.getElementById('__NEXT_DATA__'); if(!node) return;
              const data=JSON.parse(node.textContent); if(!data.buildId) return;
              fetch('/6/buckler/api/auth/getlogindata',{credentials:'same-origin',cache:'no-store',signal:AbortSignal.timeout(12000)})
              .then(r=>r.ok?r.json():null).then(d=>{
                const user=d?.loginUser;
                if(user?.shortId) window.chrome.webview.postMessage({nonce:{{{{JsonSerializer.Serialize(_nonce)}}}},id:String(user.shortId),name:user.fighterId||'',buildId:data.buildId});
                else {
                  const link=[...document.querySelectorAll('a[href]')].find(a=>a.href.includes('/auth/loginep'));
                  if(link) location.href=link.href;
                }
              }).catch(()=>{});
            })();
            """);
        }
        catch { }
        finally { _checking = false; }
    }
    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || _finishing || !Uri.TryCreate(e.Source, UriKind.Absolute, out var uri) || uri.Host != "www.streetfighter.com" || uri.Scheme != "https") return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson); var data = doc.RootElement;
            if (!data.TryGetProperty("nonce", out var nonce) || nonce.GetString() != _nonce) return;
            var id = data.GetProperty("id").GetString()!;
            if (id.Length != 10) return;
            _finishing = true;
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(BucklerClient.Origin);
            var list = cookies.Where(c => CookieVault.IsSiteDomain(c.Domain)).Select(c =>
                new SavedCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSession ? DateTime.MinValue : c.Expires, c.IsSecure, c.IsHttpOnly)).ToList();
            var session = new SavedSession(id, data.GetProperty("name").GetString()!, _browser.CoreWebView2.Settings.UserAgent,
                data.GetProperty("buildId").GetString()!, _settings.RouteKey, list);
            _vault.Save(session);
            await _browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            _completion.TrySetResult(session); Close();
        }
        catch { _finishing = false; _address.Text = "登录状态未能保存，请重试。"; }
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true; _timer.Stop(); foreach (var p in _popups.ToArray()) p.Close();
        _browser.Dispose(); _completion.TrySetResult(null); base.OnClosed(e);
    }
}
