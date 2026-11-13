using System.Windows;

namespace Sf6Prototype;

public partial class MainWindow
{
    private void UpdateRecentMatches()
    {
        var visible=_ready && _settings.RefreshAfterMatch && PageTabs.SelectedIndex==0 &&
            _archive.Owner==_archive.SignedInId && ViewedHistoryOwner==_archive.SignedInId;
        RecentMatches.Visibility=visible ? Visibility.Visible : Visibility.Collapsed;
        RecentMatches.IsCollapsed=_settings.RecentMatchesCollapsed;
        if(visible) RecentMatches.Reload(_archive,App.StartedAt);
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
