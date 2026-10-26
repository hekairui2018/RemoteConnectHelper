using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RemoteConnectHelper.V2.UI;
using RemoteConnectHelper.V2.Infrastructure;
namespace RemoteConnectHelper.V2.Diagnostics;
internal static class FilePickerTests
{
    private delegate bool EnumWindow(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumThreadWindows(uint thread,EnumWindow callback,IntPtr parameter);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Dispatcher.BeginInvoke(new Action(()=>{
            var owner=new Window{Title="灰Desk文件选择受控测试",Width=320,Height=420,Left=100,Top=100};owner.Show();
            var records=new List<object>();
            try
            {
                string folder=Path.Combine(Path.GetFullPath(output),"empty-images");Directory.CreateDirectory(folder);
                var prefs=new Preferences{LastImageFolder=folder};uint thread=GetCurrentThreadId();var guid=Guid.NewGuid();
                Rect observed=Rect.Empty;
                using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var timer=new System.Threading.Timer(_=>{EnumThreadWindows(thread,(window,_)=>{var name=new StringBuilder(256);GetClassName(window,name,256);if(IsWindowVisible(window)&&name.ToString()=="#32770"){observed=DesktopWindows.NativeBounds(window);Capture(window,Path.Combine(output,"首次文件窗口.png"));return false;}return true;},IntPtr.Zero);},null,800,Timeout.Infinite);
                string? result=ImageFilePicker.Show(owner,prefs,cancel.Token,guid);
                if(result!=null||observed.IsEmpty||prefs.ImagePickerBounds is not {Length:4})throw new Exception("Native picker did not show/cancel or retain bounds");
                var area=DesktopWindows.WorkAreaFor(owner);if(Math.Abs(observed.Left+(observed.Width/2)-area.Left-area.Width/2)>6||Math.Abs(observed.Top+(observed.Height/2)-area.Top-area.Height/2)>6)throw new Exception("First picker is not centered: "+observed);
                if(prefs.LastImageFolder!=folder)throw new Exception("Directory was not remembered on cancel");
                records.Add(new{Name="native first opening centers and cancel retains directory",Passed=true,Bounds=observed.ToString()});
                var saved=ImageFilePicker.Fit(area,observed,[area.Left+50,area.Top+60,observed.Width,observed.Height]);prefs.ImagePickerBounds=[saved.X,saved.Y,saved.Width,saved.Height];
                using var cancelAgain=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                result=ImageFilePicker.Show(owner,prefs,cancelAgain.Token,guid);
                var restored=prefs.ImagePickerBounds!;
                if(result!=null||Math.Abs(restored[0]-saved.X)>6||Math.Abs(restored[1]-saved.Y)>6)throw new Exception("Picker did not restore saved physical position");
                records.Add(new{Name="native reopening restores position and directory without selecting file",Passed=true,Bounds=restored});exit=0;
            }
            catch(Exception error){records.Add(new{Name="file picker integration",Passed=false,Error=error.ToString()});}
            finally{File.WriteAllText(Path.Combine(output,"file-picker-results.json"),JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));owner.Close();app.Shutdown();}
        }));app.Run();return exit;
    }
    private static void Capture(IntPtr window,string path)
    {
        var rect=DesktopWindows.NativeBounds(window);using var image=new System.Drawing.Bitmap((int)rect.Width,(int)rect.Height);
        using(var graphics=System.Drawing.Graphics.FromImage(image)){var dc=graphics.GetHdc();try{PrintWindow(window,dc,2);}finally{graphics.ReleaseHdc(dc);}}
        image.Save(path,System.Drawing.Imaging.ImageFormat.Png);
    }
}
