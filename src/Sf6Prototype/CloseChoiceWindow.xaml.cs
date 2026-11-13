using System.Windows;

namespace Sf6Prototype;

public partial class CloseChoiceWindow : ChromeWindow
{
    public CloseAction Choice { get; private set; }=CloseAction.Ask;
    public bool Remember=>RememberChoice.IsChecked==true;
    public CloseChoiceWindow(Window owner) { InitializeComponent(); InitializeWindowChrome(WindowSurface,TitleBar); Owner=owner; }
    private void Exit_Click(object sender,RoutedEventArgs e) { Choice=CloseAction.Exit; DialogResult=true; }
    private void MinimizeToTray_Click(object sender,RoutedEventArgs e) { Choice=CloseAction.MinimizeToTray; DialogResult=true; }
}
