using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sf6Prototype;

internal static class UiCapture
{
    public static void SaveLater(FrameworkElement element, string name)
    {
        if (!App.Capture) return;
        element.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(App.DataDirectory,name)); png.Save(file);
        }),DispatcherPriority.ContextIdle);
    }
}
