using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sf6Prototype;

internal static class BucklerClientChecks
{
    private sealed class ReplyHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request);
    }

    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool passed, string message) { if (!passed) throw new Exception(message); count++; }
        var root = Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"));
        var vault = new CookieVault(root);
        var settings = new ConnectionSettings { PlayerId = "1234567890" };
        var gate = new RequestGate(TimeSpan.Zero);
        var a = new SavedSession(settings.PlayerId, "A", "Sf6Checks/1.0", "build", "direct", []);
        var b = a with { AccountId = "2234567890", AccountName = "B" };
        BucklerClient Client(SavedSession snapshot, Func<CookieContainer, Task<HttpResponseMessage>> send) =>
            new(settings, snapshot, vault, jar => new ReplyHandler(_ => send(jar)), gate);
        static HttpResponseMessage Ok(string body = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        static void SetCookie(CookieContainer jar, string value) =>
            jar.Add(new Cookie("fixture", value, "/", "www.streetfighter.com"));

        vault.Save(b);
        var selected = vault.Save(a);
        var idle = new BucklerClient(settings, selected, vault);
        vault.Activate(b.AccountId);
        idle.Dispose();
        Check(vault.Load()?.AccountId == b.AccountId, "Closing an idle rank client preserves the newly selected account");

        foreach (var change in new[] { "switch", "switch-back", "remove", "sign-in-again" })
        {
            selected = vault.Save(a);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var late = Client(selected, async jar =>
            {
                entered.SetResult();
                await release.Task;
                SetCookie(jar, "obsolete");
                return Ok();
            });
            var request = late.FetchCardAsync(a.AccountId, CancellationToken.None);
            await entered.Task;
            if (change is "switch" or "switch-back") vault.Activate(b.AccountId);
            else vault.Remove(a.AccountId);
            if (change == "switch-back") vault.Activate(a.AccountId);
            if (change == "sign-in-again") vault.Save(a);
            var before = vault.Load();
            release.SetResult();
            await request;
            late.Dispose();
            var after = vault.Load();
            Check(before?.AccountId == after?.AccountId && before?.Revision == after?.Revision,
                $"A late HTTP response and disposal preserve the selected session after {change}");
            var stored = vault.Accounts().SingleOrDefault(s => s.AccountId == a.AccountId);
            Check(change == "remove" ? stored == null : stored?.Cookies.Count == 0,
                $"A late client cannot restore or overwrite saved account cookies after {change}");
        }

        selected = vault.Save(a);
        using var writer = Client(selected, jar => { SetCookie(jar, "fresh"); return Task.FromResult(Ok()); });
        using var older = Client(selected, jar => { SetCookie(jar, "old"); return Task.FromResult(Ok()); });
        await writer.FetchCardAsync(a.AccountId, CancellationToken.None);
        var first = vault.Load()!;
        await older.FetchCardAsync(a.AccountId, CancellationToken.None);
        Check(first.Cookies.Single().Value == "fresh" && vault.Load()!.Revision == first.Revision,
            "An older same-account client cannot replace a newer cookie revision");
        await writer.FetchCardAsync(a.AccountId, CancellationToken.None);
        Check(vault.Load()!.Revision == first.Revision, "A response with unchanged cookies keeps the session revision");

        using var mismatched = Client(first, jar =>
        {
            SetCookie(jar, "different-account");
            return Task.FromResult(Ok(JsonSerializer.Serialize(new { loginUser = new { shortId = b.AccountId, fighterId = "B" } })));
        });
        try { await mismatched.ValidateAsync(CancellationToken.None); throw new Exception("Expected account mismatch"); }
        catch (LoginRequiredException) { count++; }
        Check(vault.Load()!.Revision == first.Revision && vault.Accounts().Single(s => s.AccountId == b.AccountId).Cookies.Count == 0,
            "Mismatched validation persists neither response cookies nor a different identity");

        using (var locked = new FileStream(Path.Combine(root, "session.dpapi.tmp"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            try { vault.Save(first with { AccountName = "Latest" }); throw new Exception("Expected a locked selector"); }
            catch (IOException) { count++; }
        }
        var recovered = new CookieVault(root).Load()!;
        Check(recovered.AccountName == "Latest" && recovered.Revision == vault.Accounts().Single(s => s.AccountId == a.AccountId).Revision &&
            vault.TryUpdate(first, first with { AccountName = "Stale" }) == null,
            "A partial account write recovers the committed revision and rejects older clients");

        var legacyDirectory = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
        var legacyPath = Path.Combine(legacyDirectory, "session.dpapi");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { a.AccountId, a.AccountName, a.UserAgent, a.BuildId, a.RouteKey, a.Cookies });
        File.WriteAllBytes(legacyPath, ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("SF6.LocalSession.v1"), DataProtectionScope.CurrentUser));
        var legacy = new CookieVault(legacyDirectory);
        var snapshot = legacy.Load()!;
        Check(snapshot.Revision == Guid.Empty && legacy.Accounts().Single().AccountId == a.AccountId,
            "Existing single-account files without revisions migrate without another login");
        using var migrated = new BucklerClient(settings, snapshot, legacy, jar => new ReplyHandler(_ =>
        {
            SetCookie(jar, "rotated");
            return Task.FromResult(Ok(JsonSerializer.Serialize(new { loginUser = new { shortId = a.AccountId, fighterId = "Renamed" } })));
        }), gate);
        await migrated.ValidateAsync(CancellationToken.None);
        var saved = new CookieVault(legacyDirectory).Load()!;
        Check(saved.AccountName == "Renamed" && saved.Cookies.Single().Value == "rotated" && saved.Revision != Guid.Empty &&
            legacy.Accounts().Single().Revision == saved.Revision, "Validated legacy sessions save the updated name and cookies across restarts");
        await CheckRateLimitsAsync(vault, first, Check);
        return count;
    }

    private static async Task CheckRateLimitsAsync(CookieVault vault, SavedSession session, Action<bool, string> check)
    {
        var settings = new ConnectionSettings { PlayerId = session.AccountId };
        foreach (var header in new[] { "delta", "date", "missing", "zero" })
        {
            var gate = new RequestGate(TimeSpan.FromMilliseconds(10));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedCooldown = DateTimeOffset.MinValue;
            using var limited = new BucklerClient(settings, session, vault, _ => new ReplyHandler(async request =>
            {
                entered.SetResult();
                await release.Task;
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                startedCooldown = DateTimeOffset.UtcNow;
                response.Headers.RetryAfter = header switch
                {
                    "delta" => new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)),
                    "date" => new RetryConditionHeaderValue(startedCooldown.AddMinutes(2)),
                    "zero" => new RetryConditionHeaderValue(TimeSpan.Zero),
                    _ => null
                };
                return response;
            }), gate);
            var queuedSentAt = DateTimeOffset.MinValue;
            using var other = new BucklerClient(settings, session, vault, _ => new ReplyHandler(request =>
            {
                queuedSentAt = DateTimeOffset.UtcNow;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }), gate);
            var first = limited.FetchCardAsync(session.AccountId, CancellationToken.None);
            await entered.Task;
            using var cancel = new CancellationTokenSource();
            var queued = other.FetchCardAsync(session.AccountId, cancel.Token);
            release.SetResult();
            try { await first; throw new Exception("Expected HTTP 429"); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests) { }
            gate.Interval = TimeSpan.Zero;
            if (header == "delta")
            {
                await queued.WaitAsync(TimeSpan.FromSeconds(10));
                check(queuedSentAt - startedCooldown >= TimeSpan.FromSeconds(1) && other.Requests == 1,
                    "An already queued client sends only after Retry-After expires");
            }
            else
            {
                var deadline = DateTimeOffset.UtcNow + gate.RemainingDelay;
                var expected = startedCooldown.AddMinutes(header == "date" ? 2 : 1);
                check((deadline - expected).Duration() < TimeSpan.FromSeconds(2) && !queued.IsCompleted,
                    $"The {header} Retry-After deadline survives changing the regular interval");
                cancel.Cancel();
                try { await queued; throw new Exception("Expected cancellation during cooldown"); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                check(other.Requests == 0, "Cancelling a cooldown wait never sends the queued request");
            }
            check(limited.Requests == 1, "A rate-limited HTTP response is never retried by its sender");
        }
    }
}
