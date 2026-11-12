using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;

namespace Sf6Prototype;

public sealed class TrayIcon : IDisposable
{
    private readonly Window _window;
    private readonly Forms.NotifyIcon _tray;
    private readonly ModernTrayMenu _menu;
    private readonly HwndSource _source;
    private Drawing.Icon? _small,_large;
    private bool _disposed;
    public TrayIcon(Window window,Action show,Action exit)
    {
        _window=window;
        _source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _menu=new ModernTrayMenu(()=>window.Dispatcher.BeginInvoke(show),()=>window.Dispatcher.BeginInvoke(exit));
        _menu.Opening+=(_,_)=>_menu.ApplyDpi((uint)_menu.DeviceDpi);
        _tray=new Forms.NotifyIcon {Text=Strings.AppTitle,ContextMenuStrip=_menu};
        _tray.MouseDoubleClick+=(_,e)=>{if(e.Button==Forms.MouseButtons.Left) window.Dispatcher.BeginInvoke(show);};
        UpdateIcons(); _tray.Visible=true; _source.AddHook(WindowMessage);
    }
    public void RefreshLanguage() { _tray.Text=Strings.AppTitle; _menu.RefreshLanguage(); }
    private static Drawing.Icon LoadIcon(int size)
    {
        using var stream=Application.GetResourceStream(new Uri("pack://application:,,,/Sf6Prototype;component/Assets/app.ico")).Stream;
        return new Drawing.Icon(stream,size,size);
    }
    private void UpdateIcons()
    {
        if(_disposed) return;
        var dpi=GetDpiForWindow(_source.Handle);
        var small=LoadIcon(GetSystemMetricsForDpi(49,dpi)); // SM_CXSMICON
        var large=LoadIcon(GetSystemMetricsForDpi(11,dpi)); // SM_CXICON
        SendMessage(_source.Handle,0x0080,IntPtr.Zero,small.Handle);
        SendMessage(_source.Handle,0x0080,new IntPtr(1),large.Handle);
        _tray.Icon=small;
        _small?.Dispose(); _large?.Dispose(); _small=small; _large=large;
    }
    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x02E0) _window.Dispatcher.BeginInvoke(UpdateIcons); // WM_DPICHANGED
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        _source.RemoveHook(WindowMessage); _tray.Visible=false; _tray.Dispose(); _menu.Dispose(); _small?.Dispose(); _large?.Dispose();
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index,uint dpi);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
}
