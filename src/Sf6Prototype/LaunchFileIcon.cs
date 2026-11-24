using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Sf6Prototype;

internal static class LaunchFileIcon
{
    // Use the shell's base image, including a shortcut's custom icon, without Explorer overlays.
    internal static BitmapSource? Read(string path)
    {
        var initialized = CoInitializeEx(0, 0);
        try
        {
            const uint ShgfiSysIconIndex = 0x4000;
            var list = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiSysIconIndex);
            if (list == 0) return null;
            var icon = ImageList_GetIcon(list, info.IconIndex, 0);
            if (icon == 0) return null;
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally { DestroyIcon(icon); }
        }
        finally { if (initialized >= 0) CoUninitialize(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint ImageList_GetIcon(nint list, int index, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
