using System.Windows;

namespace Sf6Prototype;

internal sealed partial class OverlayCaptureNotice : ChromeWindow
{
    private readonly Func<bool> _acknowledge;
    private bool _acknowledged;

    public OverlayCaptureNotice(Window overlay, Func<bool> acknowledge)
    {
        InitializeComponent();
        InitializeWindowChrome(WindowSurface, TitleBar);
        Owner = overlay;
        _acknowledge = acknowledge;
        // An owned notice closes with its overlay. Only the explicit action records acknowledgement.
        Closing += (_,e) => { if (!_acknowledged && !App.IsShuttingDown) e.Cancel = true; };
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (!_acknowledge()) return;
        _acknowledged = true;
        Close();
    }
}
