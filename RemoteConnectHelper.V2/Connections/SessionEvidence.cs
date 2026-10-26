using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Connections;

public enum SessionState { Initiated, Connecting, Confirmed, Verification, Failed, Uncertain }
public sealed record SessionSample(long Window, bool NewWindow, bool TargetMatches, bool ExplicitConnected,
    bool SessionControl, bool Verification, bool Failure, bool Loading, string Reason);

/// <summary>Absence of an error or a window alone never establishes a remote session.</summary>
public sealed class SessionEvidence
{
    private long _last; private int _stable;
    public SessionState Observe(SessionSample s)
    {
        if(s.Failure){_stable=0;return SessionState.Failed;}
        if(s.Verification){_stable=0;return SessionState.Verification;}
        bool proven=s.NewWindow && s.TargetMatches && s.ExplicitConnected && s.SessionControl && !s.Loading;
        if(!proven){_stable=0;_last=0;return s.Loading?SessionState.Connecting:SessionState.Uncertain;}
        _stable=s.Window==_last?_stable+1:1;_last=s.Window;
        return _stable>=2?SessionState.Confirmed:SessionState.Connecting;
    }
}

public static class SessionObserver
{
    private delegate bool Callback(IntPtr h,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback,IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    public static HashSet<IntPtr> Windows(RemoteApp app,bool visibleOnly=true)
    {
        var ids=new HashSet<int>();
        foreach(var name in RemoteApps.Processes(app))
        foreach(var process in Process.GetProcessesByName(name))
            using(process)ids.Add(process.Id);
        var result=new HashSet<IntPtr>();
        EnumWindows((h,_)=>{GetWindowThreadProcessId(h,out uint id);if(ids.Contains((int)id)&&(!visibleOnly||IsWindowVisible(h)))result.Add(h);return true;},IntPtr.Zero);
        return result;
    }
    public static bool MatchesTarget(IEnumerable<string> labels,string code)=>code.Length>0&&labels.Any(name=>Regex.IsMatch(Regex.Replace(name,@"[\s-]",""),@"(?<!\d)"+Regex.Escape(code)+@"(?!\d)"));
    public static SessionSample Read(IntPtr window,bool isNew,string code)
    {
        var title=new StringBuilder(512);GetWindowText(window,title,title.Capacity);
        var names=new List<string>{title.ToString()};bool sessionControl=false;
        try
        {
            var root=AutomationElement.FromHandle(window);
            // Bounded traversal rather than FindAll over the entire remote desktop tree.
            var queue=new Queue<(AutomationElement Node,int Depth)>();queue.Enqueue((root,0));
            int visited=0;var clock=Stopwatch.StartNew();
            while(queue.Count>0 && visited++<100 && clock.ElapsedMilliseconds<150)
            {
                var(node,depth)=queue.Dequeue();string name=node.Current.Name;
                if(!string.IsNullOrWhiteSpace(name))names.Add(name);
                if((node.Current.ControlType==ControlType.Button || node.Current.ControlType==ControlType.MenuItem) && Regex.IsMatch(name.Trim(),@"^(断开连接|结束远程控制|结束会话|Disconnect|End session)$",RegexOptions.IgnoreCase))sessionControl=true;
                if(depth>=4)continue;
                var walker=TreeWalker.ControlViewWalker;var child=walker.GetFirstChild(node);
                for(int i=0;child!=null&&i<20;i++,child=walker.GetNextSibling(child))queue.Enqueue((child,depth+1));
            }
        }catch{} // Inaccessible UIA means unknown, never success.
        string text=string.Join("\n",names), flat=Regex.Replace(text,@"[\s-]","");
        var clientStatus=NegativeClientStatus.FromText(text);
        bool fail=clientStatus?.Status==ConnectStatus.Failed||Regex.IsMatch(text,@"密码错误|验证码错误|无法对同账号|无法连接到自己|连接失败|设备不在线|对方不在线|设备离线|连接被拒绝|认证失败|connection failed|incorrect password",RegexOptions.IgnoreCase);
        bool verify=clientStatus?.Status==ConnectStatus.NeedPassword||Regex.IsMatch(text,@"请输入验证码|请输入密码|安全验证|等待对方同意|请等待对方接受|等待授权|身份验证|等待验证|verification required|waiting for approval",RegexOptions.IgnoreCase);
        bool connected=names.Any(n=>Regex.IsMatch(n.Trim(),@"^(已连接|连接成功|会话已连接|Connected|Connection established)(\s|$|[：:])",RegexOptions.IgnoreCase));
        bool controls=sessionControl;
        bool loading=Regex.IsMatch(text,@"正在连接|建立连接中|加载中|connecting|loading",RegexOptions.IgnoreCase);
        bool target=MatchesTarget(names,code);
        if(!fail&&!verify&&!loading){
            try{if(SunloginSessionStatus.Read(window,code)){connected=true;controls=true;target=true;}}
            catch(Exception ex){RemoteConnectHelper.V2.Infrastructure.Log.Write("会话顶部校验不可用 "+ex.GetType().Name);}
        }
        return new(window.ToInt64(),isNew,target,connected,controls,verify,fail,loading,
            fail?(clientStatus?.Message??"客户端报告连接失败"):verify?(clientStatus?.Message??"客户端需要验证码或人工验证"):connected&&controls&&target?"本地客户端会话状态与目标已核对":"尚未取得足够的会话证据");
    }
    public static Task<ConnectResult> ConfirmAsync(RemoteApp app,string code,HashSet<IntPtr> before,IntPtr mainWindow,CancellationToken token)
        =>ConfirmCoreAsync(()=>Windows(app),Read,NegativeClientStatus.ReadAsync,code,before,mainWindow,token);
    internal static async Task<ConnectResult> ConfirmCoreAsync(Func<HashSet<IntPtr>> windows,Func<IntPtr,bool,string,SessionSample> read,
        Func<IntPtr,CancellationToken,Task<ConnectResult?>> readStatus,string code,HashSet<IntPtr> before,IntPtr mainWindow,CancellationToken token,TimeSpan? budget=null)
    {
        var detectors=new Dictionary<IntPtr,SessionEvidence>();var pending=new Dictionary<IntPtr,Task<SessionSample>>();
        var lastOcr=new Dictionary<IntPtr,long>();var clock=Stopwatch.StartNew();var limit=budget??TimeSpan.FromSeconds(20);
        while(clock.Elapsed<limit){
            token.ThrowIfCancellationRequested();var current=windows();
            foreach(var gone in detectors.Keys.Where(h=>!current.Contains(h)).ToArray())detectors.Remove(gone);
            foreach(var h in current.Take(12)){
                token.ThrowIfCancellationRequested();if(clock.Elapsed>=limit)break;
                if(!pending.TryGetValue(h,out var reading))pending[h]=reading=Task.Run(()=>read(h,!before.Contains(h),code),token);
                SessionSample sample;
                try{sample=await reading.WaitAsync(TimeSpan.FromMilliseconds(800),token);pending.Remove(h);}
                catch(TimeoutException){sample=new(h.ToInt64(),!before.Contains(h),h==mainWindow,false,false,false,false,false,"客户端控件读取超时，改用可见状态OCR");}
                catch(OperationCanceledException){throw;}
                catch{pending.Remove(h);continue;}
                if(before.Contains(h)&&h!=mainWindow&&!sample.TargetMatches)continue;
                if(!detectors.TryGetValue(h,out var detector))detectors[h]=detector=new();
                var state=detector.Observe(sample);
                if(state==SessionState.Failed)return ConnectResult.Fail(sample.Reason);
                if(state==SessionState.Verification)return ConnectResult.NeedPwd(sample.Reason);
                if(state==SessionState.Confirmed)return ConnectResult.Confirm();
                bool positive=sample.NewWindow&&sample.TargetMatches&&sample.ExplicitConnected&&sample.SessionControl&&!sample.Loading;
                if(!positive&&(!lastOcr.TryGetValue(h,out long last)||clock.ElapsedMilliseconds-last>=1200)){
                    lastOcr[h]=clock.ElapsedMilliseconds;
                    try{var status=await readStatus(h,token).WaitAsync(TimeSpan.FromSeconds(2),token);if(status!=null)return status;}
                    catch(TimeoutException){}catch(OperationCanceledException){throw;}catch{}
                }
            }
            var remaining=limit-clock.Elapsed;if(remaining>TimeSpan.Zero)await Task.Delay(remaining<TimeSpan.FromMilliseconds(600)?remaining:TimeSpan.FromMilliseconds(600),token);
        }
        return ConnectResult.Unknown("状态确认超时，尚未确认连接成功；请查看客户端提示，可修改后重试");
    }
}
