using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sf6Prototype;

public sealed class LoginRequiredException(string message) : Exception(message);
public interface IBattleSource
{
    int Requests { get; }
    Task<BattlePage> FetchPageAsync(string owner, string category, int page, CancellationToken token);
}
public sealed class BucklerClient : IBattleSource, IProfileSource, IRankSource, ICharacterNameSource, IDisposable
{
    public const string Origin = "https://www.streetfighter.com";
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;
    private readonly CookieVault _vault;
    private SavedSession _session;
    public int Requests { get; private set; }
    public event Action<RequestActivity>? Activity;
    public Action<CharacterObservation>? CharacterObserver {get;set;}
    public string AccountName => _session.AccountName;
    public string AccountId => _session.AccountId;
    public BucklerClient(ConnectionSettings settings, SavedSession session, CookieVault vault)
    {
        _session = session; _vault = vault; _cookies = CookieVault.ToContainer(session);
        _http = new HttpClient(settings.CreateHandler(_cookies)) { Timeout = TimeSpan.FromSeconds(25), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(session.UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Referrer = new Uri($"{Origin}/6/buckler/zh-hans/profile/{settings.PlayerId}/battlelog");
    }
    public async Task ValidateAsync(CancellationToken token)
    {
        var (code, body) = await GetAsync("/6/buckler/api/auth/getlogindata", token);
        EnsureSuccess(code);
        using var d = ParseJson(body);
        if (!d.RootElement.TryGetProperty("loginUser", out var user) || user.ValueKind != JsonValueKind.Object)
            throw new LoginRequiredException(Strings.CapcomSessionExpired);
        var id = MatchParser.Id(user, "shortId");
        if (id.Length == 0 || id == "0") throw new LoginRequiredException(Strings.CapcomSessionExpired);
        _session = _session with { AccountId = id, AccountName = MatchParser.Text(user, "fighterId") };
        Persist();
    }
    public async Task<BattlePage> FetchPageAsync(string owner, string category, int page, CancellationToken token)
    {
        if (!BattleCategory.All.Any(c => c.Key == category)) throw new ArgumentException(Strings.UnknownCategory);
        if (string.IsNullOrEmpty(_session.BuildId)) await RefreshBuildAsync(owner, token);
        string Url() => $"/6/buckler/_next/data/{Uri.EscapeDataString(_session.BuildId)}/zh-hans/profile/{owner}/battlelog/{category}.json?page={page}&sid={owner}";
        var (code, body) = await GetAsync(Url(), token);
        if (code == 404)
        {
            await RefreshBuildAsync(owner, token);
            (code, body) = await GetAsync(Url(), token);
        }
        EnsureSuccess(code);
        if (!body.TrimStart().StartsWith('{')) throw new LoginRequiredException(Strings.VerificationRequired);
        try
        {
            var parsed=MatchParser.Parse(body,owner,category);
            CharacterObserver?.Invoke(CharacterObservation.Battles(parsed.Battles));
            return parsed;
        }
        catch (JsonException) { throw new InvalidDataException(Strings.InvalidReplayJson); }
        catch (KeyNotFoundException) { throw new InvalidDataException(Strings.ReplayStructureChanged); }
    }
    public async Task<string> FetchPlayAsync(string owner, CancellationToken token)
    {
        if (!Regex.IsMatch(owner,@"^\d{10}$")) throw new ArgumentException(Strings.InvalidUserCode);
        if (string.IsNullOrEmpty(_session.BuildId)) await RefreshBuildAsync(owner, token);
        string Url() => $"/6/buckler/_next/data/{Uri.EscapeDataString(_session.BuildId)}/zh-hans/profile/{owner}/play.json?sid={owner}";
        var (code, body) = await GetAsync(Url(), token);
        if (code == 404) { await RefreshBuildAsync(owner, token); (code, body) = await GetAsync(Url(), token); }
        EnsureSuccess(code);
        if(CharacterObserver!=null) CharacterObserver(CharacterObservation.Profile(body));
        return body;
    }
    public async Task<string> FetchSeasonRanksAsync(string owner, int season, bool peakMr, CancellationToken token)
    {
        if (!Regex.IsMatch(owner,@"^\d{10}$")) throw new ArgumentException(Strings.InvalidPlayer);
        var query = new RankQuery(season,peakMr); query.Validate();
        var endpoint = peakMr ? "highest/master_rating_info" : "leagueinfo";
        var payload = JsonSerializer.Serialize(new { targetShortId=long.Parse(owner),targetSeasonId=season,locale="zh-hans",peak=query.HighestLp });
        var (code,body) = await GetAsync($"/6/buckler/api/profile/play/act/{endpoint}",token,payload); EnsureSuccess(code);
        if(CharacterObserver!=null) CharacterObserver(CharacterObservation.Ranks(body));
        return body;
    }
    public async Task<string> FetchCharacterNamesAsync(string owner,string language,CancellationToken token)
    {
        // Use the actual localized page routes; English intentionally has no locale segment.
        var (code,html)=await GetAsync(CharacterNames.PagePath(owner,language),token);
        EnsureSuccess(code);
        var match=Regex.Match(html,"<script[^>]*id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>",RegexOptions.Singleline);
        if(!match.Success) throw new LoginRequiredException(Strings.PageNotVerified);
        return match.Groups[1].Value;
    }
    public async Task<string> FetchCardAsync(string owner, CancellationToken token)
    {
        var (code, body) = await GetAsync($"/6/buckler/api/zh-hans/card/{owner}", token); EnsureSuccess(code); return body;
    }
    private async Task RefreshBuildAsync(string owner, CancellationToken token)
    {
        var (code, html) = await GetAsync($"/6/buckler/zh-hans/profile/{owner}/battlelog", token); EnsureSuccess(code);
        var match = Regex.Match(html, "<script[^>]*id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!match.Success) throw new LoginRequiredException(Strings.PageNotVerified);
        using var data = JsonDocument.Parse(match.Groups[1].Value);
        _session = _session with { BuildId = data.RootElement.GetProperty("buildId").GetString()! }; Persist();
    }
    private async Task<(int Code, string Body)> GetAsync(string path, CancellationToken token, string? payload = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(payload==null ? HttpMethod.Get : HttpMethod.Post,Origin+path);
                request.Headers.CacheControl = new() { NoCache=true,NoStore=true };
                if(payload!=null) request.Content = new StringContent(payload,System.Text.Encoding.UTF8,"application/json");
                Activity?.Invoke(new(RequestPhase.Waiting));
                var sent=0L;
                using var response = await RequestGate.Shared.RunAsync(() =>
                {
                    Requests++;
                    sent=System.Diagnostics.Stopwatch.GetTimestamp();Activity?.Invoke(new(RequestPhase.Sending));
                    return _http.SendAsync(request, token);
                }, token);
                Persist();
                var code = (int)response.StatusCode;
                Activity?.Invoke(new(RequestPhase.Completed,System.Diagnostics.Stopwatch.GetElapsedTime(sent).TotalSeconds,code==200));
                if (code == 429)
                {
                    var after = response.Headers.RetryAfter?.Delta;
                    throw new HttpRequestException(after != null ? Localization.Format(Strings.RateLimitDelay, Math.Ceiling(after.Value.TotalSeconds)) : Strings.RateLimitPaused);
                }
                if (code >= 500 && attempt < 1) {Activity?.Invoke(new(RequestPhase.Retrying)); await Task.Delay(TimeSpan.FromSeconds(5), token); continue; }
                var body = await response.Content.ReadAsStringAsync(token);
                return (code, body);
            }
            catch (TaskCanceledException) when (!token.IsCancellationRequested && attempt < 1)
            {Activity?.Invoke(new(RequestPhase.Retrying)); await Task.Delay(TimeSpan.FromSeconds(5), token); }
        }
    }
    private static void EnsureSuccess(int code)
    {
        if (code is 401 or 403 || code is >= 300 and < 400) throw new LoginRequiredException(Localization.Format(Strings.HttpVerificationRequired, code));
        if (code != 200) throw new HttpRequestException(Localization.Format(Strings.HttpSyncStopped, code),null,(HttpStatusCode)code);
    }
    private static JsonDocument ParseJson(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new LoginRequiredException(Strings.LoginVerificationPage); }
    }
    private void Persist() { _session = _session with { Cookies = CookieVault.FromContainer(_cookies) }; _vault.Save(_session); }
    public void Dispose() { Persist(); _http.Dispose(); }
}
