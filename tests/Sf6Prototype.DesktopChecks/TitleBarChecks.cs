using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Sf6Prototype;

internal static class TitleBarChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var window=new MainWindow();window.ApplyTemplate();
        var root=(FrameworkElement)window.Content;
        var header=(Grid)window.FindName("TitleBar");
        var navigation=(StackPanel)window.FindName("NavigationBar");
        var tabs=(ListBox)window.FindName("PageTabs");
        var name=(TextBlock)window.FindName("ViewedPlayerName");
        var search=(Border)window.FindName("PlayerSearchField");
        var account=(Button)window.FindName("AccountButton");
        var captions=(StackPanel)window.FindName("CaptionButtons");
        ((TextBlock)window.FindName("AccountName")).Text="很长的账号昵称用于验证";
        ((Button)window.FindName("CancelButton")).Visibility=Visibility.Visible;
        name.Text="很长的玩家昵称仍然留出拖动空间";
        ((TextBlock)window.FindName("UpdatedLabel")).Text="更新于 2025-11-13 12:34";
        window.SetValue(MainWindow.IsPlayerSearchActiveProperty,true);
        ((TextBox)window.FindName("PlayerUidBox")).Text="1234567890";
        double Left(FrameworkElement element)=>element.TranslatePoint(new Point(),header).X;
        double Right(FrameworkElement element)=>element.TranslatePoint(new Point(element.ActualWidth,0),header).X;
        foreach(var width in new[]{1108d,1120,1380,1920})
        {
            root.Measure(new Size(width,880));root.Arrange(new Rect(0,0,width,880));root.UpdateLayout();
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);root.UpdateLayout();
            Check(Right(tabs)<=Left(name) && Right(name)<=Left(search) && Right(account)<=Left(captions),$"Toolbar regions do not overlap at {width} DIP with all controls visible");
            Check(name.ActualWidth>=120 && Right(captions)<=header.ActualWidth,$"The title retains a usable drag region and window buttons stay on screen at {width} DIP");
            var entry=(TextBox)window.FindName("PlayerUidBox");
            var submit=(Button)window.FindName("PlayerSearchSubmit");
            Check(entry.ActualWidth>=70 && Right(entry)<=Left(submit),$"The expanded player search has room to type and submit at {width} DIP");
        }
        Check(WindowChrome.GetIsHitTestVisibleInChrome(navigation) && WindowChrome.GetIsHitTestVisibleInChrome(search) &&
            !WindowChrome.GetIsHitTestVisibleInChrome(name),"Navigation and search remain interactive while the center title is draggable");
        Check(captions.Children.OfType<Button>().All(b=>!string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.FocusVisualStyle!=null),"Window controls expose accessible names and keyboard focus");
        Check(new WindowInteropHelper(window).Handle==IntPtr.Zero,"Title bar layout checks do not create a native window");
        return count;
    }
}
