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
        var available = typeof(SettingsWindow).GetProperty("AvailableUpdate", BindingFlags.NonPublic | BindingFlags.Static)!;
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
            var startupSwitch = (CheckBox)window.FindName("StartupUpdateEnabled");
            Check(startupSwitch.IsChecked == true, "The startup update switch defaults to on");
            startupSwitch.IsChecked = false;
            var draft = (ConnectionSettings)typeof(SettingsWindow).GetMethod("Draft", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true])!;
            var optedOut = new SettingsWindow(null!, draft);
            Check(!draft.CheckForUpdatesOnStartup && ((CheckBox)optedOut.FindName("StartupUpdateEnabled")).IsChecked == false &&
                ((Button)optedOut.FindName("CheckUpdatesButton")).IsEnabled, "Saving the switch disables automatic checks and leaves manual checks available");
            optedOut.Close();
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
                ((FrameworkElement)toast.FindName("UpdateActions")).Visibility == Visibility.Visible,
                "The manual update toast includes the version and update choices");
            toast.Dismiss();
            Check(((Button)window.FindName("InstallUpdateButton")).Visibility==Visibility.Visible, "The install action remains available after the toast disappears");
            var reopened = new SettingsWindow(null!, new());
            Check(((Button)reopened.FindName("CheckUpdatesButton")).Content?.ToString() == Strings.CheckUpdates &&
                ((Button)reopened.FindName("InstallUpdateButton")).Visibility==Visibility.Visible,
                "Reopening settings preserves the update and still permits checking again");
            reopened.Close();
            window.ShowCategory((FrameworkElement)window.FindName("AboutPage"));
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
                Capture(surface, Path.Combine(captureDirectory,language+"-available.png"));
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
            var dataDirectory = typeof(App).GetProperty(nameof(App.DataDirectory))!;
            var originalDirectory = dataDirectory.GetValue(null);
            var testDirectory = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N")));
            dataDirectory.SetValue(null, testDirectory.FullName);
            try { CheckStartupUpdates(release, captureDirectory, Check); }
            finally { dataDirectory.SetValue(null, originalDirectory); }
        }
        finally { available.SetValue(null, original); toast.Dismiss(); window.Close(); LocalizationWpf.Apply("zh-Hans"); }
        return count;
    }

    private static void Capture(FrameworkElement surface, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    private static void CheckStartupUpdates(UpdateRelease release, string captureDirectory, Action<bool, string> check)
    {
        var main = new MainWindow();
        var toast = (StatusToast)main.FindName("StartupUpdateToast");
        var message = (TextBlock)toast.FindName("MessageText");
        var update = (Button)toast.FindName("InstallPromptButton");
        var later = (Button)toast.FindName("LaterButton");
        var ignore = (Button)toast.FindName("IgnoreUpdateButton");
        var close = (Button)toast.FindName("ClosePromptButton");
        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        var method = typeof(MainWindow).GetMethod("CheckForAutomaticUpdateAsync", instance)!;
        var settingsField = typeof(MainWindow).GetField("_settings", instance)!;
        Task Start(Func<ConnectionSettings, CancellationToken, Task<UpdateCheckResult>> request) =>
            (Task)method.Invoke(main, [request])!;

        var settings = new ConnectionSettings { UseProxy = true };
        settingsField.SetValue(main, settings with { CheckForUpdatesOnStartup = false });
        var requests = 0;
        Task<UpdateCheckResult> Available(ConnectionSettings actual, CancellationToken token)
        {
            requests++;
            check(actual.RouteKey == settings.RouteKey, "Startup checks use the saved connection settings");
            return Task.FromResult(new UpdateCheckResult(UpdateStatus.Available, release));
        }
        Start(Available).GetAwaiter().GetResult();
        check(requests == 0 && toast.Visibility == Visibility.Collapsed, "Opting out skips the update request entirely");
        settingsField.SetValue(main, settings);
        foreach (var status in new[] { UpdateStatus.UpToDate, UpdateStatus.NoRelease, UpdateStatus.NotInstalled })
        {
            Start((_, _) => Task.FromResult(new UpdateCheckResult(status))).GetAwaiter().GetResult();
            check(toast.Visibility == Visibility.Collapsed, $"Startup stays quiet for {status}");
        }
        Start((_, _) => Task.FromException<UpdateCheckResult>(new HttpRequestException("offline"))).GetAwaiter().GetResult();
        check(toast.Visibility == Visibility.Collapsed, "An offline update check leaves startup usable and quiet");
        Start(Available).GetAwaiter().GetResult();
        check(requests == 1 && toast.Visibility == Visibility.Visible && message.Text == release.AvailableMessage &&
            update.Content?.ToString() == Strings.InstallUpdate && later.Visibility == Visibility.Visible &&
            later.Content?.ToString() == Strings.UpdateLater && ignore.Content?.ToString() == Strings.IgnoreUpdate &&
            close.Visibility == Visibility.Visible, "A new version offers install, skip, snooze and close");
        var surface = (FrameworkElement)main.Content;
        foreach (var language in new[] { "en", "zh-Hant", "ja", "zh-Hans" })
        foreach (var size in new[] { new Size(1380, 880), new Size(1120, 720) })
        {
            LocalizationWpf.Apply(language); toast.RefreshUpdatePrompt();
            surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
            Rect Bounds(FrameworkElement e) => new(e.TranslatePoint(new Point(), toast),e.RenderSize);
            var buttons = new[] { Bounds(ignore), Bounds(later), Bounds(update), Bounds(close) };
            check(buttons.All(b => b.Width > 0 && b.Height > 0 && new Rect(toast.RenderSize).Contains(b)) &&
                buttons[0].Right < buttons[1].Left && buttons[1].Right < buttons[2].Left &&
                buttons[3].Bottom <= buttons[2].Top && buttons[3].Right >= buttons[2].Right - 1 &&
                !Bounds(message).IntersectsWith(buttons[3]), $"All four update controls fit in {language} at {size}");
            Capture(surface, Path.Combine(captureDirectory, $"startup-{language}-{size.Width}x{size.Height}.png"));
        }
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var reopened = new SettingsWindow(null!, settings);
        check(toast.Visibility == Visibility.Collapsed && !File.Exists(Path.Combine(App.DataDirectory,"settings.json")) &&
            ((Button)reopened.FindName("InstallUpdateButton")).Visibility == Visibility.Visible,
            "Closing the prompt keeps the update available and does not write a reminder preference");
        reopened.Close();
        Start(Available).GetAwaiter().GetResult();
        ignore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var saved = ConnectionSettings.Load(App.DataDirectory);
        check(toast.Visibility == Visibility.Collapsed && saved.UpdateReminder.IgnoredVersion == release.Version.ToString(3),
            "Clicking Skip persists the offered version immediately");
        settingsField.SetValue(main,saved);
        Start(Available).GetAwaiter().GetResult();
        check(toast.Visibility == Visibility.Collapsed, "Automatic checks respect the skipped version");
        var newer = release with { Version = new Version(100,0,0) };
        Start((_,_)=>Task.FromResult(new UpdateCheckResult(UpdateStatus.Available,newer))).GetAwaiter().GetResult();
        check(toast.Visibility == Visibility.Visible && message.Text == newer.AvailableMessage,"A newer release is still offered");
        var before = DateTimeOffset.UtcNow;
        later.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        saved = ConnectionSettings.Load(App.DataDirectory);
        var timer = (DispatcherTimer)typeof(MainWindow).GetField("_updateReminderTimer",instance)!.GetValue(main)!;
        check(toast.Visibility == Visibility.Collapsed && saved.UpdateReminder.RemindAfterUtc >= before.AddDays(5) &&
            saved.UpdateReminder.RemindAfterUtc <= DateTimeOffset.UtcNow.AddDays(5) && timer.IsEnabled,
            "Snooze saves the exact deadline and schedules a check while the app stays open");
        settingsField.SetValue(main,saved);
        Start(Available).GetAwaiter().GetResult();
        check(toast.Visibility==Visibility.Collapsed,"Startup respects a saved five-day snooze");
        var dueCheck=typeof(MainWindow).GetMethod("CheckDueUpdateAsync",instance)!;
        var callsBeforeDue=requests;
        void Tick() => ((Task)dueCheck.Invoke(main,[(Func<ConnectionSettings,CancellationToken,Task<UpdateCheckResult>>)Available])!).GetAwaiter().GetResult();
        Tick();
        check(requests==callsBeforeDue && timer.IsEnabled,"An early timer tick waits for the saved UTC deadline");
        settingsField.SetValue(main,saved with {UpdateReminder=new(RemindAfterUtc:DateTimeOffset.UtcNow.AddSeconds(-1))});
        Tick();
        check(requests==callsBeforeDue+1 && !timer.IsEnabled && toast.Visibility==Visibility.Visible,
            "An open app rechecks the current release when the five-day reminder is due");
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = new SettingsWindow(null!, saved);
        var manual=typeof(SettingsWindow).GetMethod("CheckForUpdatesAsync",instance)!;
        Func<Task<UpdateCheckResult>> available=()=>Task.FromResult(new UpdateCheckResult(UpdateStatus.Available,release));
        ((Task)manual.Invoke(dialog,[available])!).GetAwaiter().GetResult();
        check(((StatusToast)dialog.FindName("UpdateToast")).Visibility==Visibility.Visible,"A manual check still offers a snoozed update");
        settingsField.SetValue(main,settings);
        typeof(MainWindow).GetMethod("ConfigureUpdateReminder",instance)!.Invoke(main,null);
        check(!timer.IsEnabled,"Removing a reminder stops its timer");
        dialog.ShowCategory((FrameworkElement)dialog.FindName("GeneralPage"));
        var applied = false;
        dialog.ApplyUpdate = _ => { applied = true; return Task.CompletedTask; };
        typeof(SettingsWindow).GetMethod("OfferStartupUpdate", instance)!.Invoke(dialog, [release]);
        var dialogToast = (StatusToast)dialog.FindName("UpdateToast");
        check(dialogToast.Visibility == Visibility.Visible && ((FrameworkElement)dialog.FindName("GeneralPage")).Visibility == Visibility.Visible,
            "A startup response received in settings offers the update without navigating away");
        ((Button)dialogToast.FindName("InstallPromptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.Dispatcher.Invoke(() => {}, DispatcherPriority.ContextIdle);
        check(!applied && ((FrameworkElement)dialog.FindName("AboutPage")).Visibility == Visibility.Visible &&
            ((TextBlock)dialogToast.FindName("MessageText")).Text == Strings.UpdateInstallFailed,
            "Update now starts the installer directly and the invalid test package is rejected before any download");
        dialog.Close();

        var response = new TaskCompletionSource<UpdateCheckResult>();
        CancellationToken checkToken = default;
        var pending = Start((_, token) => { checkToken = token; return response.Task; });
        check(!pending.IsCompleted, "Startup can continue while the version request is pending");
        typeof(MainWindow).GetMethod("CancelAutomaticUpdateCheck", instance)!.Invoke(main, null);
        response.SetResult(new(UpdateStatus.Available, release));
        main.Dispatcher.Invoke(() => {}, DispatcherPriority.ContextIdle);
        pending.GetAwaiter().GetResult();
        check(checkToken.IsCancellationRequested && toast.Visibility == Visibility.Collapsed,
            "Cancelling a startup check suppresses its late response");
    }
}
