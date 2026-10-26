using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteConnectHelper.V2.UI;

public static class DesktopWindows
{
    public const int ExStyle = -20;
    public const long NoActivate = 0x08000000, ToolWindow = 0x80;
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetStyle(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo
    { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    public static long ExtendedStyle(Window window) => GetStyle(new WindowInteropHelper(window).EnsureHandle(),ExStyle).ToInt64();
    public static double DpiScale(Window window)=>Math.Max(96,GetDpiForWindow(new WindowInteropHelper(window).EnsureHandle()))/96d;
    public static void EndFrontPulse(Window window)
    {
        bool pinned=window is ConnectionCard;
        window.Topmost=pinned;
        var handle=new WindowInteropHelper(window).EnsureHandle();
        const uint flags=0x01|0x02|0x10; // Keep geometry and keyboard focus; change only normal-window Z order.
        SetWindowPos(handle,new IntPtr(pinned?-1:-2),0,0,0,0,flags);
    }
    public static void BeginFrontPulse(Window window)
    {
        // Briefly cover even other topmost windows, without activating or sending keyboard input.
        SetWindowPos(new WindowInteropHelper(window).EnsureHandle(),new IntPtr(-1),0,0,0,0,0x13);
    }
    public static Rect NativeBounds(IntPtr handle)
    {
        if(!GetWindowRect(handle,out var rect))return Rect.Empty;
        return new Rect(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top);
    }
    public static void MoveOnly(Window window,System.Windows.Point point)
    {
        var handle=new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(handle,IntPtr.Zero,(int)Math.Round(point.X),(int)Math.Round(point.Y),0,0,0x15);
    }
    public static Rect WorkAreaFor(Window window)
    {
        var rect=NativeBounds(new WindowInteropHelper(window).EnsureHandle());
        return WorkAreaAt(new NativePoint{X=(int)(rect.Left+rect.Width/2),Y=(int)(rect.Top+rect.Height/2)});
    }
    public static void ShowBallMenu(System.Windows.Forms.ContextMenuStrip menu,Window ball,double opacity)
    {
        // WinForms Point and WPF coordinates must not be mixed under DPI virtualization.
        var prior=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            menu.Close();menu.Opacity=Math.Clamp(opacity,.1,1);
            var anchor=NativeBounds(new WindowInteropHelper(ball).EnsureHandle());
            var area=WorkAreaFor(ball);var size=menu.GetPreferredSize(System.Drawing.Size.Empty);
            int x=(int)Math.Clamp(anchor.Left,area.Left,Math.Max(area.Left,area.Right-size.Width));
            int y=(int)(anchor.Bottom+4);
            if(y+size.Height>area.Bottom)y=(int)Math.Max(area.Top,anchor.Top-size.Height-4);
            menu.Show(new System.Drawing.Point(x,y));
            // Correct final native bounds after the menu has acquired its monitor DPI.
            var shown=NativeBounds(menu.Handle);
            x=(int)Math.Clamp(shown.Left,area.Left,Math.Max(area.Left,area.Right-shown.Width));
            y=(int)Math.Clamp(shown.Top,area.Top,Math.Max(area.Top,area.Bottom-shown.Height));
            SetWindowPos(menu.Handle,IntPtr.Zero,x,y,0,0,0x15);
        }
        finally{if(prior!=IntPtr.Zero)SetThreadDpiAwarenessContext(prior);}
    }
    public static void KeepSubmenusOnMonitor(System.Windows.Forms.ContextMenuStrip root)
    {
        void Attach(System.Windows.Forms.ToolStripItemCollection items)
        {
            foreach(var item in items.OfType<System.Windows.Forms.ToolStripMenuItem>().Where(i=>i.HasDropDownItems))
            {
                item.DropDownOpening+=(_,_)=>{
                    var owner=NativeBounds(root.Handle);var area=WorkAreaAtPoint(owner.Left+owner.Width/2,owner.Top+owner.Height/2);
                    var parent=NativeBounds(item.Owner!.Handle);var size=item.DropDown.GetPreferredSize(System.Drawing.Size.Empty);
                    item.DropDownDirection=parent.Right+size.Width>area.Right?System.Windows.Forms.ToolStripDropDownDirection.Left:System.Windows.Forms.ToolStripDropDownDirection.Right;
                    item.DropDown.Opacity=root.Opacity;
                };
                item.DropDownOpened+=(_,_)=>{
                    var owner=NativeBounds(root.Handle);var area=WorkAreaAtPoint(owner.Left+owner.Width/2,owner.Top+owner.Height/2);
                    var shown=NativeBounds(item.DropDown.Handle);
                    int x=(int)Math.Clamp(shown.Left,area.Left,Math.Max(area.Left,area.Right-shown.Width));
                    int y=(int)Math.Clamp(shown.Top,area.Top,Math.Max(area.Top,area.Bottom-shown.Height));
                    SetWindowPos(item.DropDown.Handle,IntPtr.Zero,x,y,0,0,0x15);
                };
                Attach(item.DropDownItems);
            }
        }
        Attach(root.Items);
    }
    public static void Passive(Window window, bool passive)
    {
        IntPtr handle=new WindowInteropHelper(window).EnsureHandle();
        long style=GetStyle(handle,ExStyle).ToInt64() | ToolWindow;
        style=passive ? style | NoActivate : style & ~NoActivate;
        SetStyle(handle,ExStyle,new IntPtr(style));
        window.ShowActivated=false;
    }
    public static System.Windows.Point CursorPosition()
    {
        if(!GetCursorPos(out var point))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new System.Windows.Point(point.X,point.Y);
    }
    public static Rect WorkAreaAtPointer()
    {
        GetCursorPos(out var point);
        return WorkAreaAt(point);
    }
    private static Rect WorkAreaAt(NativePoint point)
    {
        var monitor=MonitorFromPoint(point,2);
        var info=new MonitorInfo {Size=Marshal.SizeOf<MonitorInfo>()};
        if(GetMonitorInfo(monitor,ref info)) return new Rect(info.Work.Left,info.Work.Top,info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top);
        return new Rect(0,0,1920,1080);
    }
    public static Rect WorkAreaAtPoint(double x,double y)=>WorkAreaAt(new NativePoint{X=(int)x,Y=(int)y});
    public static Rect FitBeside(Rect area,Rect anchor,double widthDip,double heightDip,double scale)
    {
        var rect=Fit(area,widthDip,heightDip,scale);double gap=8*scale;
        double x=anchor.Right+gap;
        if(x+rect.Width>area.Right)x=anchor.Left-gap-rect.Width;
        return new Rect(Math.Clamp(x,area.Left,Math.Max(area.Left,area.Right-rect.Width)),
            Math.Clamp(anchor.Top,area.Top,Math.Max(area.Top,area.Bottom-rect.Height)),rect.Width,rect.Height);
    }
    public static Rect Fit(Rect area,double widthDip,double heightDip,double dpiScale)
    {
        double gap=Math.Min(16,Math.Min(area.Width,area.Height)/8);
        double w=Math.Min(widthDip*dpiScale,Math.Max(1,area.Width-2*gap));
        double h=Math.Min(heightDip*dpiScale,Math.Max(1,area.Height-2*gap));
        return new Rect(area.Right-w-gap,area.Bottom-h-gap,w,h);
    }
    public static void Place(Window window,Rect area,Rect? beside=null,System.Windows.Point? preferred=null)
    {
        var handle=new WindowInteropHelper(window).EnsureHandle();
        // A monitor hop is needed only for explicit placement on a different monitor, never for each drag step.
        var target=MonitorFromPoint(new NativePoint{X=(int)(area.X+area.Width/2),Y=(int)(area.Y+area.Height/2)},2);
        if(MonitorFromWindow(handle,2)!=target)
            SetWindowPos(handle,IntPtr.Zero,(int)area.X+8,(int)area.Y+8,0,0,0x15);
        double scale=Math.Max(1,GetDpiForWindow(handle))/96d;
        var rect=beside.HasValue?FitBeside(area,beside.Value,window.Width,window.Height,scale):Fit(area,window.Width,window.Height,scale);
        if(preferred is {} point)rect=new Rect(Math.Clamp(point.X,area.Left,Math.Max(area.Left,area.Right-rect.Width)),Math.Clamp(point.Y,area.Top,Math.Max(area.Top,area.Bottom-rect.Height)),rect.Width,rect.Height);
        window.MaxWidth=(area.Width-16)/scale;window.MaxHeight=(area.Height-16)/scale;
        SetWindowPos(handle,IntPtr.Zero,(int)rect.X,(int)rect.Y,(int)rect.Width,(int)rect.Height,0x14);
    }
}
