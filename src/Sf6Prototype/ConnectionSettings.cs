using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using System.Globalization;

namespace Sf6Prototype;

public enum CloseAction { Ask, MinimizeToTray, Exit }

public record ConnectionSettings
{
    public const int DefaultRefreshReminderSeconds = 24 * 60 * 60;
    public const double DefaultApiIntervalSeconds = 5;
    public double ApiIntervalSeconds { get; init; } = DefaultApiIntervalSeconds;
    public static bool ValidApiInterval(double seconds) => double.IsFinite(seconds) && seconds is >=0.1 and <=3600;
    public static bool LowApiInterval(double seconds) => ValidApiInterval(seconds) && seconds<DefaultApiIntervalSeconds;
    public int RefreshReminderSeconds { get; init; } = DefaultRefreshReminderSeconds;
    public int AutoRefreshSeconds { get; init; }
    public CloseAction WindowCloseAction { get; init; }
    public bool UseProxy { get; init; }
    public string ProxyAddress { get; init; } = "http://127.0.0.1:7890";
    public string PlayerId { get; init; } = "3396336472";
    public string RouteKey => UseProxy ? ValidateProxy().AbsoluteUri : "direct";
    public Uri ValidateProxy()
    {
        if (!Uri.TryCreate(ProxyAddress.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host) || uri.Port <= 0 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入 http://主机:端口 或 socks5://主机:端口；此版本支持无认证代理。");
        return uri;
    }
    public string BrowserArguments => UseProxy
        ? $"--proxy-server={ValidateProxy().GetLeftPart(UriPartial.Authority)} --proxy-bypass-list=<-loopback>"
        : "--no-proxy-server";
    public HttpClientHandler CreateHandler(CookieContainer? cookies = null) => new()
    {
        UseProxy = UseProxy, Proxy = UseProxy ? new WebProxy(ValidateProxy()) { BypassProxyOnLocal = false } : null,
        UseCookies = cookies != null, CookieContainer = cookies ?? new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false
    };
    public async Task<long> TestGoogleAsync(CancellationToken token)
    {
        using var client = new HttpClient(CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };
        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync("https://www.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException($"Google 返回 HTTP {(int)response.StatusCode}");
        return watch.ElapsedMilliseconds;
    }
    public static ConnectionSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new();
        var settings=JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(path)) ?? new();
        return settings with
        {
            RefreshReminderSeconds=settings.RefreshReminderSeconds>=0 ? settings.RefreshReminderSeconds : DefaultRefreshReminderSeconds,
            AutoRefreshSeconds=Math.Max(0,settings.AutoRefreshSeconds),
            ApiIntervalSeconds=ValidApiInterval(settings.ApiIntervalSeconds) ? settings.ApiIntervalSeconds : DefaultApiIntervalSeconds,
            WindowCloseAction=Enum.IsDefined(settings.WindowCloseAction) ? settings.WindowCloseAction : CloseAction.Ask
        };
    }
    public static int ParseReminderSeconds(string value)
    {
        return ParseIntervalSeconds(value,"更新提醒");
    }
    public static int ParseAutoRefreshSeconds(string value) => ParseIntervalSeconds(value,"自动刷新");
    public static double ParseApiIntervalSeconds(string value)
    {
        if(!double.TryParse(value.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var seconds) || !ValidApiInterval(seconds))
            throw new ArgumentException("卡普空请求间隔请输入 0.1–3600 秒，可用小数。");
        return seconds;
    }
    private static int ParseIntervalSeconds(string value,string name)
    {
        if(!int.TryParse(value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var seconds))
            throw new ArgumentException($"{name}请输入非负整数秒数，0 表示关闭。");
        return seconds;
    }
    public void Save(string directory)
    {
        if(RefreshReminderSeconds<0) throw new ArgumentException("更新提醒请输入非负整数秒数，0 表示关闭。");
        if(AutoRefreshSeconds<0) throw new ArgumentException("自动刷新请输入非负整数秒数，0 表示关闭。");
        if(!ValidApiInterval(ApiIntervalSeconds)) throw new ArgumentException("卡普空请求间隔请输入 0.1–3600 秒，可用小数。");
        if(!Enum.IsDefined(WindowCloseAction)) throw new ArgumentException("请选择关闭窗口时的操作。");
        if (UseProxy) ValidateProxy();
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
