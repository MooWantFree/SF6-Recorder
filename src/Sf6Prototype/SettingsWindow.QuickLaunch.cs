using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Sf6Prototype;

public partial class SettingsWindow
{
    private QuickLaunchSettings _launchDraft = new();
    private string? _detectedGame;
    private int _searchVersion;
    private bool LaunchNeedsAccelerator => _launchDraft.Target == QuickLaunchTarget.Accelerator &&
        !QuickLaunchSettings.IsProgram(_launchDraft.AcceleratorPath, QuickLaunchTarget.Accelerator);

    private void InitializeQuickLaunch(QuickLaunchSettings settings)
    {
        _launchDraft = settings;
        LaunchTargetBox.SelectedValue = settings.Target.ToString();
        LaunchFileLabel.SizeChanged += (_, e) => { if (e.WidthChanged) ShowLaunchFile(); };
        ShowLaunchFile();
        LaunchTargetBox.SelectionChanged += async (_, _) =>
        {
            if (!Enum.TryParse<QuickLaunchTarget>(LaunchTargetBox.SelectedValue as string, out var target))
            {
                LaunchTargetBox.SelectedValue = _launchDraft.Target.ToString();
                return;
            }
            if (target == _launchDraft.Target) return;
            _searchVersion++;
            FindGameButton.IsEnabled = true;
            _launchDraft = _launchDraft with { Target = target };
            await ShowSelectedLaunchFileAsync();
        };
        Loaded += async (_, _) => await ShowSelectedLaunchFileAsync();
    }

    private async Task ShowSelectedLaunchFileAsync()
    {
        ShowLaunchFile();
        if (_launchDraft.Target == QuickLaunchTarget.Game && string.IsNullOrEmpty(_launchDraft.GamePath) && string.IsNullOrEmpty(_detectedGame))
            await FindGameAsync(explicitSearch:false);
    }

    private void ShowLaunchFile()
    {
        var game = _launchDraft.Target == QuickLaunchTarget.Game;
        LaunchFilePanel.Visibility = _launchDraft.Target == QuickLaunchTarget.Off ? Visibility.Collapsed : Visibility.Visible;
        FindGameButton.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
        var path = game ? _launchDraft.GamePath : _launchDraft.AcceleratorPath;
        if (game && string.IsNullOrEmpty(path)) path = _detectedGame;
        LaunchFileLabel.ToolTip = path;
        if (!string.IsNullOrEmpty(path)) LaunchFileLabel.Text = CompactLaunchPath(path);
        else if (game && _detectedGame == null) LaunchFileLabel.Text = "";
        else LaunchFileLabel.SetResourceReference(TextBlock.TextProperty, game ? "I18n.QuickLaunchNoGame" : "I18n.QuickLaunchChooseAccelerator");
    }

    private string CompactLaunchPath(string path)
    {
        var label = LaunchFileLabel;
        var width = label.ActualWidth;
        if (width <= 0) return path;
        var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(label).PixelsPerDip;
        bool Fits(string text) => new FormattedText(text, label.Language.GetSpecificCulture(), label.FlowDirection,
            typeface, label.FontSize, label.Foreground, null, TextOptions.GetTextFormattingMode(label), dpi).WidthIncludingTrailingWhitespace <= width;
        if (Fits(path)) return path;

        var root = Path.GetPathRoot(path) ?? "";
        var file = Path.GetFileName(path);
        var suffix = path.Length - file.Length;
        var prefix = root + "…" + Path.DirectorySeparatorChar;
        if (Fits(prefix + file))
        {
            // Keep whole parent directories nearest the file while they fit.
            while (suffix > root.Length)
            {
                var parent = Path.GetDirectoryName(path[..suffix].TrimEnd('\\', '/'));
                if (parent == null) break;
                var start = Path.EndsInDirectorySeparator(parent) ? parent.Length : parent.Length + 1;
                if (start >= suffix || !Fits(prefix + path[start..])) break;
                suffix = start;
            }
            return prefix + path[suffix..];
        }
        if (Fits(file)) return file;

        // When even the filename overflows, preserve its ending and extension.
        var boundaries = StringInfo.ParseCombiningCharacters(file);
        var low = 0;
        var high = boundaries.Length;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (Fits("…" + file[boundaries[mid]..])) high = mid;
            else low = mid + 1;
        }
        return low == boundaries.Length ? "…" : "…" + file[boundaries[low]..];
    }

    private async void FindGame_Click(object sender, RoutedEventArgs e) => await FindGameAsync(explicitSearch:true);

    private async Task FindGameAsync(bool explicitSearch)
    {
        var version = ++_searchVersion;
        FindGameButton.IsEnabled = false;
        var found = await Task.Run(SteamGame.Find);
        if (_closing.IsCancellationRequested || version != _searchVersion) return;
        FindGameButton.IsEnabled = true;
        _detectedGame = found ?? "";
        if (found != null) _launchDraft = _launchDraft with { GamePath = "" };
        else if (explicitSearch) UpdateToast.Show(Strings.QuickLaunchNoGame, true);
        ShowLaunchFile();
    }

    private void ChooseLaunchFile_Click(object sender, RoutedEventArgs e)
    {
        var game = _launchDraft.Target == QuickLaunchTarget.Game;
        var dialog = new OpenFileDialog { Filter = game ? Strings.QuickLaunchExeFilter : Strings.QuickLaunchShortcutFilter,
            CheckFileExists = true, DereferenceLinks = game };
        if (dialog.ShowDialog(this) != true) return;
        if (!QuickLaunchSettings.IsProgram(dialog.FileName, _launchDraft.Target))
        {
            UpdateToast.Show(game ? Strings.QuickLaunchFileMissing : Strings.QuickLaunchChooseAccelerator, true);
            return;
        }
        _searchVersion++;
        FindGameButton.IsEnabled = true;
        _launchDraft = game ? _launchDraft with { GamePath = dialog.FileName } : _launchDraft with { AcceleratorPath = dialog.FileName };
        ShowError(null);
        ShowLaunchFile();
    }
}
