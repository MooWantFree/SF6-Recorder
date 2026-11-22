using System.Windows;

namespace Sf6Prototype;

internal sealed partial class OverlayCaptureNotice : ChromeWindow
{
    private readonly Func<bool> _acknowledge;
    private bool _acknowledged;

    public OverlayCaptureNotice(RecentMatchesStreamWindow overlay, Func<bool> acknowledge)
    {
        InitializeComponent();
        InitializeWindowChrome(WindowSurface, TitleBar);
        new System.Windows.Interop.WindowInteropHelper(this).Owner = overlay.Handle;
        overlay.Closed += OverlayClosed;
        Closed += (_, _) => overlay.Closed -= OverlayClosed;
        _acknowledge = acknowledge;
        // An owned notice closes with its overlay. Only the explicit action records acknowledgement.
        Closing += (_,e) => { if (!_acknowledged && !App.IsShuttingDown) e.Cancel = true; };
    }

    private void OverlayClosed(object? sender, EventArgs e) { _acknowledged = true; Close(); }

    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (!_acknowledge()) return;
        _acknowledged = true;
        Close();
    }
}
