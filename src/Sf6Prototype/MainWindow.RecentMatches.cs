using System.Windows;
using System.Windows.Controls.Primitives;

namespace Sf6Prototype;

public partial class MainWindow
{
    private RecentMatchesStreamWindow? _streamWindow;
    private Archive? _streamArchive;
    private OverlayContextMenu? _overlayMenu;
    private SettingsWindow? _overlayEditor;
    private ConnectionSettings OverlayAppearance => _overlayEditor?.OverlayAppearance(_settings) ?? _settings;

    private void SetStreamWindowOpen(bool open)
    {
        if (!open) _streamWindow?.Close();
        else if (_streamWindow == null && _ready) ShowStreamWindow(_archive.SignedInId);
        else if (!_ready) RecentMatches.StreamButton.IsChecked = false;
    }

    private void ShowStreamWindow(string? account, bool activate = true) =>
        ShowStreamWindow(new RecentMatchesStreamWindow(account, OverlayAppearance), activate);

    private void ShowStreamWindow(RecentMatchesStreamWindow window, bool activate, System.Drawing.Rectangle? bounds = null, bool minimized = false)
    {
        _streamWindow = window;
        RecentMatches.DisplayChanged += DisplayStreamMatches;
        var menu = _overlayMenu ??= CreateOverlayMenu();
        window.Panel.ContextMenu = menu;
        window.ContextMenuOpening += (_, e) =>
        {
            e.Handled = true;
            menu.SetAppearance(OverlayAppearance);
            // Keep the popup on a stable HWND while crossing the opaque/transparent boundary.
            Window placementOwner = (Window?)_overlayEditor ?? this;
            menu.PlacementTarget = placementOwner;
            var keyboard = e.CursorLeft < 0;
            menu.Placement = keyboard ? PlacementMode.RelativePoint : PlacementMode.MousePoint;
            var offset = keyboard ? placementOwner.PointFromScreen(window.PointToScreen(new Point(12, 12))) : new Point();
            menu.HorizontalOffset = offset.X;
            menu.VerticalOffset = offset.Y;
            menu.IsOpen = true;
        };
        window.Closed += (_, _) =>
        {
            RecentMatches.DisplayChanged -= DisplayStreamMatches;
            window.Panel.ContextMenu = null;
            if (_streamWindow != window) return;
            _streamWindow = null;
            RecentMatches.StreamButton.IsChecked = false;
            _streamArchive = null;
            menu.IsOpen = false;
            _overlayEditor?.SetStreamPreview(false);
        };
        UpdateRecentMatches();
        window.Show(activate, bounds, minimized);
        RecentMatches.StreamButton.IsChecked = true;
        if (!_settings.OverlayCaptureTipAcknowledged)
            new OverlayCaptureNotice(window, AcknowledgeOverlayCaptureTip).Show();
    }

    private bool AcknowledgeOverlayCaptureTip()
    {
        var settings = _settings with { OverlayCaptureTipAcknowledged = true };
        try
        {
            settings.Save(App.DataDirectory);
            _settings = settings;
            return true;
        }
        catch (Exception error) { ReportError(error); return false; }
    }

    private OverlayContextMenu CreateOverlayMenu()
    {
        var menu = new OverlayContextMenu();
        var pendingOpacity = false;
        void CommitOpacity()
        {
            if (!pendingOpacity) return;
            pendingOpacity = false;
            if (_overlayEditor == null && menu.Picker.Value != _settings.OverlayBackgroundOpacity)
                SaveOverlayAppearance(_settings with { OverlayBackgroundOpacity = menu.Picker.Value });
        }
        menu.Picker.Changed += () =>
        {
            if (!menu.IsOpen) return;
            pendingOpacity = true;
            if (_overlayEditor is { } editor) editor.OverlayOpacity.Value = menu.Picker.Value;
            else ApplyOverlaySettings(_settings with { OverlayBackgroundOpacity = menu.Picker.Value });
        };
        menu.Picker.Committed += () => { if (menu.IsOpen) CommitOpacity(); };
        menu.Closed += (_,_) => CommitOpacity();
        menu.ToggleGreen += () =>
        {
            if (_overlayEditor is { } editor) editor.OverlayGreen.IsChecked = editor.OverlayGreen.IsChecked != true;
            else SaveOverlayAppearance(_settings with { OverlayBackgroundOpacity = menu.Picker.Value, OverlayGreenScreen = !_settings.OverlayGreenScreen });
        };
        menu.OpenSettings += () =>
        {
            CommitOpacity();
            menu.IsOpen = false;
            // Let the context menu finish restoring focus before activating the settings dialog.
            Dispatcher.BeginInvoke(() =>
            {
                if (_streamWindow is { IsVisible: true }) OpenSettings(overlayPage: true);
            }, System.Windows.Threading.DispatcherPriority.Background);
        };
        menu.CloseOverlay += () => _streamWindow?.Close();
        return menu;
    }

    private void SaveOverlayAppearance(ConnectionSettings next)
    {
        try
        {
            if (!App.Capture && !string.IsNullOrEmpty(App.DataDirectory)) next.Save(App.DataDirectory);
            _settings = next;
            ApplyOverlaySettings(_settings);
        }
        catch (Exception ex) { ApplyOverlaySettings(_settings); ReportError(ex); }
    }

    private void ApplyOverlaySettings(ConnectionSettings appearance)
    {
        if (_streamWindow is not { } current) return;
        if (current.AllowsTransparency == RecentMatchesStreamWindow.NeedsTransparency(appearance))
        {
            current.ApplyBackground(appearance);
            return;
        }
        // WPF fixes AllowsTransparency when the HWND is created.
        var bounds = current.Bounds;
        var minimized = current.Minimized;
        var replacement = new RecentMatchesStreamWindow(current.Account, appearance, current.PinOverride);
        _streamWindow = null;
        current.Close();
        ShowStreamWindow(replacement, false, bounds, minimized);
        if (_overlayEditor is { IsActive: true } editor) replacement.PlaceBehindSettings(editor);
    }

    private void DisplayStreamMatches(RecentMatchDisplay live) =>
        _streamWindow?.Display(_overlayEditor?.StreamSample ?? live);

    private void UpdateRecentMatches()
    {
        var visible=_ready && _settings.RefreshAfterMatch && PageTabs.SelectedIndex==0 &&
            _archive.Owner==_archive.SignedInId && ViewedHistoryOwner==_archive.SignedInId;
        RecentMatches.Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
        RecentMatches.IsCollapsed=_settings.RecentMatchesCollapsed;
        if (_streamWindow is { } stream)
        {
            if (stream.Account == null && _archive.SignedInId is { } signedIn) stream.AttachAccount(signedIn);
            if (!_ready || stream.Account != _archive.SignedInId) stream.Close();
            else if (stream.Account == null)
            {
                DisplayStreamMatches(new("", "", "—", "—", "—", "Ink", "—", [], Strings.NoRecentRankedMatches, true));
                return;
            }
        }
        if (!visible && _streamWindow == null) return;
        var source = _archive;
        if (_streamWindow?.Account is { } account && source.Owner != account)
        {
            // While browsing someone else, keep the broadcast on the streamer's local records.
            _streamArchive ??= new Archive(_archive.Store, _archive.Profiles);
            _streamArchive.Load(account, refreshImages: false);
            source = _streamArchive;
        }
        RecentMatches.Reload(source, App.StartedAt);
    }
    private void RecentMatchesCollapsedChanged(bool collapsed)
    {
        if(!_ready || _settings.RecentMatchesCollapsed==collapsed) return;
        _settings=_settings with {RecentMatchesCollapsed=collapsed};
        if(!App.Capture && !string.IsNullOrWhiteSpace(App.DataDirectory))
        {
            try {_settings.Save(App.DataDirectory);}
            catch(Exception ex) {ReportError(ex);}
        }
    }
}
