using System;
using System.Runtime.InteropServices;
using System.Drawing;
public static class InstallerCapture
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    private struct Rect { public int L,T,R,B; }
    public static void SaveWindow(IntPtr hwnd, string path)
    {
        Rect r;
        if (!GetWindowRect(hwnd,out r) || r.R<=r.L) throw new Exception("No installer window");
        using(var bitmap=new Bitmap(r.R-r.L,r.B-r.T)) {
            using(var graphics=Graphics.FromImage(bitmap)) {
                var dc=graphics.GetHdc();
                try { if(!PrintWindow(hwnd,dc,2)) throw new Exception("Installer capture failed"); }
                finally { graphics.ReleaseHdc(dc); }
            }
            bitmap.Save(path);
        }
    }
    public static void SaveIcon(string executable,string path)
    {
        using(var icon=Icon.ExtractAssociatedIcon(executable))
        using(var bitmap=icon.ToBitmap()) bitmap.Save(path);
    }
}
