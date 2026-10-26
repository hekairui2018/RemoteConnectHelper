using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Discovery;
using RemoteConnectHelper.V2.Connections;

namespace RemoteConnectHelper.V2.Infrastructure;

/// <summary>Uses labelled, writable controls in the selected executable only. Ambiguity is manual.</summary>
public static class AdaptiveClientConnector
{
    public sealed record Field(string Key,string Label,bool Password,bool Writable);
    public static string? SelectCode(IEnumerable<Field> fields)
    {
        var choices=fields.Where(f=>f.Writable&&!f.Password
            && !Regex.IsMatch(f.Label,@"本机|本设备|您的|你的|自己|our ID|your ID|邮箱|账户|帐号|账号|email|login|密码|password|会话代码|session code",RegexOptions.IgnoreCase)
            && Regex.IsMatch(f.Label,@"伙伴.{0,5}(?:ID|识别码)|远程.{0,5}(?:ID|识别码|设备)|对方.{0,5}(?:ID|设备)|设备(?:代?码|ID)|识别码|partner.{0,5}ID|remote.{0,5}ID|输入最终用户的.?ID|ID[、,，].{0,8}(?:IP|地址|host)",RegexOptions.IgnoreCase)).ToArray();
        return choices.Length==1?choices[0].Key:null;
    }
    public static string? SelectPassword(IEnumerable<Field> fields)
    {
        var choices=fields.Where(f=>f.Writable&&(f.Password||Regex.IsMatch(f.Label,@"^(?:密码|验证码|安全码|password|verification code)$",RegexOptions.IgnoreCase))
            && !Regex.IsMatch(f.Label,@"本机|本设备|您的|your|永久|无人值守|设置|修改|再次|确认密码|new password|confirm password",RegexOptions.IgnoreCase)).ToArray();
        return choices.Length==1?choices[0].Key:null;
    }
    public static async Task<ConnectResult> ConnectAsync(string path,string code,string password,CancellationToken token)
    {
        if(!SoftwareCatalog.ValidExecutable(path))return ConnectResult.Fail("所选软件启动路径无效，请更换路径");
        if(!Regex.IsMatch(code,@"^\d{7,12}$")||password.Any(c=>c is '\r' or '\n'))return ConnectResult.NeedPwd("请核对设备码和密码");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try{return await Task.Run(()=>RunAsync(Path.GetFullPath(path),code,password,timeout.Token),timeout.Token).WaitAsync(timeout.Token);}
        catch(OperationCanceledException){return token.IsCancellationRequested?ConnectResult.Cancel("已停止自适应操作"):ConnectResult.Unknown("自适应定位超时，请在软件中核对");}
    }
    private sealed record View(AutomationElement Root,List<AutomationElement> Nodes);
    private static IEnumerable<View> Views(string path)
    {
        var ids=new HashSet<int>();
        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))using(process)
            try{if(string.Equals(process.MainModule?.FileName,path,StringComparison.OrdinalIgnoreCase))ids.Add(process.Id);}catch{}
        var windows=AutomationElement.RootElement.FindAll(TreeScope.Children,Condition.TrueCondition);
        foreach(AutomationElement root in windows){
            View? view=null;try{if(ids.Contains(root.Current.ProcessId)&&!root.Current.IsOffscreen)view=new(root,AdditionalClientConnector.Nodes(root));}catch(ElementNotAvailableException){}
            if(view!=null)yield return view;
        }
    }
    private static bool Owns(View view,AutomationElement element)
    {
        try{
            if(element.Current.IsOffscreen||!element.Current.IsEnabled)return false;
            if(ProcessOwnership.IsDescendant(view.Root.Current.ProcessId,element.Current.ProcessId))return true;
            using var root=Process.GetProcessById(view.Root.Current.ProcessId);
            using var child=Process.GetProcessById(element.Current.ProcessId);
            string? rootPath=root.MainModule?.FileName,childPath=child.MainModule?.FileName;
            return rootPath!=null&&string.Equals(rootPath,childPath,StringComparison.OrdinalIgnoreCase);
        }catch{return false;}
    }
    private static string Label(View view,AutomationElement edit)
    {
        string own=edit.Current.Name??"";var labelled=edit.Current.LabeledBy;
        if(labelled!=null)return own+" "+labelled.Current.Name;
        if(!string.IsNullOrWhiteSpace(own))return own.Trim();
        var bounds=edit.Current.BoundingRectangle;
        var labels=view.Nodes.Where(n=>n.Current.ControlType==ControlType.Text&&!n.Current.IsOffscreen)
            .Select(n=>(Name:n.Current.Name,Bounds:n.Current.BoundingRectangle))
            .Where(n=>!string.IsNullOrWhiteSpace(n.Name)&&!n.Bounds.IsEmpty
                && ((Math.Abs(n.Bounds.Y-bounds.Y)<Math.Max(bounds.Height,n.Bounds.Height)&&n.Bounds.Right<=bounds.X+8&&bounds.X-n.Bounds.Right<180)
                  ||(n.Bounds.Bottom<=bounds.Y+4&&bounds.Y-n.Bounds.Bottom<45&&Math.Abs(n.Bounds.X-bounds.X)<60)))
            .OrderBy(n=>Math.Abs(n.Bounds.Bottom-bounds.Y)+Math.Abs(n.Bounds.Right-bounds.X)).ToArray();
        return labels.FirstOrDefault().Name??"";
    }
    private static (List<Field> Fields,Dictionary<string,AutomationElement> Elements) Fields(View view)
    {
        var fields=new List<Field>();var elements=new Dictionary<string,AutomationElement>();
        foreach(var node in view.Nodes.Where(n=>(n.Current.ControlType==ControlType.Edit||n.Current.ControlType==ControlType.ComboBox)&&!n.Current.IsOffscreen&&n.Current.IsEnabled)){
            if(!node.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)||pattern is not ValuePattern value||value.Current.IsReadOnly)continue;
            string key=fields.Count.ToString();elements[key]=node;fields.Add(new(key,Label(view,node).Trim(),node.Current.IsPassword,true));
        }
        return(fields,elements);
    }
    private static bool Set(View view,AutomationElement element,string value,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(!Owns(view,element))return false;
        if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)&&pattern is ValuePattern v&&!v.Current.IsReadOnly){
            token.ThrowIfCancellationRequested();v.SetValue(value);
            for(int attempt=0;attempt<8;attempt++){
                token.ThrowIfCancellationRequested();
                if(element.Current.IsPassword||Regex.Replace(v.Current.Value,@"[\s-]","")==value)return true;
                Thread.Sleep(100);
            }
            return false;
        }
        return false;
    }
    private static bool Submit(View view,bool password,CancellationToken token)
    {
        string names=password?@"^(确定|确认|连接|验证|登录|Log on|OK|Connect|Authenticate)$":@"^(连接|连接远程计算机|连接远程电脑|远程控制|Connect|Connect to partner|Connect to remote computer)$";
        var buttons=view.Nodes.Where(n=>n.Current.ControlType==ControlType.Button&&Owns(view,n)&&Regex.IsMatch(n.Current.Name.Trim(),names,RegexOptions.IgnoreCase)).ToArray();
        if(buttons.Length!=1)return false;
        token.ThrowIfCancellationRequested();
        if(buttons[0].TryGetCurrentPattern(InvokePattern.Pattern,out var pattern)&&pattern is InvokePattern invoke){token.ThrowIfCancellationRequested();invoke.Invoke();return true;}
        return false;
    }
    private static async Task<ConnectResult> RunAsync(string path,string code,string password,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool running=false;foreach(var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))using(existing)try{running|=string.Equals(existing.MainModule?.FileName,path,StringComparison.OrdinalIgnoreCase);}catch{}
        if(!running){using var process=Process.Start(new ProcessStartInfo(path){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(path)!});}
        View? chosen=null;AutomationElement? codeField=null;string? passwordKey=null;
        for(int round=0;round<18;round++){
            token.ThrowIfCancellationRequested();var matches=new List<(View View,AutomationElement Code,string? Password)>();
            foreach(var view in Views(path)){
                try{var f=Fields(view);string? key=SelectCode(f.Fields);if(key!=null)matches.Add((view,f.Elements[key],SelectPassword(f.Fields)));}catch(ElementNotAvailableException){}
            }
            if(matches.Count>1)return ConnectResult.NeedPwd("发现多个设备码输入区域，请在软件里选择目标");
            if(matches.Count==1){chosen=matches[0].View;codeField=matches[0].Code;passwordKey=matches[0].Password;break;}
            await Task.Delay(300,token);
        }
        if(chosen==null||codeField==null)return await NativeLabelledConnector.ConnectAsync(path,code,password,token);
        if(!Set(chosen,codeField,code,token))return ConnectResult.NeedPwd("设备码输入框不能可靠填写，请人工核对");
        if(password.Length>0&&passwordKey!=null){var f=Fields(chosen);var key=SelectPassword(f.Fields);if(key==null||!Set(chosen,f.Elements[key],password,token))return ConnectResult.NeedPwd("设备码已填写，密码位置不明确，请人工核对");}
        if(!Submit(chosen,false,token))return NativeLabelledConnector.SubmitVerification(path,password,token)??ConnectResult.NeedPwd("设备码已填写，连接按钮不明确，请人工点击");
        if(password.Length==0||passwordKey!=null)return ConnectResult.Ok("已发起连接，等待实际会话确认");
        for(int round=0;round<30;round++){
            await Task.Delay(300,token);
            var nativeVerification=NativeLabelledConnector.SubmitVerification(path,password,token);if(nativeVerification!=null)return nativeVerification;
            var views=Views(path).ToArray();
            foreach(var view in views){
                string text=string.Join("\n",view.Nodes.Select(n=>{try{return n.Current.Name;}catch(ElementNotAvailableException){return "";}}));var failure=NegativeClientStatus.FromText(text);if(failure?.Status==ConnectStatus.Failed)return failure;
            }
            var passwordViews=new List<(View View,(List<Field> Fields,Dictionary<string,AutomationElement> Elements) Data,string? Key)>();
            foreach(var view in views){try{var fields=Fields(view);var key=SelectPassword(fields.Fields);if(key!=null)passwordViews.Add((view,fields,key));}catch(ElementNotAvailableException){}}
            var matches=passwordViews.ToArray();
            if(matches.Length>1)return ConnectResult.NeedPwd("发现多个密码输入区域，请人工选择");
            if(matches.Length==0)continue;
            var target=matches[0];if(!Set(target.View,target.Data.Elements[target.Key!],password,token))return ConnectResult.NeedPwd("请在客户端填写密码");
            return Submit(target.View,true,token)?ConnectResult.Ok("密码已提交，等待实际会话确认"):ConnectResult.NeedPwd("密码已填写，请在客户端确认");
        }
        return ConnectResult.Unknown("已发起连接，尚未找到密码弹窗或成功会话");
    }
}
