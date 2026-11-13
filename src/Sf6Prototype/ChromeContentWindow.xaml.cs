using System.Windows;
using System.Windows.Shell;

namespace Sf6Prototype;

public partial class ChromeContentWindow : ChromeWindow
{
    public object? Body {get=>BodyHost.Content;set=>BodyHost.Content=value;}
    public ChromeContentWindow():this(false) { }
    public ChromeContentWindow(bool resizable)
    {
        InitializeComponent();InitializeWindowChrome(WindowSurface,TitleBar,resizable ? MaximizeButton : null);
        ResizeMode=resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        if(resizable) SizeToContent=SizeToContent.Manual;
        else
        {
            MinimizeButton.Visibility=MaximizeButton.Visibility=Visibility.Collapsed;
            WindowChrome.GetWindowChrome(this).ResizeBorderThickness=new Thickness(0);
        }
    }
}
