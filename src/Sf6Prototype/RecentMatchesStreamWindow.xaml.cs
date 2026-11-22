using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Sf6Prototype;

public partial class RecentMatchesStreamWindow : UserControl
{
    internal const double DefaultWidth = 560, DefaultHeight = 232;
    private OverlayWindowHost? _host;
    private bool _closed;
    public event EventHandler? Closed;
    public string? Account { get; private set; }
    internal bool? PinOverride { get; private set; }
    public bool Topmost { get; private set; }
    public bool AllowsTransparency { get; }
    internal nint Handle => _host?.Handle ?? 0;
    internal System.Drawing.Rectangle? Bounds => _host?.Bounds;
    internal bool Minimized => _host?.Minimized == true;

    public RecentMatchesStreamWindow(string? account, ConnectionSettings settings, bool? pinOverride = null)
    {
        Account = account;
        PinOverride = pinOverride;
        InitializeComponent();
        Card.Heading.Margin = new Thickness(0,0,34,0);
        AllowsTransparency = NeedsTransparency(settings);
        ApplyBackground(settings);
    }

    internal void Show(bool activate, System.Drawing.Rectangle? bounds, bool minimized)
    {
        _host = new(this);
        _host.Open(activate, bounds, minimized);
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _host?.Close();
        _host = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Display(RecentMatchDisplay display)
    {
        DataContext = display;
        Card.Display(display);
    }

    internal void AttachAccount(string account) => Account = account;

    public static bool NeedsTransparency(ConnectionSettings settings) => !settings.OverlayGreenScreen && settings.OverlayBackgroundOpacity < 100;

    public void ApplyBackground(ConnectionSettings settings)
    {
        Topmost = PinOverride ?? settings.OverlayAlwaysOnTop;
        PinButton.IsChecked = Topmost;
        _host?.Pin(Topmost);
        Card.ApplyAppearance(settings);
    }

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        var pinned = PinButton.IsChecked == true;
        if (Topmost == pinned) return;
        PinOverride = Topmost = pinned;
        _host?.Pin(pinned);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (InputManager.Current.MostRecentInputDevice is MouseDevice) Panel.Focus();
    }

    internal void AllowInputDuringSettings() => _host?.EnableInput();

    internal void PlaceBehindSettings(Window settings)
    {
        var dialog = new WindowInteropHelper(settings).Handle;
        if (!Topmost && !Minimized && dialog != 0)
            _host?.PlaceBehind(dialog);
    }

    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) _host?.Drag();
    }
}
