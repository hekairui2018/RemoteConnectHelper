using System.Runtime.InteropServices;
using System.Text;
using RemoteConnectHelper.V2.Ocr;
using RemoteConnectHelper.V2.Infrastructure;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;
using SD=System.Drawing;
namespace RemoteConnectHelper.V2.Connections;

/// <summary>OCR may report an explicit error or request, never establish connection success.</summary>
public static class NegativeClientStatus
{
    [StructLayout(LayoutKind.Sequential)]private struct Bounds {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr window,out Bounds bounds);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder name,int count);
    private static readonly SemaphoreSlim ReadGate=new(1,1);
    private static readonly Lazy<IsolatedRapidOcrProvider> Engine=new(()=>new());
    internal static IsolatedRapidOcrProvider StatusOcr=>Engine.Value;
    public static ConnectResult? FromText(string text)
    {
        text=Regex.Replace(text,@"\s+","");
        if(Regex.IsMatch(text,@"需要创建帐户|需要创建账户|连接均需使用通过验证的TeamViewer|signinto.*TeamViewer",RegexOptions.IgnoreCase))return ConnectResult.NeedPwd("TeamViewer要求登录已验证账号，请先在客户端登录");
        if(Regex.IsMatch(text,@"CannotstartUltraViewerController|CannotstartUltraViewer|err5\(5\)",RegexOptions.IgnoreCase))return ConnectResult.Fail("UltraViewer控制模块启动失败，请检查客户端安装或权限");
        if(Regex.IsMatch(text,@"识别码不存在|设备码不存在|设备代码不存在|識別碼不存在|识别码不在线|设备不在线|设备离线|对方不在线|targetdeviceisoffline",RegexOptions.IgnoreCase))return ConnectResult.Fail("识别码不存在或目标设备不在线，请核对设备码并确认对方已打开远程软件");
        if(Regex.IsMatch(text,@"验证码错误|密码错误|密码不正确|验证码不正确|incorrectpassword",RegexOptions.IgnoreCase))return ConnectResult.Fail("验证码或密码错误，请核对后重试");
        if(Regex.IsMatch(text,@"连接被拒绝|拒绝连接|对方已拒绝|连接已取消",RegexOptions.IgnoreCase))return ConnectResult.Fail("对方拒绝或取消了本次连接");
        if(Regex.IsMatch(text,@"不能连接本机|不能连接自己|无法连接本机|无法对同账号|连接错误|连接失败|设备不在线|设备离线|对方不在线|验证码错误|密码错误|密码不正确|验证码不正确|Failedtoconnect|targetdeviceisoffline|connectionfailed|incorrectpassword",RegexOptions.IgnoreCase))return ConnectResult.Fail("远程软件报告连接未成功，请检查目标设备和验证码");
        if(Regex.IsMatch(text,@"请等待对方接受|等待对方同意|等待授权|waitingforapproval",RegexOptions.IgnoreCase))return ConnectResult.NeedPwd("等待对方接受或授权，尚未连接成功");
        if(Regex.IsMatch(text,@"请输入.{0,8}(密码|验证码)|输入连接密码|passwordrequired|enteryourpartner.spassword",RegexOptions.IgnoreCase))return ConnectResult.NeedPwd("等待输入密码或验证码，请在客户端完成验证");
        return null;
    }
    internal static async Task<ConnectResult?> ReadBitmapAsync(SD.Bitmap bitmap,CancellationToken token)
    {
        try{
            using var stream=new MemoryStream();bitmap.Save(stream,SD.Imaging.ImageFormat.Png);
            var document=await StatusOcr.ReadAsync(stream.ToArray(),0,false,token);
            var result=FromText(string.Concat(document.Blocks.Select(b=>b.Text)));
            if(result!=null){Log.Write("连接状态回读 source=PP-OCR status="+result.Status);return result;}
        }catch(OperationCanceledException){throw;}
        catch(Exception ex){Log.Write("连接状态PP-OCR不可用 "+ex.GetType().Name);}
        var fallback=await ScreenOps.OcrAsync(bitmap);token.ThrowIfCancellationRequested();
        return fallback==null?null:FromText(string.Join("\n",fallback.Words.Select(w=>w.Text)));
    }
    public static async Task<ConnectResult?> ReadAsync(IntPtr window,CancellationToken token)
    {
        if(!await ReadGate.WaitAsync(0,token))return null;
        try{
            token.ThrowIfCancellationRequested();
            if(!IsWindowVisible(window)||IsIconic(window)||!GetWindowRect(window,out var rect))return null;
            var name=new StringBuilder(128);GetClassName(window,name,name.Capacity);
            // The paired remote desktop renderer is never a source for local failure/password OCR.
            if(name.ToString()=="ORAY_FLUTTER_VIEW_CONTAINER")return null;
            int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
            if(width<50||height<40||(long)width*height>12_000_000)return null;
            int cropWidth=Math.Min(width,960),cropHeight=Math.Min(height,540);
            int left=rect.Left+(width-cropWidth)/2,top=rect.Top+(height-cropHeight)/2;
            foreach(var point in new[]{(.1,.1),(.9,.1),(.5,.5),(.1,.9),(.9,.9)})
                if(!ScreenOps.IsClickTargetUncovered(window,left+(int)(cropWidth*point.Item1),top+(int)(cropHeight*point.Item2)))return null;
            for(int y=16;y<cropHeight;y+=64)for(int x=16;x<cropWidth;x+=64)
                if(!ScreenOps.IsClickTargetUncovered(window,left+x,top+y))return null;
            using var bitmap=new SD.Bitmap(cropWidth,cropHeight,SD.Imaging.PixelFormat.Format32bppArgb);
            using(var graphics=SD.Graphics.FromImage(bitmap))graphics.CopyFromScreen(left,top,0,0,new SD.Size(cropWidth,cropHeight));
            return await ReadBitmapAsync(bitmap,token);
        }finally{ReadGate.Release();}
    }
}
