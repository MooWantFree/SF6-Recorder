using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Sf6Prototype;

public partial class RecentMatchesStreamWindow : Window
{
    public string? Account { get; private set; }
    internal bool? PinOverride { get; private set; }

    public RecentMatchesStreamWindow(string? account, ConnectionSettings settings, bool? pinOverride = null)
    {
        Account = account;
        PinOverride = pinOverride;
        InitializeComponent();
        Card.Heading.Margin = new Thickness(0,0,34,0);
        AllowsTransparency = NeedsTransparency(settings);
        Background = AllowsTransparency ? Brushes.Transparent : (Brush)Panel.FindResource("Surface");
        ApplyBackground(settings);
        SourceInitialized += (_,_) =>
        {
            // Render opaque modes into a redirected HWND that OBS's automatic BitBlt path can read.
            if (!AllowsTransparency && PresentationSource.FromVisual(this) is HwndSource source)
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        };
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
        Card.ApplyAppearance(settings);
    }

    private void Pin_Changed(object sender, RoutedEventArgs e)
    {
        var pinned = PinButton.IsChecked == true;
        if (Topmost == pinned) return;
        PinOverride = pinned;
        Topmost = pinned;
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        // Leave keyboard focus visible for keyboard users; hide the mouse control once the pointer leaves.
        if (InputManager.Current.MostRecentInputDevice is MouseDevice) Panel.Focus();
    }

    internal void AllowInputDuringSettings()
    {
        // ShowDialog disables every existing top-level HWND on this dispatcher, including this independent overlay.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) EnableWindow(handle, true);
    }
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);

    internal void PlaceBehindSettings(Window settings)
    {
        if (Topmost || !IsVisible || WindowState == WindowState.Minimized) return;
        var overlay = new WindowInteropHelper(this).Handle;
        var dialog = new WindowInteropHelper(settings).Handle;
        if (overlay == IntPtr.Zero || dialog == IntPtr.Zero) return;
        const uint noSize = 0x0001, noMove = 0x0002, noActivate = 0x0010, noOwnerZOrder = 0x0200;
        SetWindowPos(overlay, dialog, 0, 0, 0, 0, noSize | noMove | noActivate | noOwnerZOrder);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void MoveWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

}
