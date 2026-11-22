using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Sf6Prototype;

// OBS stores the class of the top-level HWND with the source. This name is part of
// the capture interface and must stay the same across launches and releases.
internal sealed class OverlayWindowHost(RecentMatchesStreamWindow view) : Forms.NativeWindow
{
    // OBS Automatic capture selects WGC for classes containing "Mozilla", preserving per-pixel alpha.
    internal const string CaptureClass = "SF6Recorder.Mozilla.MatchOverlay";
    private const int Child = 0x40000000, Visible = 0x10000000, Popup = unchecked((int)0x80000000);
    private const int ThickFrame = 0x40000, SystemMenu = 0x80000, MinimizeBox = 0x20000;
    private const int AppWindow = 0x40000, Layered = 0x80000;
    private const uint NoSize = 1, NoMove = 2, NoZOrder = 4, NoActivate = 16;
    private const int HitClient = 1, HitCaption = 2, HitTransparent = -1;
    private static readonly nint Module = GetModuleHandle(null);
    private static readonly WindowProc DefaultProc = DefWindowProc;
    private static readonly System.Drawing.Icon? Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
    private static bool _registered;
    private HwndSource? _content;

    internal void Open(bool activate, System.Drawing.Rectangle? bounds, bool minimized)
    {
        if (!_registered)
        {
            var wc = new WindowClass { Name = CaptureClass, Instance = Module, Proc = DefaultProc,
                Cursor = LoadCursor(0, 32512), Icon = Icon?.Handle ?? 0 };
            if (RegisterClass(ref wc) == 0) throw new Win32Exception();
            _registered = true;
        }
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var hwnd = CreateWindowEx(AppWindow | (view.AllowsTransparency ? Layered : 0),
            CaptureClass, Strings.StreamWindowTitle, Popup | ThickFrame | SystemMenu | MinimizeBox,
            bounds?.Left ?? area.Left + area.Width / 2, bounds?.Top ?? area.Top + area.Height / 2,
            1, 1, 0, 0, Module, 0);
        if (hwnd == 0) throw new Win32Exception();
        AssignHandle(hwnd);
        try
        {
            var width = Pixels(RecentMatchesStreamWindow.DefaultWidth);
            var height = Pixels(RecentMatchesStreamWindow.DefaultHeight);
            var rect = bounds ?? new System.Drawing.Rectangle(area.Left + (area.Width-width)/2,
                area.Top + (area.Height-height)/2, width, height);
            SetWindowPos(hwnd, view.Topmost ? -1 : -2, rect.X, rect.Y, rect.Width, rect.Height, NoActivate);
            _content = new HwndSource(new HwndSourceParameters("Match overlay content", rect.Width, rect.Height) {
                WindowStyle = Child | Visible, ParentWindow = hwnd, UsesPerPixelTransparency = view.AllowsTransparency,
                HwndSourceHook = ChildMessage
            }) { SizeToContent = SizeToContent.Manual };
            if (!view.AllowsTransparency) _content.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            _content.RootVisual = view;
            ShowWindow(hwnd, minimized ? 7 : activate ? 5 : 4);
        }
        catch { Close(); throw; }
    }

    private int Pixels(double dip) => (int)Math.Round(dip * GetDpiForWindow(Handle) / 96);
    internal System.Drawing.Rectangle Bounds { get; private set; }
    internal bool Minimized => IsIconic(Handle);
    internal void Pin(bool pin) => SetWindowPos(Handle, pin ? -1 : -2, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
    internal void EnableInput() => EnableWindow(Handle, true);
    internal void PlaceBehind(nint window) => SetWindowPos(Handle, window, 0, 0, 0, 0, NoSize | NoMove | NoActivate | 0x200);
    internal void Drag() { ReleaseCapture(); SendMessage(Handle, 0x00A1, HitCaption, 0); }
    internal void Close() { if (Handle != 0) DestroyWindow(Handle); }

    private int HitTest(nint position)
    {
        int x = (short)position.ToInt64(), y = (short)(position.ToInt64() >> 16);
        int edge = Pixels(6);
        var left = x < Bounds.Left + edge; var right = x >= Bounds.Right - edge;
        if (y < Bounds.Top + edge) return left ? 13 : right ? 14 : 12;
        if (y >= Bounds.Bottom - edge) return left ? 16 : right ? 17 : 15;
        return left ? 10 : right ? 11 : HitClient;
    }

    private nint ChildMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Let the parent receive non-client messages at the resize border.
        if (message == 0x0084 && HitTest(lParam) != HitClient) { handled = true; return HitTransparent; }
        return 0;
    }

    protected override void WndProc(ref Forms.Message m)
    {
        switch (m.Msg)
        {
            case 0x0083: // WM_NCCALCSIZE: the entire frameless window is client area.
                m.Result = 0; return;
            case 0x0084: m.Result = HitTest(m.LParam); return;
            case 0x0024: // WM_GETMINMAXINFO
                var limits = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
                limits.MinTrackSize = new(Pixels(view.MinWidth), Pixels(view.MinHeight));
                Marshal.StructureToPtr(limits, m.LParam, false); return;
            case 0x02E0: // WM_DPICHANGED
                var r = Marshal.PtrToStructure<NativeRect>(m.LParam);
                SetWindowPos(Handle, 0, r.Left, r.Top, r.Right-r.Left, r.Bottom-r.Top, NoZOrder | NoActivate);
                return;
            case 0x0047: // WM_WINDOWPOSCHANGED
                if (!Minimized && GetWindowRect(Handle, out var rect))
                    Bounds = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                break;
            case 0x0005: // WM_SIZE
                if (!Minimized && GetClientRect(Handle, out var client) && client.Right > 0 && client.Bottom > 0)
                {
                    if (view.AllowsTransparency) ResizeLayeredSurface(client.Right, client.Bottom);
                    if (_content != null) SetWindowPos(_content.Handle, 0, 0, 0, client.Right, client.Bottom, NoZOrder | NoActivate);
                }
                break;
            case 0x0007: // WM_SETFOCUS
                if (_content != null) SetFocus(_content.Handle);
                break;
            case 0x0010: view.Close(); return;
            case 0x0002: _content?.Dispose(); _content = null; break;
        }
        base.WndProc(ref m);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style; public WindowProc Proc; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, Menu; public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    {
        public System.Drawing.Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern ushort RegisterClass(ref WindowClass wc);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern nint DefWindowProc(nint h, uint msg, nint w, nint l);
    [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern nint CreateWindowEx(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32")] private static extern bool DestroyWindow(nint h);
    private void ResizeLayeredSurface(int width, int height)
    {
        var size = new System.Drawing.Size(width, height);
        var origin = new System.Drawing.Point();
        // Alpha zero makes Windows pass mouse input to the app behind the overlay.
        // A one-alpha backing surface preserves dragging even at zero background opacity.
        var count = checked(width * height);
        var pixels = ArrayPool<int>.Shared.Rent(count);
        nint bitmap;
        try
        {
            Array.Fill(pixels, 0x01000000, 0, count);
            bitmap = CreateBitmap(width, height, 1, 32, pixels);
        }
        finally { ArrayPool<int>.Shared.Return(pixels); }
        if (bitmap == 0) throw new Win32Exception();
        var dc = CreateCompatibleDC(0);
        var previous = SelectObject(dc, bitmap);
        try
        {
            uint blend = 0x01FF0000;
            if (!UpdateLayeredWindow(Handle, 0, 0, ref size, dc, ref origin, 0, ref blend, 2))
                throw new Win32Exception();
        }
        finally { SelectObject(dc, previous); DeleteObject(bitmap); DeleteDC(dc); }
    }
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32", SetLastError=true)] private static extern nint CreateBitmap(int w,int h,uint planes,uint bits,int[] pixels);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc,nint obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32", SetLastError=true)] private static extern bool UpdateLayeredWindow(nint h,nint dest,nint pos,ref System.Drawing.Size size,nint src,ref System.Drawing.Point origin,uint key,ref uint blend,uint flags);
    [DllImport("user32")] private static extern uint GetDpiForWindow(nint h);
    [DllImport("user32")] private static extern bool GetWindowRect(nint h, out NativeRect r);
    [DllImport("user32")] private static extern bool GetClientRect(nint h, out NativeRect r);
    [DllImport("user32")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags);
    [DllImport("user32")] private static extern bool EnableWindow(nint h, bool enable);
    [DllImport("user32")] private static extern bool IsIconic(nint h);
    [DllImport("user32")] private static extern nint SetFocus(nint h);
    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern nint SendMessage(nint h, uint msg, nint w, nint l);
}
