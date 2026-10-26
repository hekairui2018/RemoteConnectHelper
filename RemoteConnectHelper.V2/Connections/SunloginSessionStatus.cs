using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;

namespace RemoteConnectHelper.V2.Connections;

/// <summary>Read only the local session toolbar; never OCR or inspect the remote desktop.</summary>
public static class SunloginSessionStatus
{
    private static readonly SemaphoreSlim OcrGate=new(1,1);
    private delegate bool Callback(IntPtr h,IntPtr unused);
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int L,T,R,B;}
    [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,IntPtr data);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr root,Callback callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out Rect rect);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr h);
    private static string Class(IntPtr h){var s=new StringBuilder(128);GetClassName(h,s,s.Capacity);return s.ToString();}
    private static string Title(IntPtr h){var s=new StringBuilder(128);GetWindowText(h,s,s.Capacity);return s.ToString();}
    public static bool Sufficient(bool renderer,bool matchingTarget,bool greenSignal,bool qualityLabel,bool pending)
        =>renderer&&matchingTarget&&greenSignal&&qualityLabel&&!pending;
    [ThreadStatic]public static string? LastReason;
    private static bool Reject(string reason){LastReason=reason;return false;}
    public static bool Read(IntPtr window,string requestedCode)
    {
        string cls=Class(window);if(cls is not ("FlutterMultiWindow" or "ORAY_FLUTTER_VIEW_CONTAINER"))return Reject("window class");
        GetWindowThreadProcessId(window,out uint pid);
        using(var process=Process.GetProcessById((int)pid))if(!process.ProcessName.Equals("AweSun",StringComparison.OrdinalIgnoreCase))return false;
        string code=Regex.Replace(Title(window),@"[\s-]","");
        if(!Regex.IsMatch(code,@"^\d{9,10}$")||requestedCode.Length>0&&requestedCode!=code)return Reject("window target");
        IntPtr toolbar=IntPtr.Zero,container=IntPtr.Zero;
        EnumWindows((h,_)=>{
            GetWindowThreadProcessId(h,out uint p);if(p!=pid||!IsWindowVisible(h)||Regex.Replace(Title(h),@"[\s-]","")!=code)return true;
            if(Class(h)=="FlutterMultiWindow")toolbar=h;
            if(Class(h)=="ORAY_FLUTTER_VIEW_CONTAINER")container=h;return true;
        },IntPtr.Zero);
        if(toolbar==IntPtr.Zero||container==IntPtr.Zero||!GetWindowRect(toolbar,out var bounds)||bounds.R-bounds.L<320||bounds.B-bounds.T<180)return Reject("paired renderer window");
        bool renderer=false;EnumChildWindows(container,(h,_)=>{if(Class(h)=="ORAY_FLUTTER_VIEW"&&IsWindowVisible(h))renderer=true;return true;},IntPtr.Zero);
        if(!renderer)return Reject("renderer child");
        int width=Math.Min(260,bounds.R-bounds.L),height=30;
        foreach(int dx in new[]{4,35,80,125,180,250})if(dx<width&&!ScreenOps.IsClickTargetUncovered(toolbar,bounds.L+dx,bounds.T+12)&&!ScreenOps.IsClickTargetUncovered(container,bounds.L+dx,bounds.T+12))return Reject("toolbar covered");
        using var strip=new Bitmap(width,height);
        using(var g=Graphics.FromImage(strip))g.CopyFromScreen(bounds.L,bounds.T,0,0,new Size(width,height));
        int green=0;for(int x=0;x<18;x++)for(int y=3;y<24;y++){var c=strip.GetPixel(x,y);if(c.G>105&&c.G>c.R+35&&c.G>c.B+15)green++;}
        if(green<5)return Reject("green signal absent");
        using var large=new Bitmap(width*3,height*3);using(var g=Graphics.FromImage(large)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(strip,0,0,large.Width,large.Height);}
        var doc=ScreenOps.OcrAsync(large).GetAwaiter().GetResult();if(doc==null)return Reject("toolbar OCR unavailable");
        string text=Regex.Replace(doc.FlatText??"",@"\s","");
        bool target=Regex.IsMatch(text,@"(?<!\d)"+Regex.Escape(code)+@"(?!\d)");
        bool quality=Regex.IsMatch(text,@"流畅|顺畅|高清|超清|原画");
        bool pending=Regex.IsMatch(text,@"正在连接|连接中|等待|验证|失败|断开|重连|离线");
        if(!quality&&OcrGate.Wait(0)){
            try{
                using var png=new MemoryStream();large.Save(png,System.Drawing.Imaging.ImageFormat.Png);
                var rich=NegativeClientStatus.StatusOcr.ReadAsync(png.ToArray(),0,false,CancellationToken.None).GetAwaiter().GetResult();
                string recognized=Regex.Replace(string.Concat(rich.Blocks.Select(b=>b.Text)),@"\s","");
                target|=Regex.IsMatch(recognized,@"(?<!\d)"+Regex.Escape(code)+@"(?!\d)");
                quality=Regex.IsMatch(recognized,@"流畅|顺畅|高清|超清|原画");
                pending|=Regex.IsMatch(recognized,@"正在连接|连接中|等待|验证|失败|断开|重连|离线");
            }catch(Exception ex){LastReason="toolbar OCR "+ex.GetType().Name;}
            finally{OcrGate.Release();}
        }
        LastReason=$"renderer={renderer};target={target};signal={green>=5};quality={quality};pending={pending}";
        return Sufficient(renderer,target,green>=5,quality,pending);
    }
}
