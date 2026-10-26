using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;
namespace RemoteConnectHelper.V2.Infrastructure;

public static class AdditionalClientConnector
{
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr window,int state);
    private static bool ClickNavigation(AutomationElement root,AutomationElement entry,CancellationToken token){
        var handle=new IntPtr(root.Current.NativeWindowHandle);if(handle==IntPtr.Zero)return false;
        ShowWindow(handle,9);InputSimulator.BringToFront(handle);token.ThrowIfCancellationRequested();
        var row=TreeWalker.ControlViewWalker.GetParent(entry);if(row?.Current.AutomationId!="DevicesSubView.CommonSubLeafItem")return false;
        var bounds=row.Current.BoundingRectangle;var client=root.Current.BoundingRectangle;
        if(GetForegroundWindow()!=handle||bounds.IsEmpty||!client.Contains(bounds)||entry.Current.Name!="开始协助")return false;
        ScreenOps.ClickVerifiedScreen(handle,(int)(bounds.X+Math.Min(30,bounds.Width/2)),(int)(bounds.Y+bounds.Height/2),120);return true;
    }
    public static ProcessStartInfo AnyDeskStart(string path,string code,bool hasPassword)
    {
        if(!ConnectionPolicy.ValidCode(RemoteApp.AnyDesk,code))throw new ArgumentException("设备码无效");
        var start=new ProcessStartInfo(path){UseShellExecute=false,RedirectStandardInput=hasPassword,WorkingDirectory=Path.GetDirectoryName(path)!};
        start.ArgumentList.Add(code);if(hasPassword)start.ArgumentList.Add("--with-password");return start;
    }
    public static ProcessStartInfo RustDeskStart(string path,string code,string password)
    {
        if(!ConnectionPolicy.ValidCode(RemoteApp.RustDesk,code))throw new ArgumentException("RustDesk设备ID无效");
        var start=new ProcessStartInfo(path){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)!};
        start.ArgumentList.Add("--connect");start.ArgumentList.Add(code);
        if(password.Length>0){start.ArgumentList.Add("--password");start.ArgumentList.Add(password);}return start;
    }
    public static async Task<ConnectResult> ConnectAsync(RemoteApp app,string path,string code,string password,CancellationToken token)
    {
        if(password.Any(c=>c is '\r' or '\n'))return ConnectResult.NeedPwd("密码含换行，请人工核对");
        token.ThrowIfCancellationRequested();
        if(app==RemoteApp.AnyDesk){
            using var process=Process.Start(AnyDeskStart(path,code,password.Length>0));
            if(process==null)return ConnectResult.Fail("AnyDesk未能打开");
            if(password.Length>0){await process.StandardInput.WriteLineAsync(password.AsMemory(),token);process.StandardInput.Close();}
            return ConnectResult.Ok("已向AnyDesk发起连接，正在等待实际会话确认");
        }
        if(app==RemoteApp.RustDesk){
            using var process=Process.Start(RustDeskStart(path,code,password));
            return process==null?ConnectResult.Fail("RustDesk未能打开"):ConnectResult.Ok("已向RustDesk发起连接，等待实际会话确认");
        }
        if(app==RemoteApp.ToDesk){
            var start=new ProcessStartInfo(path){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(path)!};
            start.ArgumentList.Add("ToDesk://connect&"+code+"&"+Uri.EscapeDataString(password));
            using var process=Process.Start(start);
            return process==null?ConnectResult.Fail("ToDesk未能打开"):ConnectResult.Ok("已通过ToDesk官方连接入口发起连接，等待实际会话确认");
        }
        if(app==RemoteApp.TeamViewer)return await AdaptiveClientConnector.ConnectAsync(path,code,password,token);
        if(app==RemoteApp.UltraViewer)return await AdaptiveClientConnector.ConnectAsync(path,code,password,token);
        if(app!=RemoteApp.UU)return ConnectResult.Fail("此软件尚未适配");
        using(var process=Process.Start(new ProcessStartInfo(path){UseShellExecute=true})){}
        return await Task.Run(async()=>{
            AutomationElement? root=null,remote=null;
            for(int round=0;round<15&&remote==null;round++){
                token.ThrowIfCancellationRequested();
                foreach(var window in SessionObserver.Windows(app)){
                    try{var candidate=AutomationElement.FromHandle(window);remote=candidate.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"qt_scrollarea_viewport.RemoteAssistContent.remoteWidget"));
                        if(remote==null){var entry=candidate.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.NameProperty,"开始协助"),new PropertyCondition(AutomationElement.AutomationIdProperty,"DevicesSubView.CommonSubLeafItem.textLabel")));token.ThrowIfCancellationRequested();if(entry!=null)ClickNavigation(candidate,entry,token);}if(remote!=null){root=candidate;break;}}catch(Exception ex){Log.Write("UU导航异常 "+ex.GetType().Name);}
                }
                if(remote==null)await Task.Delay(300,token);
            }
            if(root==null||remote==null)return ConnectResult.NeedPwd("请在UU远程打开“远程协助→开始协助”后重试");
            token.ThrowIfCancellationRequested();var fields=Nodes(remote).Where(n=>n.Current.ControlType==ControlType.Edit&&!n.Current.IsPassword&&!n.Current.IsOffscreen).ToArray();
            if(fields.Length!=1||!SetValue(fields[0],code))return ConnectResult.NeedPwd("未能准确定位UU远程的伙伴设备ID，请人工填写");
            await Task.Delay(200,token);token.ThrowIfCancellationRequested();
            var connect=Nodes(remote).SingleOrDefault(n=>n.Current.ControlType==ControlType.Button&&n.Current.Name.Trim()=="连接"&&n.Current.IsEnabled);
            if(connect==null||!Invoke(connect))return ConnectResult.NeedPwd("UU远程设备ID已填写，请检查后点击连接");
            await Task.Delay(250,token);
            foreach(var window in SessionObserver.Windows(app)){try{var text=string.Join("\n",Nodes(AutomationElement.FromHandle(window)).Select(n=>n.Current.Name));var failure=NegativeClientStatus.FromText(text);if(failure?.Status==ConnectStatus.Failed)return failure;}catch(ElementNotAvailableException){}}
            if(password.Length==0)return ConnectResult.NeedPwd("UU远程已发起连接，请在客户端输入验证码；成功后助手自动收起");
            for(int round=0;round<30;round++){
                await Task.Delay(300,token);
                foreach(var window in SessionObserver.Windows(app)){
                    try{
                        var dialog=AutomationElement.FromHandle(window);var nodes=Nodes(dialog);string text=string.Join("\n",nodes.Select(n=>n.Current.Name));
                        if(Regex.IsMatch(text,@"验证码错误|密码错误|无法对同账号|设备不在线|连接失败"))return ConnectResult.NeedPwd("UU远程未连接成功，请检查验证码及设备状态");
                        var passwords=nodes.Where(n=>n.Current.ControlType==ControlType.Edit&&!n.Current.IsOffscreen&&n.Current.IsPassword).ToArray();
                        if(passwords.Length==0&&Regex.IsMatch(text,@"请输入.{0,8}(验证码|密码)"))passwords=nodes.Where(n=>n.Current.ControlType==ControlType.Edit&&!n.Current.IsOffscreen&&n.Current.BoundingRectangle.Width>0).ToArray();
                        if(passwords.Length!=1)continue;
                        token.ThrowIfCancellationRequested();if(!SetValue(passwords[0],password))return ConnectResult.NeedPwd("请在UU远程输入验证码，确认成功后助手自动收起");
                        var submit=nodes.Where(n=>n.Current.ControlType==ControlType.Button&&n.Current.IsEnabled&&Regex.IsMatch(n.Current.Name.Trim(),@"^(确定|确认|连接|立即连接|开始远控)$")).ToArray();
                        token.ThrowIfCancellationRequested();
                        if(submit.Length==1&&Invoke(submit[0]))return ConnectResult.Ok("已提交UU远程验证码，等待实际会话确认");
                        return ConnectResult.NeedPwd("验证码已填入UU远程，请在客户端确认连接");
                    }catch(ElementNotAvailableException){}
                }
            }
            return ConnectResult.Ok("UU远程已发起连接，尚未确认实际会话");
        },token);
    }
    internal static List<AutomationElement> Nodes(AutomationElement root)
    {
        var nodes=new List<AutomationElement>();var queue=new Queue<(AutomationElement,int)>();queue.Enqueue((root,0));var clock=Stopwatch.StartNew();
        while(queue.Count>0&&nodes.Count<220&&clock.ElapsedMilliseconds<600){var(n,depth)=queue.Dequeue();nodes.Add(n);if(depth>=16)continue;var walker=TreeWalker.ControlViewWalker;var child=walker.GetFirstChild(n);for(int count=0;child!=null&&count<40;count++,child=walker.GetNextSibling(child))queue.Enqueue((child,depth+1));}
        return nodes;
    }
    private static bool SetValue(AutomationElement element,string value){
        try{if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)&&pattern is ValuePattern p&&!p.Current.IsReadOnly){p.SetValue(value);return element.Current.IsPassword||Regex.Replace(p.Current.Value,@"\s","")==value;}}catch{}return false;
    }
    private static bool Invoke(AutomationElement element){try{if(element.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern)&&pattern is InvokePattern p){p.Invoke();return true;}}catch{}return false;}
}
