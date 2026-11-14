using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using Sf6Prototype;

internal static class StatusFileChecks
{
    public static int Run(MainWindow main)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
        var root = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(root, "status.json");
        var dataDirectory = typeof(App).GetProperty(nameof(App.DataDirectory))!;
        var previousDirectory = App.DataDirectory;
        var previousLog = Diagnostics.Log;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var setStatus = typeof(MainWindow).GetMethod("SetStatus", flags)!;
        var reportError = typeof(MainWindow).GetMethod("ReportError", flags)!;
        var toast = (StatusToast)main.FindName("Toast");
        var toastText = (TextBlock)toast.FindName("MessageText");
        try
        {
            dataDirectory.SetValue(null, root);
            Diagnostics.Log = new DiagnosticLog(root);
            foreach (string locked in new[] { path + ".tmp", path })
            {
                setStatus.Invoke(main, ["ready", "Before lock"]);
                var previous = File.ReadAllText(path);
                using (var held = new FileStream(locked, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    setStatus.Invoke(main, ["ready", "While locked"]);
                    reportError.Invoke(main, [new InvalidDataException("Fixture error")]);
                    Check(toast.Visibility == System.Windows.Visibility.Visible,
                        "A failed status write preserves the error notification");
                    Check(toastText.Text == "Fixture error", "The original error reaches the UI while status output is locked");
                }
                Check(File.ReadAllText(path) == previous, "A failed status update preserves the last complete snapshot");
                setStatus.Invoke(main, ["ready", "After lock"]);
                using var recovered = JsonDocument.Parse(File.ReadAllText(path));
                Check(recovered.RootElement.GetProperty("status").GetString() == "After lock", "Status output resumes when the lock is released");
            }
            Check(File.ReadLines(Diagnostics.Log.FilePath).Any(line => line.Contains("status_write_failed")),
                "An unavailable status file is recorded in the diagnostic log");
        }
        finally
        {
            toast.Dismiss();
            Diagnostics.Log = previousLog;
            dataDirectory.SetValue(null, previousDirectory);
        }
        return count;
    }
}
