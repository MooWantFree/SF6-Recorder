using System.IO;
using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;

namespace Sf6Prototype;

public record ConnectionSettings
{
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
        return JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(path)) ?? new();
    }
    public void Save(string directory)
    {
        if (UseProxy) ValidateProxy();
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
