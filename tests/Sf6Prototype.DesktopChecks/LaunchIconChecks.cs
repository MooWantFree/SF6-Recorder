using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sf6Prototype;

internal static class LaunchIconChecks
{
    public static int Run()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".local", "checks", Guid.NewGuid().ToString("N"))).FullName;
        var exe = Environment.ProcessPath!;
        var custom = Path.Combine(Environment.CurrentDirectory, "src", "Sf6Prototype", "Assets", "app.ico");
        var read = typeof(MainWindow).Assembly.GetType("Sf6Prototype.LaunchFileIcon")!.GetMethod("Read", BindingFlags.Static | BindingFlags.NonPublic)!;
        byte[] Pixels(string path)
        {
            var image = (BitmapSource?)read.Invoke(null, [path]) ?? throw new Exception("Could not load the launch icon: " + path);
            var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var stride = bitmap.PixelWidth * 4;
            var bytes = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(bytes, stride, 0);
            return bytes;
        }
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        string Link(string name, string? icon)
        {
            var path = Path.Combine(dir, name + ".lnk");
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = exe;
            if (icon != null) shortcut.IconLocation = icon + ",0";
            shortcut.Save();
            return path;
        }
        var original = Pixels(exe);
        var customIcon = Pixels(custom);
        if (original.SequenceEqual(customIcon)) throw new Exception("Icon fixtures must be visually distinct");
        if (!Pixels(Link("Default", null)).SequenceEqual(original)) throw new Exception("A shortcut must display its target icon without the arrow");
        if (!Pixels(Link("Custom", custom)).SequenceEqual(customIcon)) throw new Exception("A shortcut must preserve its custom icon without the arrow");
        return 2;
    }
}
