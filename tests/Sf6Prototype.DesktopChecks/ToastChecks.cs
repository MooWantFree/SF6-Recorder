using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sf6Prototype;

internal static class ToastChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool ok,string message) {if(!ok) throw new Exception(message);count++;}
        var main=new MainWindow();var toast=(StatusToast)main.FindName("Toast");
        var text=(TextBlock)toast.FindName("MessageText");var button=(Button)toast.FindName("MessageButton");
        var action=(Button)toast.FindName("ActionButton");var progress=(ProgressBar)toast.FindName("Progress");
        var timer=(DispatcherTimer)typeof(StatusToast).GetField("_timer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(toast)!;
        toast.Show(Strings.Saved);
        Check(timer.IsEnabled && timer.Interval==TimeSpan.FromSeconds(5),"Ordinary toasts auto-dismiss after five seconds");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(toast.Visibility==Visibility.Collapsed && !timer.IsEnabled,"Clicking an ordinary toast dismisses it and stops its timer");
        toast.BeginProgress(Strings.SavedLoadingCharacterNames);
        Check(toast.IsLoading && !timer.IsEnabled && progress.Visibility==Visibility.Visible,"Language loading shows progress without starting an expiry timer");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));toast.Dismiss();toast.Show("Unrelated notification");
        Check(toast.Visibility==Visibility.Visible && text.Text==Strings.SavedLoadingCharacterNames,"Clicks, programmatic dismissal and ordinary notifications cannot hide an active language toast");
        toast.UpdateProgress(Strings.SavedWaitingCharacterNames);
        Check(text.Text==Strings.SavedWaitingCharacterNames && !timer.IsEnabled,"Request-gate waiting updates the same persistent toast");
        toast.CompleteProgress(Strings.SavedCharacterNamesReady);
        Check(!toast.IsLoading && timer.IsEnabled && !progress.IsIndeterminate && button.Focusable,"Completion stops the animation and enables keyboard dismissal and expiry");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(toast.Visibility==Visibility.Collapsed,"Completed language toasts can be clicked away immediately");
        toast.BeginProgress("Loading");toast.CompleteProgress(Strings.SavedCharacterNamesFailed,true);
        timer.Interval=TimeSpan.FromMilliseconds(1);
        var frame=new DispatcherFrame();var timeout=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
        timeout.Tick+=(_,_)=>{timeout.Stop();frame.Continue=false;};timeout.Start();Dispatcher.PushFrame(frame);
        Check(toast.Visibility==Visibility.Collapsed && !timer.IsEnabled,"Failed language loading also expires after completion");
        timer.Interval=TimeSpan.FromSeconds(5);
        var signedIn=0;
        toast.BeginProgress("Loading");toast.CompleteProgress(Strings.SavedCharacterNamesSignIn,true,Strings.SignIn,()=>signedIn++);
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(signedIn==1 && toast.Visibility==Visibility.Collapsed,"An expired-session toast preserves its explicit sign-in action");
        var surface=(FrameworkElement)main.Content;
        var directory=Path.Combine(Environment.CurrentDirectory,".local","checks","toast-captures");Directory.CreateDirectory(directory);
        try
        {
            foreach(var language in new[]{"en","zh-Hans","zh-Hant","ja"})
            {
                LocalizationWpf.Apply(language);
                foreach(var size in new[]{new Size(1380,880),new Size(1120,720)})
                {
                    toast.Dismiss();Layout(surface,size);
                    var history=(FrameworkElement)main.FindName("History");var before=history.TranslatePoint(new Point(),surface);var height=history.ActualHeight;
                    toast.BeginProgress(Strings.SavedLoadingCharacterNames);Layout(surface,size);
                    Check(history.TranslatePoint(new Point(),surface)==before && history.ActualHeight==height,"Floating toast never shifts or resizes the history content");
                    Check(toast.ActualWidth<=560 && toast.ActualHeight<160 && toast.ActualWidth>0 && text.ActualWidth>0,"Localized loading toast fits both supported window sizes");
                    Capture(surface,Path.Combine(directory,$"{language}-{size.Width}x{size.Height}-loading.png"),size);
                    toast.CompleteProgress(Strings.SavedCharacterNamesFailed,true);Layout(surface,size);
                    Check(toast.ActualHeight<160,"Localized failure messages wrap inside the toast");
                    if(language=="ja")Capture(surface,Path.Combine(directory,$"{language}-{size.Width}x{size.Height}-failed.png"),size);
                }
            }
        }
        finally {toast.CompleteProgress("");toast.Dismiss();LocalizationWpf.Apply("zh-Hans");}
        return count;
    }
    private static void Layout(FrameworkElement element,Size size)
    {element.Measure(size);element.Arrange(new Rect(size));element.UpdateLayout();}
    private static void Capture(FrameworkElement element,string path,Size size)
    {
        var bitmap=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);png.Save(output);
    }
}
