using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
namespace RemoteConnectHelper.V2.Diagnostics;

internal static class LiveInputProbe
{
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int command);
    internal static int Run(string client,string output)
    {
        Directory.CreateDirectory(output);
        var kind=client=="sunlogin"?RemoteAppKind.Sunlogin:client=="todesk"?RemoteAppKind.ToDesk:throw new ArgumentException("Unknown client");
        string[] processes=kind==RemoteAppKind.Sunlogin?["AweSun"]:["ToDesk"];
        string[] classes=kind==RemoteAppKind.Sunlogin?["FLUTTER_RUNNER_WIN32_WINDOW"]:["H-SMILE-FRAME"];
        string[] excluded=kind==RemoteAppKind.Sunlogin?["FlutterMultiWindow"]:[];
        var rows=new List<object>();bool passed=true;
        foreach(var mode in new[]{"existing","minimized","hidden"}){
            var existing=ScreenOps.FindWindow(processes,classes,excluded);
            if(existing!=null&&mode!="existing")ShowWindow(existing.Hwnd,mode=="minimized"?6:0);
            bool filled=false;string readback="not reached";
            var context=new ConnectionContext(kind){InputProbe=(window,x,y,px,py)=>{
                filled=true;
                readback=CoordOps.VerifyCode(window,"123456",x,y).ToString();
                ScreenOps.EnsureForeground(window);
                using var screenshot=ScreenOps.CaptureClient(window);
                screenshot.Save(Path.Combine(output,mode+"-private.png"),System.Drawing.Imaging.ImageFormat.Png);
                return ConnectResult.Ok("Input-only diagnostic; no connect click");
            }};
            context.Cts.CancelAfter(TimeSpan.FromSeconds(40));
            ConnectResult result;
            try{IRemoteConnector connector=kind==RemoteAppKind.Sunlogin?new SunloginConnector():new ToDeskConnector();
                result=connector.ConnectAsync("123456","123456",context).GetAwaiter().GetResult();}
            catch(Exception ex){result=ConnectResult.Fail(ex.GetType().Name+": "+ex.Message);}
            try{
                var current=ScreenOps.FindWindow(processes,classes,excluded);
                if(current!=null&&ScreenOps.EnsureForeground(current)){
                    using var screenshot=ScreenOps.CaptureClient(current);
                    screenshot.Save(Path.Combine(output,mode+"-final-private.png"),System.Drawing.Imaging.ImageFormat.Png);
                    var doc=ScreenOps.OcrAsync(screenshot).GetAwaiter().GetResult();
                    if(doc!=null)File.WriteAllText(Path.Combine(output,mode+"-ocr-private.json"),JsonSerializer.Serialize(doc));
                }
            }catch{ }
            bool ok=filled&&readback=="Matched"&&!context.ConnectClicked;
            passed&=ok;
            rows.Add(new{Mode=mode,Passed=ok,Filled=filled,Readback=readback,ConnectClicked=context.ConnectClicked,Status=result.Status.ToString(),result.Message,
                Password=kind==RemoteAppKind.Sunlogin?(filled?"123456 entered in inline field; screenshot requires review":"Not filled"):"No password field on main window; password is requested after connecting, not exercised"});
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Client=client,Passed=passed,Tests=rows},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(client+" "+mode+" filled="+filled+" readback="+readback+" clicked="+context.ConnectClicked);
            context.Cts.Dispose();
            if(!filled)break;
        }
        return passed?0:1;
    }
}
