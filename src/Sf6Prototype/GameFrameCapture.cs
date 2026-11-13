using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sf6Prototype;

public readonly record struct Sf6Process(int Id,nint Window);

public static class Sf6GameProbe
{
    public static Sf6Process? Find()
    {
        Sf6Process? found=null;
        foreach(var process in Process.GetProcessesByName("StreetFighter6"))
        {
            using(process)
            {
                try
                {
                    var candidate=new Sf6Process(process.Id,process.MainWindowHandle);
                    if(found==null || candidate.Window!=0) found=candidate;
                }
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
            }
        }
        return found;
    }
}

// Reads only the top strip of the foreground game's client area, via the desktop
// compositor. No DLL injection, process memory access, input hooks or video encoding.
public sealed class GameFrameCapture : IDisposable
{
    private nint _dc,_bitmap,_previous,_pixels;
    public byte[] Pixels {get;}=new byte[MatchEndDetection.FrameWidth*MatchEndDetection.FrameHeight*4];
    public bool TryCapture(Sf6Process game,bool requireForeground=true)
    {
        if(game.Window==0 || !IsWindow(game.Window) || IsIconic(game.Window)) return false;
        GetWindowThreadProcessId(game.Window,out var windowPid);
        if(windowPid!=game.Id) return false;
        if(requireForeground)
        {
            GetWindowThreadProcessId(GetForegroundWindow(),out var foregroundPid);
            if(foregroundPid!=game.Id) return false;
        }
        var previousDpi=SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            if(!GetClientRect(game.Window,out var rect)) return false;
            var origin=new Point();
            if(!ClientToScreen(game.Window,ref origin)) return false;
            var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
            if(width<640 || height<360) return false;
            // SF6's standard playfield is 16:9, including letterboxed clients.
            var contentWidth=Math.Min(width,height*16/9);var contentHeight=contentWidth*9/16;
            var x=origin.X+(width-contentWidth)/2;var y=origin.Y+(height-contentHeight)/2;
            var sourceHeight=(int)Math.Round(contentHeight*MatchEndDetection.FrameHeight/360d);
            var screen=GetDC(0);
            if(screen==0) return false;
            try
            {
                if(_dc==0)
                {
                    _dc=CreateCompatibleDC(screen);
                    var info=new BitmapInfo {Size=40,Width=MatchEndDetection.FrameWidth,Height=-MatchEndDetection.FrameHeight,Planes=1,BitCount=32};
                    _bitmap=CreateDIBSection(screen,ref info,0,out _pixels,0,0);
                    if(_dc==0 || _bitmap==0) {Dispose();return false;}
                    _previous=SelectObject(_dc,_bitmap);
                    SetStretchBltMode(_dc,4); // HALFTONE, for stable scaling at different DPIs.
                }
                if(!StretchBlt(_dc,0,0,MatchEndDetection.FrameWidth,MatchEndDetection.FrameHeight,
                    screen,x,y,contentWidth,sourceHeight,0x00CC0020)) return false;
                GdiFlush();
                Marshal.Copy(_pixels,Pixels,0,Pixels.Length);
                return true;
            }
            finally {ReleaseDC(0,screen);}
        }
        finally {if(previousDpi!=0) SetThreadDpiAwarenessContext(previousDpi);}
    }
    public static bool IsForeground(Sf6Process game)
    {GetWindowThreadProcessId(GetForegroundWindow(),out var pid);return pid==game.Id && !IsIconic(game.Window);}
    public void Dispose()
    {
        if(_dc!=0 && _previous!=0) SelectObject(_dc,_previous);
        if(_bitmap!=0) DeleteObject(_bitmap);
        if(_dc!=0) DeleteDC(_dc);
        _dc=_bitmap=_previous=_pixels=0;
    }
    public static long CurrentThreadCpuTicks() => GetThreadTimes(new nint(-2),out _,out _,out var kernel,out var user) ? kernel+user : 0;
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,SizeImage;public int XPelsPerMeter,YPelsPerMeter;public uint ClrUsed,ClrImportant;}
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out int processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window,ref Point point);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc,int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint destination,int x,int y,int width,int height,nint source,int sourceX,int sourceY,int sourceWidth,int sourceHeight,uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(nint thread,out long created,out long exited,out long kernel,out long user);
}
