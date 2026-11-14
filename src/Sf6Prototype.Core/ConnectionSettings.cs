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
    public const int DefaultRefreshReminderSeconds = 0;
    public const double DefaultApiIntervalSeconds = 2;
    public const int DefaultMatchEndWaitSeconds = 1;
    public double ApiIntervalSeconds { get; init; } = DefaultApiIntervalSeconds;
    public static bool ValidApiInterval(double seconds) => double.IsFinite(seconds) && seconds is >=0.1 and <=3600;
    public static bool LowApiInterval(double seconds) => ValidApiInterval(seconds) && seconds<DefaultApiIntervalSeconds;
    public int RefreshReminderSeconds { get; init; } = DefaultRefreshReminderSeconds;
    public int AutoRefreshSeconds { get; init; } = 0;
    public bool RefreshAfterMatch { get; init; } = true;
    public bool FocusLatestMatchAfterRefresh { get; init; } = true;
    public bool RecentMatchesCollapsed {get;init;} = false;
    public int MatchEndWaitSeconds { get; init; } = DefaultMatchEndWaitSeconds;
    public int MatchEndRetrySeconds { get; init; } = 8;
    public double MatchEndSamplesPerSecond { get; init; } = 4;
    public static bool ValidMatchEndFrequency(double value)=>double.IsFinite(value) && value is >=0.5 and <=10;
    public CloseAction WindowCloseAction { get; init; } = CloseAction.Ask;
    public string Language { get; init; } = Localization.SystemLanguage;
    public bool UseProxy { get; init; } = false;
    public string ProxyAddress { get; init; } = "http://127.0.0.1:7890";
    public string PlayerId { get; init; } = "3396336472";
    public string RouteKey => UseProxy ? ValidateProxy().AbsoluteUri : "direct";
    public Uri ValidateProxy()
    {
        if (!Uri.TryCreate(ProxyAddress.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host) || uri.Port <= 0 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(Strings.InvalidProxy);
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
        if (response.StatusCode != HttpStatusCode.NoContent) throw new HttpRequestException(Localization.Format(Strings.GoogleHttpError, (int)response.StatusCode));
        return watch.ElapsedMilliseconds;
    }
    private static readonly object FileGate = new();
    public static ConnectionSettings Load(string directory)
    {
        lock (FileGate) return LoadCore(directory);
    }
    private static ConnectionSettings LoadCore(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return new();
        ConnectionSettings settings;
        try
        {
            settings = ReadSettings(path);
            // Seed a recovery copy for existing installations without rewriting preferences.
            if(!File.Exists(path + ".bak"))
            {
                try { AtomicFile.WriteText(path + ".bak", File.ReadAllText(path)); }
                catch(Exception backupError) when(backupError is IOException or UnauthorizedAccessException)
                { Diagnostics.Write("settings", "backup_failed", backupError); }
            }
        }
        catch (JsonException error)
        {
            Diagnostics.Write("settings", "corrupt", error);
            settings = new() {RefreshAfterMatch=false}; // Damaged preferences must not opt into background requests.
            try { if (File.Exists(path + ".bak")) settings = ReadSettings(path + ".bak"); }
            catch (Exception backupError) when (backupError is JsonException or IOException or UnauthorizedAccessException)
            { Diagnostics.Write("settings", "backup_unavailable", backupError); }
            try
            {
                // Preserve evidence before repairing the primary file. Recovery is local and silent.
                File.Copy(path, Path.Combine(directory, "settings.corrupt-" + Guid.NewGuid().ToString("N") + ".json"));
                AtomicFile.WriteText(path, JsonSerializer.Serialize(settings));
                Diagnostics.Write("settings", "recovered");
            }
            catch (Exception repairError) when (repairError is IOException or UnauthorizedAccessException)
            { Diagnostics.Write("settings", "repair_failed", repairError); }
        }
        return settings with
        {
            RefreshReminderSeconds=settings.RefreshReminderSeconds>=0 ? settings.RefreshReminderSeconds : DefaultRefreshReminderSeconds,
            AutoRefreshSeconds=Math.Max(0,settings.AutoRefreshSeconds),
            MatchEndWaitSeconds=settings.MatchEndWaitSeconds is >=0 and <=3600 ? settings.MatchEndWaitSeconds : DefaultMatchEndWaitSeconds,
            MatchEndRetrySeconds=settings.MatchEndRetrySeconds is >=1 and <=3600 ? settings.MatchEndRetrySeconds : 8,
            MatchEndSamplesPerSecond=ValidMatchEndFrequency(settings.MatchEndSamplesPerSecond) ? settings.MatchEndSamplesPerSecond : 4,
            ApiIntervalSeconds=ValidApiInterval(settings.ApiIntervalSeconds) ? settings.ApiIntervalSeconds : DefaultApiIntervalSeconds,
            WindowCloseAction=Enum.IsDefined(settings.WindowCloseAction) ? settings.WindowCloseAction : CloseAction.Ask,
            Language=Localization.Normalize(settings.Language)
        };
    }
    private static ConnectionSettings ReadSettings(string path) =>
        JsonSerializer.Deserialize<ConnectionSettings>(File.ReadAllText(path)) ?? throw new JsonException("Empty settings document");
    public static int ParseReminderSeconds(string value)
    {
        return ParseIntervalSeconds(value,Strings.RefreshReminder);
    }
    public static int ParseAutoRefreshSeconds(string value) => ParseIntervalSeconds(value,Strings.AutoRefresh);
    public static int ParseMatchEndSeconds(string value,bool retry=false)
    {
        var seconds=ParseIntervalSeconds(value,retry ? Strings.MatchEndRetry : Strings.MatchEndWait);
        if(seconds>3600 || retry && seconds<1) throw new ArgumentException(Strings.InvalidMatchEndInterval);
        return seconds;
    }
    public static double ParseMatchEndFrequency(string value)
    {
        if(!double.TryParse(value.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var frequency) || !ValidMatchEndFrequency(frequency))
            throw new ArgumentException(Strings.InvalidMatchEndFrequency);
        return frequency;
    }
    public static double ParseApiIntervalSeconds(string value)
    {
        if(!double.TryParse(value.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var seconds) || !ValidApiInterval(seconds))
            throw new ArgumentException(Strings.InvalidApiInterval);
        return seconds;
    }
    private static int ParseIntervalSeconds(string value,string name)
    {
        if(!int.TryParse(value.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var seconds))
            throw new ArgumentException(Localization.Format(Strings.InvalidInterval, name));
        return seconds;
    }
    public void Save(string directory)
    {
        if(RefreshReminderSeconds<0) throw new ArgumentException(Strings.InvalidReminder);
        if(AutoRefreshSeconds<0) throw new ArgumentException(Strings.InvalidAutoRefresh);
        if(MatchEndWaitSeconds is <0 or >3600 || MatchEndRetrySeconds is <1 or >3600) throw new ArgumentException(Strings.InvalidMatchEndInterval);
        if(!ValidMatchEndFrequency(MatchEndSamplesPerSecond)) throw new ArgumentException(Strings.InvalidMatchEndFrequency);
        if(!ValidApiInterval(ApiIntervalSeconds)) throw new ArgumentException(Strings.InvalidApiInterval);
        if(!Enum.IsDefined(WindowCloseAction)) throw new ArgumentException(Strings.InvalidCloseAction);
        if(!Localization.IsSupported(Language)) throw new ArgumentException(Strings.InvalidLanguage);
        if (UseProxy) ValidateProxy();
        lock (FileGate)
        {
            var path = Path.Combine(directory, "settings.json");
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteText(path, json);
            try { AtomicFile.WriteText(path + ".bak", json); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Diagnostics.Write("settings", "backup_failed", error); }
        }
    }
}
