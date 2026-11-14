using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        var refresh=(Button)window.FindName("SyncButton");
        var cancel=(Button)window.FindName("CancelButton");
        var ring=(RefreshProgressGlyph)window.FindName("SyncRing");
        void Call(string method,params object[] args)=>typeof(MainWindow).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);
        ((TextBlock)window.FindName("AccountName")).Text="很长的账号昵称用于验证";
        name.Text="很长的玩家昵称仍然留出拖动空间";
        ((TextBlock)window.FindName("UpdatedLabel")).Text="更新于 2025-11-13 12:34";
        window.SetValue(MainWindow.IsPlayerSearchActiveProperty,true);
        ((TextBox)window.FindName("PlayerUidBox")).Text="1234567890";
        double Left(FrameworkElement element)=>element.TranslatePoint(new Point(),header).X;
        double Right(FrameworkElement element)=>element.TranslatePoint(new Point(element.ActualWidth,0),header).X;
        Rect Bounds(FrameworkElement element)=>new(element.TranslatePoint(new Point(),header),element.RenderSize);
        void Layout(double width)
        {
            root.Measure(new Size(width,880));root.Arrange(new Rect(0,0,width,880));root.UpdateLayout();
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);root.UpdateLayout();
        }
        foreach(var width in new[]{1108d,1120,1380,1920})
        {
            Layout(width);
            Check(Right(tabs)<=Left(name) && Right(name)<=Left(search) && Right(account)<=Left(captions),$"Toolbar regions do not overlap at {width} DIP with all controls visible");
            Check(name.ActualWidth>=120 && Right(captions)<=header.ActualWidth,$"The title retains a usable drag region and window buttons stay on screen at {width} DIP");
            var entry=(TextBox)window.FindName("PlayerUidBox");
            var submit=(Button)window.FindName("PlayerSearchSubmit");
            Check(entry.ActualWidth>=70 && Right(entry)<=Left(submit),$"The expanded player search has room to type and submit at {width} DIP");
            var idleName=Bounds(name);var idleRefresh=Bounds(refresh);var idleTabs=Bounds(tabs);var idleNavigation=Bounds(navigation);
            void Stable(string state)
            {
                Layout(width);
                Check(Bounds(name)==idleName && Bounds(refresh)==idleRefresh && Bounds(tabs)==idleTabs && Bounds(navigation)==idleNavigation,
                    $"Player name, refresh button and navigation keep identical bounds during {state} at {width} DIP");
            }
            Call("SetBusy",true,false,false);Stable("local preparation");
            Call("RequestProgress",new RequestActivity(RequestPhase.Waiting));Stable("request queueing");
            Call("RequestProgress",new RequestActivity(RequestPhase.Sending));Stable("network progress");
            Check(ring.Visibility==Visibility.Visible && cancel.Visibility==Visibility.Visible,"Network progress exposes the reserved cancel action");
            var model=(RefreshProgressState)typeof(MainWindow).GetField("_refreshProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;
            var status=(TextBlock)window.FindName("UpdatedLabel");
            foreach(var culture in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                LocalizationWpf.Apply(culture);
                model.Begin();
                model.Load(new("",null,123,null,""),0);Call("UpdateButtonProgress");Layout(width);
                var measured=new FormattedText(status.Text,status.Language.GetSpecificCulture(),status.FlowDirection,
                    new Typeface(status.FontFamily,status.FontStyle,status.FontWeight,status.FontStretch),status.FontSize,Brushes.Black,
                    null,TextOptions.GetTextFormattingMode(status),VisualTreeHelper.GetDpi(status).PixelsPerDip).WidthIncludingTrailingWhitespace;
                Check(measured<=status.ActualWidth+.5,$"Visible header progress fits without hover in {culture} at {width} DIP ({measured:F1}/{status.ActualWidth:F1})");
                Save(header,$"header-reading-{culture}-{width:0}.png");
                model.Load(new("",.5,3,4,""),1);Call("UpdateButtonProgress");Layout(width);
                measured=new FormattedText(status.Text,status.Language.GetSpecificCulture(),status.FlowDirection,
                    new Typeface(status.FontFamily,status.FontStyle,status.FontWeight,status.FontStretch),status.FontSize,Brushes.Black,
                    null,TextOptions.GetTextFormattingMode(status),VisualTreeHelper.GetDpi(status).PixelsPerDip).WidthIncludingTrailingWhitespace;
                Check(measured<=status.ActualWidth+.5,$"Visible remaining time fits in {culture} at {width} DIP");
                Save(header,$"header-estimate-{culture}-{width:0}.png");
            }
            LocalizationWpf.Apply("zh-Hans");Stable("estimated progress");
            Call("RecordRefreshResult","",4);Call("FinishButtonProgress",true,false);Call("SetBusy",false,false,false);Stable("completion");
            Save(header,$"header-complete-{width:0}.png");
            Check(cancel.Visibility==Visibility.Hidden,"Completion hides the cancel action while preserving its slot");
            Call("HideButtonProgress");Stable("return to idle");
            Call("ClearRefreshResult");
        }
        Check(WindowChrome.GetIsHitTestVisibleInChrome(navigation) && WindowChrome.GetIsHitTestVisibleInChrome(search) &&
            !WindowChrome.GetIsHitTestVisibleInChrome(name),"Navigation and search remain interactive while the center title is draggable");
        Check(captions.Children.OfType<Button>().All(b=>!string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.FocusVisualStyle!=null),"Window controls expose accessible names and keyboard focus");
        Check(new WindowInteropHelper(window).Handle==IntPtr.Zero,"Title bar layout checks do not create a native window");
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
