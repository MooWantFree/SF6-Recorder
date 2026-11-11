using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Sf6Prototype;

public partial class App : Application
{
    private FileStream? _instanceLock;
    public static string DataDirectory { get; private set; } = "";
    public static bool AutoSync { get; private set; }
    public static bool Capture { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Arg(string name)
        {
            var i = Array.IndexOf(e.Args, name);
            return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null;
        }
        foreach (var pid in (Arg("--wait-for") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(pid, out var id)) continue;
            try { using var p = Process.GetProcessById(id); if (!p.WaitForExit(20_000)) throw new IOException("上一次浏览器进程尚未退出，请稍后重试。"); }
            catch (ArgumentException) { }
        }
        DataDirectory = Path.GetFullPath(Arg("--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "Data"));
        AutoSync = e.Args.Contains("--sync-on-start");
        Capture = e.Args.Contains("--capture");
        Directory.CreateDirectory(DataDirectory);
        try { _instanceLock = new FileStream(Path.Combine(DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            MessageBox.Show("这个数据目录已有运行中的战绩窗口。", "街霸 6 战绩");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            File.WriteAllText(Path.Combine(DataDirectory, "fatal-error.txt"), args.Exception.GetType().Name + " / " + args.Exception.HResult);
            MessageBox.Show("应用遇到错误，请查看数据目录中的诊断记录。", "街霸 6 战绩");
            args.Handled = true;
            Shutdown(1);
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
