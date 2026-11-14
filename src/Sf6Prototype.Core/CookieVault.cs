using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sf6Prototype;

public record SavedCookie(string Name, string Value, string Domain, string Path, DateTime Expires, bool Secure, bool HttpOnly);
public record SavedSession(string AccountId, string AccountName, string UserAgent, string BuildId, string RouteKey, List<SavedCookie> Cookies)
{
    // Older encrypted sessions start at Guid.Empty until their first update.
    public Guid Revision { get; init; }
}

public sealed class CookieVault(string directory)
{
    private readonly string _path = Path.Combine(directory, "session.dpapi");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SF6.LocalSession.v1");
    private static readonly object FileGate = new();
    public SavedSession? Load()
    {
        lock (FileGate) return ReadActive();
    }

    // Explicit sign-in and activation select the current account and invalidate old clients.
    public SavedSession Save(SavedSession session)
    {
        lock (FileGate) return WriteActive(session);
    }

    internal SavedSession? TryUpdate(SavedSession original, SavedSession updated)
    {
        lock (FileGate)
        {
            if (updated.AccountId != original.AccountId || ReadActive() is not { } active ||
                active.AccountId != original.AccountId || active.Revision != original.Revision) return null;
            return WriteActive(updated);
        }
    }

    // Commit the account file first. If writing the selector fails, the old selection
    // still identifies its own account, and a stale snapshot cannot undo a committed update.
    private SavedSession WriteActive(SavedSession session)
    {
        var accountPath = AccountPath(session.AccountId);
        session = session with { Revision = Guid.NewGuid() };
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.Combine(directory, "Accounts"));
            File.WriteAllBytes(accountPath + ".tmp", encrypted); File.Move(accountPath + ".tmp", accountPath, true);
            File.WriteAllBytes(_path + ".tmp", encrypted); File.Move(_path + ".tmp", _path, true);
            return session;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private string AccountPath(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{10}$")) throw new ArgumentException(Strings.InvalidAccountId);
        return Path.Combine(directory, "Accounts", id + ".dpapi");
    }
    private SavedSession? ReadActive()
    {
        var selected = ReadSession(_path);
        return selected == null ? null : ReadSession(AccountPath(selected.AccountId)) ?? selected;
    }
    private static SavedSession? ReadSession(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SavedSession>(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { return null; }
    }
    public IReadOnlyList<SavedSession> Accounts()
    {
        lock (FileGate)
        {
            var folder = Path.Combine(directory, "Accounts");
            // Preserve the revision and cookies when migrating a single-account vault.
            if (ReadSession(_path) is { } current && !File.Exists(AccountPath(current.AccountId)))
            {
                Directory.CreateDirectory(folder);
                File.Copy(_path, AccountPath(current.AccountId));
            }
            return !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*.dpapi")
                .Select(ReadSession).OfType<SavedSession>().OrderBy(s => s.AccountName).ToList();
        }
    }
    public SavedSession Activate(string id)
    {
        lock (FileGate)
        {
            var session = ReadSession(AccountPath(id)) ?? throw new InvalidDataException(Strings.AccountSignedOut);
            return WriteActive(session);
        }
    }
    public void Remove(string id)
    {
        // Remove credentials only; the local match archive and cached portraits remain available.
        lock (FileGate)
        {
            var accountPath = AccountPath(id);
            if (ReadSession(_path)?.AccountId == id) File.Delete(_path);
            File.Delete(accountPath);
        }
    }
    public static bool IsSiteDomain(string domain) => domain.TrimStart('.').Equals("streetfighter.com", StringComparison.OrdinalIgnoreCase)
        || domain.EndsWith(".streetfighter.com", StringComparison.OrdinalIgnoreCase);
    public static CookieContainer ToContainer(SavedSession session)
    {
        var jar = new CookieContainer();
        foreach (var c in session.Cookies.Where(c => IsSiteDomain(c.Domain)))
        {
            if (c.Expires != DateTime.MinValue && c.Expires.ToUniversalTime() < DateTime.UtcNow) continue;
            jar.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly, Expires = c.Expires });
        }
        return jar;
    }
    public static List<SavedCookie> FromContainer(CookieContainer jar) => jar.GetAllCookies().Cast<Cookie>()
        .Where(c => IsSiteDomain(c.Domain) && !c.Expired)
        .Select(c => new SavedCookie(c.Name, c.Value, c.Domain, c.Path, c.Expires, c.Secure, c.HttpOnly)).ToList();
}
