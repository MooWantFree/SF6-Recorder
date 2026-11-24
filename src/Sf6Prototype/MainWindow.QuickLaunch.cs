using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;

namespace Sf6Prototype;

public partial class MainWindow
{
    private int _launchIconVersion;

    private async void UpdateQuickLaunchAsync()
    {
        var version = ++_launchIconVersion;
        var launch = _settings.QuickLaunch;
        QuickLaunchButton.Visibility = launch.Target == QuickLaunchTarget.Off ? Visibility.Collapsed : Visibility.Visible;
        LaunchIcon.Source = null;
        LaunchFallback.Visibility = Visibility.Visible;
        if (launch.Target == QuickLaunchTarget.Off) return;

        var game = launch.Target == QuickLaunchTarget.Game;
        var label = game ? "I18n.QuickLaunchGame" : "I18n.QuickLaunchAccelerator";
        QuickLaunchButton.SetResourceReference(ToolTipProperty, label);
        QuickLaunchButton.SetResourceReference(AutomationProperties.NameProperty, label);
        LaunchFallback.Text = game ? "\uE7FC" : "\uE945";
        try
        {
            var icon = await Task.Run(() =>
            {
                var path = game && string.IsNullOrEmpty(launch.GamePath) ? SteamGame.Find() :
                    game ? launch.GamePath : launch.AcceleratorPath;
                return QuickLaunchSettings.IsProgram(path, launch.Target) && File.Exists(path) ? LaunchFileIcon.Read(path!) : null;
            });
            if (version != _launchIconVersion || _exitRequested || icon == null) return;
            LaunchIcon.Source = icon;
            LaunchFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Diagnostics.Write("quick_launch", "load_icon", error); }
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _exitRequested || _settings.QuickLaunch.Target == QuickLaunchTarget.Off) return;
        try { using var process = Process.Start(_settings.QuickLaunch.StartInfo()); }
        catch (FileNotFoundException) { ShowNotice(Strings.QuickLaunchFileMissing, true); }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        { Diagnostics.Write("quick_launch", "start_failed", error); ShowNotice(Strings.QuickLaunchFailed, true); }
    }
}
