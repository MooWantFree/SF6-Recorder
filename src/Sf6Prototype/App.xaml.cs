using System.Diagnostics;
using System.IO;
using System.Windows;
using Velopack;
using Velopack.Locators;

namespace Sf6Prototype;

public partial class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
    private FileStream? _instanceLock;
    public static string DataDirectory { get; private set; } = "";
    public static bool AutoSync { get; private set; }
    public static bool Capture { get; private set; }
    public static bool IsShuttingDown { get; private set; }
    public static Size? StartSize { get; private set; }
    public static DateTimeOffset StartedAt {get;private set;}=DateTimeOffset.UtcNow;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using(var process=Process.GetCurrentProcess()) StartedAt=process.StartTime.ToUniversalTime();
        string? Arg(string name)
        {
            var i = Array.IndexOf(e.Args, name);
            return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        }
        foreach (var pid in (Arg("--wait-for") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(pid, out var id)) continue;
            try { using var p = Process.GetProcessById(id); if (!p.WaitForExit(20_000)) throw new IOException(Strings.PreviousProcessRunning); }
            catch (ArgumentException) { }
        }
        DataDirectory = DataPaths.Resolve(AppContext.BaseDirectory, Arg("--data-dir"), VelopackLocator.Current.RootAppDir);
        AutoSync = e.Args.Contains("--sync-on-start");
        Capture = e.Args.Contains("--capture");
        if(Capture && DateTimeOffset.TryParse(Arg("--capture-session-start"),System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,out var captureStart)) StartedAt=captureStart;
        if (Arg("--size")?.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height)) StartSize = new(width, height);
        Directory.CreateDirectory(DataDirectory);
        LocalizationWpf.Apply(ConnectionSettings.Load(DataDirectory).Language);
        try { _instanceLock = new FileStream(Path.Combine(DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            MessageWindow.Notice(null,Strings.DataDirectoryInUse,Strings.AppTitle);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(Path.Combine(DataDirectory, "fatal-error.txt"), args.Exception.GetType().Name + " / " + args.Exception.HResult);
            // Keep a native fallback if the UI resources themselves caused the failure.
            try {MessageWindow.Notice(MainWindow,Strings.UnexpectedError,Strings.AppTitle);}
            catch {MessageBox.Show(Strings.UnexpectedError,Strings.AppTitle);}
            args.Handled = true;
            IsShuttingDown=true;
            Shutdown(1);
        };
        SessionEnding+=(_,_)=>IsShuttingDown=true;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
