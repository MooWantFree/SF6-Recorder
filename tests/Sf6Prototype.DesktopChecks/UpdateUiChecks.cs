using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using Sf6Prototype;

internal static class UpdateUiChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var available = typeof(SettingsWindow).GetField("_availableUpdate", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = available.GetValue(null);
        var method = typeof(SettingsWindow).GetMethod("CheckForUpdatesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var refreshButton = typeof(SettingsWindow).GetMethod("UpdateReleaseButton", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var window = new SettingsWindow(null!, new());
        var button = (Button)window.FindName("CheckUpdatesButton");
        var toast = (StatusToast)window.FindName("UpdateToast");
        var message = (TextBlock)toast.FindName("MessageText");
        Task Start(Func<Task<UpdateCheckResult>> request) => (Task)method.Invoke(window, [request])!;
        try
        {
            available.SetValue(null, null); refreshButton.Invoke(window, null);
            Check(((TextBlock)window.FindName("VersionText")).Text == GitHubUpdates.VersionLabel &&
                GitHubUpdates.CurrentVersion.Build >= 0, "Settings show the version embedded in the application assembly");
            var response = new TaskCompletionSource<UpdateCheckResult>();
            var pending = Start(() => response.Task);
            Check(!button.IsEnabled && !pending.IsCompleted && button.Content?.ToString() == Strings.CheckingUpdates,
                "An in-flight check disables repeat clicks and shows its busy state");
            var release = new UpdateRelease(new Version(99, 0, 0), new Uri(GitHubUpdates.RepositoryUrl + "/releases/tag/v99.0.0"),
                "v99.0.0",new Velopack.UpdateInfo(new Velopack.VelopackAsset(),false));
            response.SetResult(new(UpdateStatus.Available, release));
            window.Dispatcher.Invoke(() => {}, DispatcherPriority.ContextIdle);
            pending.GetAwaiter().GetResult();
            Check(button.IsEnabled && button.Content?.ToString() == Strings.CheckUpdates && button.Style == window.FindResource("AccentButton"),
                "An update highlights the check button while preserving manual rechecks");
            Check(toast.Visibility == Visibility.Visible && message.Text.Contains("v99.0.0") &&
                ((Button)toast.FindName("ActionButton")).Visibility == Visibility.Visible,
                "The update toast includes both the new version and a release-page action");
            toast.Dismiss();
            Check(((Button)window.FindName("InstallUpdateButton")).Visibility==Visibility.Visible, "The install action remains available after the toast disappears");
            var reopened = new SettingsWindow(null!, new());
            Check(((Button)reopened.FindName("CheckUpdatesButton")).Content?.ToString() == Strings.CheckUpdates &&
                ((Button)reopened.FindName("InstallUpdateButton")).Visibility==Visibility.Visible,
                "Reopening settings preserves the update and still permits checking again");
            reopened.Close();
            var captureDirectory=Path.Combine(Environment.CurrentDirectory,".local","checks","update-captures");
            Directory.CreateDirectory(captureDirectory);
            foreach(var language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                LocalizationWpf.Apply(language);
                var surface=(FrameworkElement)window.Content;
                surface.Measure(new Size(window.Width-16,double.PositiveInfinity));surface.Arrange(new Rect(surface.DesiredSize));surface.UpdateLayout();
                var github=(Button)window.FindName("GitHubButton");
                var actions=(FrameworkElement)((Button)window.FindName("SaveButton")).Parent;
                Check(github.TranslatePoint(new Point(github.ActualWidth,0),surface).X+8 <= actions.TranslatePoint(new Point(),surface).X,
                    $"Available-update controls fit beside Save/Cancel in {language}: right={github.TranslatePoint(new Point(github.ActualWidth,0),surface).X}, actions={actions.TranslatePoint(new Point(),surface).X}");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);
                bitmap.Render(surface);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file=File.Create(Path.Combine(captureDirectory,language+"-available.png"));png.Save(file);
            }
            LocalizationWpf.Apply("zh-Hans");
            Start(() => Task.FromResult(new UpdateCheckResult(UpdateStatus.UpToDate))).GetAwaiter().GetResult();
            Check(message.Text == Strings.NoUpdates && button.IsEnabled && button.Content?.ToString() == Strings.CheckUpdates,
                "No-update checks show a toast and allow another manual check");
            Check(available.GetValue(null)==null && ((Button)window.FindName("InstallUpdateButton")).Visibility==Visibility.Collapsed,
                "A recheck clears a retracted update instead of offering a stale package");
            Start(() => Task.FromException<UpdateCheckResult>(new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests))).GetAwaiter().GetResult();
            Check(message.Text == Strings.UpdateCheckRateLimited && button.IsEnabled,
                "A GitHub limit shows an error and restores the manual check button");
            Start(() => Task.FromException<UpdateCheckResult>(new TaskCanceledException("timeout"))).GetAwaiter().GetResult();
            Check(message.Text == Strings.UpdateCheckFailed && button.IsEnabled, "A timeout is not confused with a successful no-update check");
            toast.Dismiss();
            var closingResponse = new TaskCompletionSource<UpdateCheckResult>();
            var closingTask = Start(() => closingResponse.Task);
            window.Close();
            closingResponse.SetResult(new(UpdateStatus.Available, release));
            window.Dispatcher.Invoke(() => {}, DispatcherPriority.ContextIdle);
            closingTask.GetAwaiter().GetResult();
            Check(available.GetValue(null) == null && toast.Visibility == Visibility.Collapsed,
                "A late response after closing settings does not display a toast or replace update state");
            var downloading=new SettingsWindow(null!,new());
            var download=new TaskCompletionSource();
            typeof(SettingsWindow).GetField("_updateDownload",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(downloading,download.Task);
            downloading.Close();
            var closing=(CancellationTokenSource)typeof(SettingsWindow).GetField("_closing",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(downloading)!;
            Check(closing.IsCancellationRequested && !downloading.PendingTransfer.IsCompleted,
                "Closing settings cancels and waits for an in-flight package download");
            download.SetCanceled();downloading.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
            Check(downloading.PendingTransfer.IsCompleted,"Cancelled update downloads drain without blocking application exit");
        }
        finally { available.SetValue(null, original); toast.Dismiss(); window.Close(); LocalizationWpf.Apply("zh-Hans"); }
        return count;
    }
}
