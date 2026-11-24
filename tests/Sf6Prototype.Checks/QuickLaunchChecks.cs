using Sf6Prototype;

internal static class QuickLaunchChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool result, string description) { if (!result) throw new Exception(description); count++; }
        var root = Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"));
        var steam = Path.Combine(root, "Steam");
        var apps = Directory.CreateDirectory(Path.Combine(steam, "steamapps")).FullName;
        var library = Path.Combine(root, "游戏 Library");
        var externalApps = Path.Combine(library, "steamapps");
        var gameFolder = Directory.CreateDirectory(Path.Combine(externalApps, "common", "Street Fighter 6")).FullName;
        var exe = Path.Combine(gameFolder, "StreetFighter6.exe");
        File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), $"\"libraryfolders\"\n{{\n\"1\"\n{{\n\"path\" \"{library.Replace("\\","\\\\")}\"\n}}\n}}");
        File.WriteAllText(Path.Combine(externalApps, "appmanifest_1364780.acf"), "\"AppState\"\n{\n\"installdir\" \"Street Fighter 6\"\n}");
        Check(SteamGame.Find([steam]) == null, "A manifest alone does not identify an installed game");
        File.WriteAllText(exe, "");
        Check(SteamGame.Find([steam]) == exe, "Find SF6 in a secondary Steam library with spaces and Unicode");
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        var settings = ConnectionSettings.Load(root);
        Check(settings.QuickLaunch.Target == QuickLaunchTarget.Game && settings.QuickLaunch.StartInfo().FileName == "steam://rungameid/1364780",
            "Existing installations default to the SF6 Steam launch action");
        var manual = settings.QuickLaunch with { GamePath = exe };
        Check(manual.StartInfo().FileName == exe && manual.StartInfo().WorkingDirectory == gameFolder, "A chosen game executable takes precedence over Steam");
        var link = Path.Combine(root, "Accelerator.LNK"); File.WriteAllText(link, "");
        var accelerator = manual with { Target = QuickLaunchTarget.Accelerator, AcceleratorPath = link };
        var start = accelerator.StartInfo();
        Check(start.FileName == link && start.UseShellExecute && start.WorkingDirectory == "", "The shell opens the shortcut itself with its own arguments and working directory");
        Check((accelerator with { AcceleratorPath = exe }).StartInfo().FileName == exe, "An accelerator can be an executable too");
        Check(!QuickLaunchSettings.IsProgram(link, QuickLaunchTarget.Game) && !QuickLaunchSettings.IsProgram("relative.exe", QuickLaunchTarget.Accelerator),
            "A manual game uses an executable and both target types require a full path");
        (settings with { QuickLaunch = accelerator }).Save(root);
        Check(ConnectionSettings.Load(root).QuickLaunch == accelerator, "Switching targets preserves both file choices on disk");
        return count;
    }
}
