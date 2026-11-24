using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Sf6Prototype;

public enum QuickLaunchTarget { Game, Accelerator, Off }

public sealed record QuickLaunchSettings
{
    public QuickLaunchTarget Target { get; init; } = QuickLaunchTarget.Game;
    // An empty game path follows Steam even if the game moves to another library.
    public string GamePath { get; init; } = "";
    public string AcceleratorPath { get; init; } = "";

    public ProcessStartInfo StartInfo()
    {
        if (Target == QuickLaunchTarget.Game && string.IsNullOrEmpty(GamePath))
            return new("steam://rungameid/" + SteamGame.AppId) { UseShellExecute = true };
        var path = Target == QuickLaunchTarget.Accelerator ? AcceleratorPath : GamePath;
        if (Target == QuickLaunchTarget.Off || !IsProgram(path, Target) || !File.Exists(path))
            throw new FileNotFoundException(Strings.QuickLaunchFileMissing);
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            info.WorkingDirectory = Path.GetDirectoryName(path)!;
        return info;
    }

    public static bool IsProgram(string? path, QuickLaunchTarget target) =>
        target != QuickLaunchTarget.Off && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
         target == QuickLaunchTarget.Accelerator && path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));
}

public static class SteamGame
{
    internal const string AppId = "1364780";

    public static string? Find()
    {
        var roots = new List<string?>();
        foreach (var (key, name) in new[] {
            (@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath") })
        {
            try { roots.Add(Registry.GetValue(key, name, null) as string); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Diagnostics.Write("quick_launch", "steam_registry", ex); }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return Find(roots);
    }

    internal static string? Find(IEnumerable<string?> roots)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !libraries.Add(root)) continue;
            var config = Read(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            foreach (Match match in Regex.Matches(config, @"(?im)^\s*""(?:path|\d+)""\s+""([^""\r\n]+)"""))
            {
                var path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Path.IsPathFullyQualified(path)) libraries.Add(path);
            }
        }
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            var manifest = Read(Path.Combine(apps, $"appmanifest_{AppId}.acf"));
            var directory = Regex.Match(manifest, @"(?im)^\s*""installdir""\s+""([^""\r\n]+)""").Groups[1].Value;
            if (directory.Length == 0 || directory is "." or ".." || directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            var exe = Path.Combine(apps, "common", directory, "StreetFighter6.exe");
            if (File.Exists(exe)) return Path.GetFullPath(exe);
        }
        return null;
    }

    private static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Diagnostics.Write("quick_launch", "steam_library", ex); return ""; }
    }
}
