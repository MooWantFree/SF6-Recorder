using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Sf6Prototype;

public partial class MainWindow
{
    public static readonly DependencyProperty IsMaximizeHotProperty=DependencyProperty.Register(nameof(IsMaximizeHot),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool IsMaximizeHot { get=>(bool)GetValue(IsMaximizeHotProperty); private set=>SetValue(IsMaximizeHotProperty,value); }
    public static readonly DependencyProperty IsMaximizePressedProperty=DependencyProperty.Register(nameof(IsMaximizePressed),typeof(bool),typeof(MainWindow),new PropertyMetadata(false));
    public bool IsMaximizePressed { get=>(bool)GetValue(IsMaximizePressedProperty); private set=>SetValue(IsMaximizePressedProperty,value); }
    private HwndSource? _chromeSource;
    private bool _captionCaptured;
    private const int HtMaxButton=9, WmNcHitTest=0x84, WmNcMouseMove=0xA0, WmNcLeftDown=0xA1,
        WmNcLeftDoubleClick=0xA3, WmMouseMove=0x200, WmLeftUp=0x202, WmCaptureChanged=0x215,
        WmCancelMode=0x1F, WmNcMouseLeave=0x2A2;

    private void InitializeWindowChrome()
    {
        SourceInitialized+=(_,_)=>
        {
            _chromeSource=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _chromeSource?.AddHook(ChromeWindowProc);
            // WindowChrome owns resizing and the caption drag area; DWM owns the outer corners.
            if(OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))
            {
                var rounded=2;
                DwmSetWindowAttribute(_chromeSource!.Handle,33,ref rounded,sizeof(int));
            }
        };
        Closed+=(_,_)=>{_chromeSource?.RemoveHook(ChromeWindowProc);_chromeSource=null;};
        Deactivated+=(_,_)=>CancelCaptionPress();
        StateChanged+=(_,_)=>UpdateMaximizedInsets();
        SizeChanged+=(_,_)=>UpdateMaximizedInsets();
        DpiChanged+=(_,_)=>Dispatcher.BeginInvoke(UpdateMaximizedInsets);
    }

    private void UpdateMaximizedInsets()
    {
        var inset=new Thickness();
        if(WindowState==WindowState.Maximized && _chromeSource!=null)
        {
            var hwnd=_chromeSource.Handle;
            var monitor=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(MonitorFromWindow(hwnd,2),ref monitor) && GetClientRect(hwnd,out var client))
            {
                var origin=new NativePoint();ClientToScreen(hwnd,ref origin);
                var dpi=VisualTreeHelper.GetDpi(this);
                // Maximized Win32 frames extend beyond the work area. Keep WPF content inside it,
                // using the actual monitor/DPI rather than a fixed border or primary-screen size.
                inset=new Thickness(Math.Max(0,monitor.Work.Left-origin.X)/dpi.DpiScaleX,
                    Math.Max(0,monitor.Work.Top-origin.Y)/dpi.DpiScaleY,
                    Math.Max(0,origin.X+client.Right-monitor.Work.Right)/dpi.DpiScaleX,
                    Math.Max(0,origin.Y+client.Bottom-monitor.Work.Bottom)/dpi.DpiScaleY);
            }
        }
        WindowSurface.Margin=inset;
        WindowChrome.GetWindowChrome(this).CaptionHeight=TitleBar.Height+inset.Top;
    }

    private void Minimize_Click(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender,RoutedEventArgs e)=>ToggleMaximize();
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void ToggleMaximize()
    {
        if(WindowState==WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private bool IsOverMaximize(NativePoint screen)
    {
        if(!IsEnabled || !MaximizeButton.IsEnabled || !MaximizeButton.IsVisible) return false;
        // PointFromScreen handles monitor offsets (including negative ones) and per-monitor DPI.
        var point=MaximizeButton.PointFromScreen(new Point(screen.X,screen.Y));
        return new Rect(MaximizeButton.RenderSize).Contains(point);
    }

    private static NativePoint MessagePoint(IntPtr value)=>new()
        {X=unchecked((short)value.ToInt64()),Y=unchecked((short)(value.ToInt64()>>16))};

    private void CancelCaptionPress()
    {
        var captured=_captionCaptured;
        _captionCaptured=false;IsMaximizeHot=false;IsMaximizePressed=false;
        if(captured && _chromeSource!=null && GetCapture()==_chromeSource.Handle) ReleaseCapture();
    }

    private IntPtr ChromeWindowProc(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        switch(message)
        {
            case WmNcHitTest:
                // Windows 11 recognizes this native hit target and supplies its Snap Layouts flyout.
                if(IsOverMaximize(MessagePoint(lParam))) {handled=true;return new IntPtr(HtMaxButton);}
                break;
            case WmNcMouseMove:
                var hot=wParam.ToInt64()==HtMaxButton && IsOverMaximize(MessagePoint(lParam));
                if(hot && !IsMaximizeHot)
                {
                    var tracking=new TrackMouse {Size=(uint)Marshal.SizeOf<TrackMouse>(),Flags=0x12,Window=hwnd};
                    TrackMouseEvent(ref tracking); // TME_LEAVE | TME_NONCLIENT
                }
                IsMaximizeHot=hot;
                break; // Let the OS observe hover for Snap Layouts.
            case WmNcMouseLeave:
                if(!_captionCaptured) IsMaximizeHot=false;
                break;
            case WmNcLeftDown:
            case WmNcLeftDoubleClick:
                if(wParam.ToInt64()==HtMaxButton && IsOverMaximize(MessagePoint(lParam)))
                {
                    _captionCaptured=true;IsMaximizeHot=true;IsMaximizePressed=true;
                    SetCapture(hwnd);handled=true;
                }
                break;
            case WmMouseMove:
                if(_captionCaptured)
                {
                    var point=MessagePoint(lParam);ClientToScreen(hwnd,ref point);
                    IsMaximizeHot=IsMaximizePressed=IsOverMaximize(point);handled=true;
                }
                else IsMaximizeHot=false;
                break;
            case WmLeftUp:
                if(_captionCaptured)
                {
                    var point=MessagePoint(lParam);ClientToScreen(hwnd,ref point);
                    var activate=IsOverMaximize(point);
                    CancelCaptionPress();handled=true;
                    if(activate) ToggleMaximize();
                }
                break;
            case WmCaptureChanged:
            case WmCancelMode:
                CancelCaptionPress();
                break;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouse {public uint Size,Flags;public IntPtr Window;public uint HoverTime;}
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TrackMouse tracking);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}
