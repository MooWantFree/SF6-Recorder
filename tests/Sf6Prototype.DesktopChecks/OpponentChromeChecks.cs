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

internal static class OpponentChromeChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var directory=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var session=new SavedSession("1234567890","Fixture","Sf6Checks/1.0","fixture","",[]);
        var window=new OpponentWindow(null!,"9999999999","很长的玩家昵称用于检查标题栏布局",new ConnectionSettings(),session,new CookieVault(directory),new ProfileCache(directory)) {Suspended=true};
        try
        {
            window.ApplyTemplate();
            var root=(FrameworkElement)window.Content;
            var title=(Grid)window.FindName("TitleBar");
            var name=(TextBlock)window.FindName("PlayerName");
            var uid=(Button)window.FindName("UidButton");
            var refresh=(Button)window.FindName("RefreshButton");
            var captions=(StackPanel)window.FindName("CaptionButtons");
            var ranks=(DataGrid)window.FindName("RankGrid");
            // Render a synthetic result while the query session remains suspended.
            ((FrameworkElement)window.FindName("LoadingState")).Visibility=Visibility.Collapsed;
            ranks.Visibility=Visibility.Visible;
            ranks.ItemsSource=new[]
            {
                new OpponentWindow.RankRow(new(1,"RYU",25000,1850,36,null),null,false,"隆"),
                new OpponentWindow.RankRow(new(2,"LUKE",23365,null,34,null),null,false,"卢克")
            };
            ((ComboBox)window.FindName("SeasonPicker")).ItemsSource=new[]{new OpponentWindow.SeasonChoice(10,10)};
            ((ComboBox)window.FindName("SeasonPicker")).SelectedIndex=0;
            ((TextBlock)window.FindName("Status")).Text="更新于 12:34:56";
            foreach(var language in new[]{"zh-Hans","en","zh-Hant","ja"})
            {
                LocalizationWpf.Apply(language);
                foreach(var size in new[]{new Size(740,580),new Size(660,380)})
                {
                    root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
                    window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);root.UpdateLayout();
                    double Left(FrameworkElement item)=>item.TranslatePoint(new Point(),title).X;
                    double Right(FrameworkElement item)=>item.TranslatePoint(new Point(item.ActualWidth,0),title).X;
                    Check(name.ActualWidth>=160 && Right(name)<=Left(refresh) && Right(uid)<=Left(refresh),$"Opponent identity and refresh do not overlap at {size.Width} DIP in {language}");
                    Check(Right(refresh)<=Left(captions) && Right(captions)<=title.ActualWidth,$"Opponent window controls stay on screen at {size.Width} DIP in {language}");
                    Check(ranks.ActualHeight>=180 && ranks.TranslatePoint(new Point(0,0),root).Y>=title.ActualHeight,$"Rank results retain usable space below the integrated title at {size.Width} DIP in {language}");
                    Save(root,$"opponent-{language}-{size.Width}x{size.Height}.png");
                }
            }
            var chrome=WindowChrome.GetWindowChrome(window);
            Check(chrome is {CaptionHeight:56,UseAeroCaptionButtons:false} && chrome.ResizeBorderThickness==new Thickness(6),"Opponent windows use the shared native drag and resize chrome");
            Check(WindowChrome.GetIsHitTestVisibleInChrome(uid) && WindowChrome.GetIsHitTestVisibleInChrome(refresh) && !WindowChrome.GetIsHitTestVisibleInChrome(name),"UID and refresh receive clicks while the player name remains a drag target");
            Check(captions.Children.OfType<Button>().Count()==3 && captions.Children.OfType<Button>().All(b=>!string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.FocusVisualStyle!=null),"Opponent caption buttons expose localized names and keyboard focus");
            Check(!window.QueryInProgress && App.DataDirectory=="" && new WindowInteropHelper(window).Handle==IntPtr.Zero,"Opponent chrome checks use synthetic data without opening a native window or querying the site");
        }
        finally {window.Close();LocalizationWpf.Apply("zh");}
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
