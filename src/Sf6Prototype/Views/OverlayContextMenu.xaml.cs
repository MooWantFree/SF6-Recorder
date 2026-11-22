using System.Windows;
using System.Windows.Controls;

namespace Sf6Prototype;

public partial class OverlayContextMenu : ContextMenu
{
    public event Action<bool>? TrendChanged;
    public event Action? ToggleGreen;
    public event Action? OpenSettings;
    public event Action? CloseOverlay;

    public OverlayContextMenu() => InitializeComponent();

    public void SetAppearance(ConnectionSettings settings)
    {
        TrendAction.IsChecked = settings.OverlayStyle.ShowTrend;
        Picker.Value = settings.OverlayBackgroundOpacity;
        Picker.GreenScreen = settings.OverlayGreenScreen;
        GreenAction.SetResourceReference(HeaderedItemsControl.HeaderProperty,
            settings.OverlayGreenScreen ? "I18n.DisableGreenScreen" : "I18n.EnableGreenScreen");
    }

    private void Trend_Click(object sender, RoutedEventArgs e) => TrendChanged?.Invoke(TrendAction.IsChecked);
    private void Green_Click(object sender, RoutedEventArgs e) => ToggleGreen?.Invoke();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => CloseOverlay?.Invoke();
}
