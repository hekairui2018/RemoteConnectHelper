using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Connections;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>Fallback for windowed legacy editors with unavailable accessibility providers.</summary>
internal static class NativeLabelledConnector
{
    private delegate bool Callback(IntPtr h,IntPtr unused);
    [DllImport("user32.dll")]static extern bool EnumWindows(Callback callback,IntPtr data);
    [DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr root,Callback callback,IntPtr data);
    [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr h,int index);
    [DllImport("user32.dll")]static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint message,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr h,out Rect rect);
    [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr h,out Rect rect);
    [DllImport("user32.dll")]static extern bool ClientToScreen(IntPtr h,ref Point point);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern IntPtr SendMessageTimeout(IntPtr h,uint msg,IntPtr w,string text,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")]static extern IntPtr ReadTextTimeout(IntPtr h,uint msg,IntPtr w,StringBuilder text,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    [StructLayout(LayoutKind.Sequential)]struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}
    private sealed record Editor(IntPtr Handle,int X,int Y,int Width,int Height);
    public static ConnectResult? SubmitVerification(string path,string password,CancellationToken token)
    {
        if(password.Length==0)return null;
        var ids=new HashSet<int>();foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))using(p)try{if(string.Equals(p.MainModule?.FileName,path,StringComparison.OrdinalIgnoreCase))ids.Add(p.Id);}catch{}
        var dialogs=new List<IntPtr>();EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint pid);var title=new StringBuilder(128);GetWindowText(h,title,128);if(ids.Contains((int)pid)&&IsWindowVisible(h)&&Regex.IsMatch(title.ToString(),@"^(TeamViewer验证|TeamViewer Authentication)$",RegexOptions.IgnoreCase))dialogs.Add(h);return true;},IntPtr.Zero);
        if(dialogs.Count!=1)return null;
        var edits=new List<Editor>();var submit=new List<IntPtr>();
        EnumChildWindows(dialogs[0],(h,_)=>{
            if(!IsWindowVisible(h)||!IsWindowEnabled(h))return true;
            var cls=new StringBuilder(128);GetClassName(h,cls,128);
            if(cls.ToString()=="Edit"&&(GetWindowLong(h,-16)&0x20)!=0)edits.Add(new(h,0,0,0,0));
            if(cls.ToString()=="Button"&&GetDlgCtrlID(h)==1){var label=new StringBuilder(128);GetWindowText(h,label,128);if(Regex.IsMatch(label.ToString(),@"^(登录|Log on|OK)$",RegexOptions.IgnoreCase))submit.Add(h);}
            return true;
        },IntPtr.Zero);
        if(edits.Count!=1||submit.Count!=1)return ConnectResult.NeedPwd("TeamViewer密码弹窗控件不明确，请人工核对");
        token.ThrowIfCancellationRequested();
        if(SendMessageTimeout(edits[0].Handle,0x000c,IntPtr.Zero,password,2,1000,out var written)==IntPtr.Zero||written==IntPtr.Zero)return ConnectResult.NeedPwd("TeamViewer密码未可靠填入，请人工核对");
        token.ThrowIfCancellationRequested();PostMessage(submit[0],0x00f5,IntPtr.Zero,IntPtr.Zero);
        return ConnectResult.Ok("TeamViewer验证密码已提交，等待实际会话确认");
    }
    public static Task<ConnectResult> ConnectAsync(string path,string code,string password,CancellationToken token)=>Task.Run(async()=>{
        var ids=new HashSet<int>();
        foreach(var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))using(p)try{if(string.Equals(p.MainModule?.FileName,path,StringComparison.OrdinalIgnoreCase))ids.Add(p.Id);}catch{}
        var windows=new List<IntPtr>();EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint pid);if(ids.Contains((int)pid)&&IsWindowVisible(h))windows.Add(h);return true;},IntPtr.Zero);
        foreach(var window in windows){
            token.ThrowIfCancellationRequested();
            if(!GetClientRect(window,out var rect)||rect.Right<350||rect.Bottom<200)continue;
            var origin=new Point();ClientToScreen(window,ref origin);
            var win=new ScreenOps.Win{Hwnd=window,X=origin.X,Y=origin.Y,W=rect.Right,H=rect.Bottom};
            if(!ScreenOps.EnsureForeground(win))continue;
            using var image=ScreenOps.CaptureClient(win);var doc=await ScreenOps.OcrAsync(image);if(doc==null)continue;
            var status=NegativeClientStatus.FromText(doc.FlatText??"");if(status?.Status==ConnectStatus.Failed)return status;
            var lines=OcrGeo.BuildLines(doc);
            var labels=lines.Where(l=>Regex.IsMatch(l.Text,@"Partner\s*(?:ID|旧)|伙伴\s*ID|伙伴识别码|远程设备(?:ID|代码)|对方设备",RegexOptions.IgnoreCase)).ToArray();
            if(labels.Length!=1)continue;
            var label=labels[0];var words=label.Words;
            // A line may contain both local and partner labels. Match the partner substring geometrically.
            string flat=string.Concat(words.Select(w=>w.Text));var match=Regex.Match(flat,@"Partner(?:ID|旧)|伙伴ID|伙伴识别码|远程设备(?:ID|代码)|对方设备",RegexOptions.IgnoreCase);
            if(!match.Success)continue;
            var mapped=words.SelectMany(w=>w.Text.Select(_=>w)).Skip(match.Index).Take(match.Length).Distinct().ToArray();
            double labelRight=mapped.Max(w=>w.Right),labelY=mapped.Average(w=>w.CenterY);
            var editors=new List<Editor>();
            EnumChildWindows(window,(child,_)=>{
                var cls=new StringBuilder(256);GetClassName(child,cls,256);
                if(Regex.IsMatch(cls.ToString(),@"(?:^Edit$|\.EDIT\.|ThunderRT6TextBox)",RegexOptions.IgnoreCase)&&IsWindowVisible(child)&&IsWindowEnabled(child)&&GetWindowRect(child,out var b))
                    editors.Add(new(child,b.Left-origin.X,b.Top-origin.Y,b.Right-b.Left,b.Bottom-b.Top));return true;
            },IntPtr.Zero);
            var choices=editors.Where(e=>Math.Abs(e.Y+e.Height/2.0-labelY)<Math.Max(22,e.Height)&&e.X>=labelRight-8&&e.X-labelRight<220).ToArray();
            if(choices.Length!=1)continue;var codeEditor=choices[0];
            if(!Set(codeEditor,code,ids,token))return ConnectResult.NeedPwd("设备码输入回读不一致，已停止连接");
            if(password.Length>0){
                var pwdLabels=doc.Words.Where(w=>Regex.IsMatch(w.Text,@"^(?:Password|密码|验证码)$",RegexOptions.IgnoreCase)&&w.Y>labelY&&w.Y<labelY+130&&w.X>codeEditor.X-200).ToArray();
                var pwd=editors.Where(e=>e.Handle!=codeEditor.Handle&&Math.Abs(e.X-codeEditor.X)<35&&e.Y>codeEditor.Y&&e.Y-codeEditor.Y<100
                    &&pwdLabels.Any(l=>Math.Abs(l.CenterY-(e.Y+e.Height/2.0))<25&&l.Right<=e.X+8)).ToArray();
                if(pwd.Length!=1||!Set(pwd[0],password,ids,token))return ConnectResult.NeedPwd("设备码已填写，密码控件不明确，请在客户端核对");
            }
            var buttons=lines.Where(l=>l.Y>codeEditor.Y&&l.X1>codeEditor.X&&Regex.IsMatch(l.Text,@"Connecttopartner|连接远程计算机|连接远程电脑|^连接$",RegexOptions.IgnoreCase)).ToArray();
            if(buttons.Length!=1)return ConnectResult.NeedPwd("设备码和密码已填写，连接按钮不明确，请人工点击");
            token.ThrowIfCancellationRequested();if(!ScreenOps.EnsureForeground(win))return ConnectResult.NeedPwd("已填写，请在客户端点击连接");
            token.ThrowIfCancellationRequested();ScreenOps.ClickClient(win,(buttons[0].X0+buttons[0].X1)/2,buttons[0].Y);
            return ConnectResult.Ok("设备码和密码已回读核对，已发起连接，等待会话确认");
        }
        return ConnectResult.NeedPwd("未找到有明确标签的可写输入框，请在客户端核对");
    },token);
    private static bool Set(Editor editor,string value,HashSet<int> ids,CancellationToken token){
        token.ThrowIfCancellationRequested();GetWindowThreadProcessId(editor.Handle,out uint pid);if(!ids.Contains((int)pid)||!IsWindowEnabled(editor.Handle))return false;
        if(SendMessageTimeout(editor.Handle,0x000c,IntPtr.Zero,value,2,1000,out var result)==IntPtr.Zero||result==IntPtr.Zero)return false;
        var actual=new StringBuilder(256);return ReadTextTimeout(editor.Handle,0x000d,new IntPtr(actual.Capacity),actual,2,1000,out _)!=IntPtr.Zero&&actual.ToString()==value;
    }
}
