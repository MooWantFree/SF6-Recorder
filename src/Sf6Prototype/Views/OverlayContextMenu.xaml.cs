using System.Windows;
using System.Windows.Controls;

namespace Sf6Prototype;

public partial class OverlayContextMenu : ContextMenu
{
    public event Action? ToggleGreen;
    public event Action? OpenSettings;
    public event Action? CloseOverlay;

    public OverlayContextMenu() => InitializeComponent();

    public void SetAppearance(ConnectionSettings settings)
    {
        Picker.Value = settings.OverlayBackgroundOpacity;
        Picker.GreenScreen = settings.OverlayGreenScreen;
        GreenAction.SetResourceReference(HeaderedItemsControl.HeaderProperty,
            settings.OverlayGreenScreen ? "I18n.DisableGreenScreen" : "I18n.EnableGreenScreen");
    }

    private void Green_Click(object sender, RoutedEventArgs e) => ToggleGreen?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseOverlay?.Invoke();
}
