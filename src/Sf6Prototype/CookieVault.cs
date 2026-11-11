using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sf6Prototype;

public record SavedCookie(string Name, string Value, string Domain, string Path, DateTime Expires, bool Secure, bool HttpOnly);
public record SavedSession(string AccountId, string AccountName, string UserAgent, string BuildId, string RouteKey, List<SavedCookie> Cookies);

public sealed class CookieVault(string directory)
{
    private readonly string _path = Path.Combine(directory, "session.dpapi");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SF6.LocalSession.v1");
    public SavedSession? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<SavedSession>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { return null; }
    }
    public void Save(SavedSession session)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(session.AccountId, @"^\d{10}$")) throw new ArgumentException("无效的账号 ID。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.Combine(directory, "Accounts"));
            var accountPath = AccountPath(session.AccountId);
            File.WriteAllBytes(accountPath + ".tmp", encrypted); File.Move(accountPath + ".tmp", accountPath, true);
            File.WriteAllBytes(_path + ".tmp", encrypted); File.Move(_path + ".tmp", _path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private string AccountPath(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{10}$")) throw new ArgumentException("无效的账号 ID。");
        return Path.Combine(directory, "Accounts", id + ".dpapi");
    }
    private SavedSession? ReadAccount(string path)
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
        // Migrate the original single-account vault without losing its session.
        var current = Load();
        if (current != null && !File.Exists(AccountPath(current.AccountId))) Save(current);
        var folder = Path.Combine(directory, "Accounts");
        return !Directory.Exists(folder) ? [] : Directory.EnumerateFiles(folder, "*.dpapi")
            .Select(ReadAccount).OfType<SavedSession>().OrderBy(s => s.AccountName).ToList();
    }
    public SavedSession Activate(string id)
    {
        var session = ReadAccount(AccountPath(id)) ?? throw new InvalidDataException("此账号已登出，请重新登录。");
        Save(session); return session;
    }
    public void Remove(string id)
    {
        // Remove credentials only; the local match archive and cached portraits remain available.
        if (Load()?.AccountId == id) File.Delete(_path);
        File.Delete(AccountPath(id));
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
