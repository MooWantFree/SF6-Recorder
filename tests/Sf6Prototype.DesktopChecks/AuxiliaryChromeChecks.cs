using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Sf6Prototype;

internal static class AuxiliaryChromeChecks
{
    public static int Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var login=(LoginWindow)Activator.CreateInstance(typeof(LoginWindow),BindingFlags.Instance|BindingFlags.NonPublic,null,[null!,new ConnectionSettings(),new CookieVault(root)],null)!;
        var confirmation=new MessageWindow(null,"确认退出当前账号？",Strings.SignOutAccount,Strings.SignOut);
        var notice=new MessageWindow(null,Strings.DataDirectoryInUse,Strings.AppTitle);
        var choice=new CloseChoiceWindow(null!);
        var popup=new ChromeContentWindow(true) {Title=Strings.OfficialCapcomSignIn,Width=1000,Height=800,Body=new TextBlock {Text="CAPCOM ID",Margin=new Thickness(24)}};
        var windows=new Window[]{login,confirmation,notice,choice,popup};
        try
        {
            foreach(var window in windows)
            {
                window.ApplyTemplate();var content=(FrameworkElement)window.Content;
                var height=window.SizeToContent==SizeToContent.Height ? double.PositiveInfinity : window.Height;
                content.Measure(new Size(window.Width,height));content.Arrange(new Rect(new Size(window.Width,double.IsFinite(height) ? height : content.DesiredSize.Height)));content.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);content.UpdateLayout();
                Check(WindowChrome.GetWindowChrome(window) is {CaptionHeight:56,UseAeroCaptionButtons:false},"Auxiliary windows use the common custom title bar");
                Check(new WindowInteropHelper(window).Handle==IntPtr.Zero && !window.IsLoaded,"Auxiliary checks never open native windows or start login navigation");
                Save(content,window==popup ? "login-popup.png" : window==notice ? "notice.png" : window.GetType().Name+".png");
            }
            var buttons=((StackPanel)((StackPanel)confirmation.Body!).Children[1]).Children.OfType<Button>().ToArray();
            Check(buttons.Length==2 && buttons[0].IsCancel && buttons[0].IsDefault && !buttons[1].IsDefault && confirmation.DialogResult!=true,"Account confirmation defaults to cancel instead of approving on Enter");
            Check(choice.Choice==CloseAction.Ask && !choice.Remember,"Restyling the close choice does not change the exit preference");
            Check(login.Body is Grid && ((Button)login.FindName("MaximizeButton")).Visibility==Visibility.Visible && ((Button)confirmation.FindName("MaximizeButton")).Visibility==Visibility.Collapsed,"Login hosts remain resizable while confirmation dialogs retain their compact close-only chrome");
            Check(App.DataDirectory=="" && !Directory.EnumerateFileSystemEntries(root).Any(),"Login layout checks never initialize WebView2 or write session data");
        }
        finally {foreach(var window in windows)window.Close();}
        return count;
    }
    private static void Save(FrameworkElement element,string name)
    {
        if(Environment.GetEnvironmentVariable("SF6_LAYOUT_CAPTURES") is not {Length:>0} directory)return;
        Directory.CreateDirectory(directory);
        var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(directory,name));encoder.Save(file);
    }
}
