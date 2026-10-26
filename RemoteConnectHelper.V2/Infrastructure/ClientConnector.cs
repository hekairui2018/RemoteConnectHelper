using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Discovery;
using RemoteConnectHelper.V2.Domain;
using LegacyKind=RemoteConnectHelper.Recognition.RemoteAppKind;
namespace RemoteConnectHelper.V2.Infrastructure;
public sealed class ClientConnector
{
    public async Task<ConnectResult> ConnectAsync(RemoteApp app,string code,string password,CancellationToken token)
    {
        if(!ConnectionPolicy.ValidCode(app,code))return ConnectResult.Fail("请填写有效设备码");
        string key=RemoteApps.Id(app);
        var installed=await Task.Run(()=>RemoteSoftwareDiscovery.Find(key),token);
        if(installed==null)return ConnectResult.Fail("没有发现已验证的远程软件，请从托盘「远程软件」补充路径");
        var before=SessionObserver.Windows(app,false);
        var context=new ConnectionContext(app==RemoteApp.Sunlogin?LegacyKind.Sunlogin:LegacyKind.ToDesk);
        context.PriorWindows=before;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        using var registration=linked.Token.Register(()=>{if(token.IsCancellationRequested)context.CancelByUser();else context.Cancel();});
        try
        {
            if(app is RemoteApp.AnyDesk or RemoteApp.UU or RemoteApp.RustDesk or RemoteApp.TeamViewer or RemoteApp.UltraViewer){
                var added=await AdditionalClientConnector.ConnectAsync(app,installed.Path,code,password,linked.Token).WaitAsync(linked.Token);
                if(added.Status is ConnectStatus.Failed or ConnectStatus.NeedPassword or ConnectStatus.Cancelled)return added;
                return await SessionObserver.ConfirmAsync(app,code,before,IntPtr.Zero,linked.Token);
            }
            IRemoteConnector connector=app==RemoteApp.Sunlogin?new SunloginConnector():new ToDeskConnector();
            var result=await connector.ConnectAsync(code,password,context).WaitAsync(linked.Token);
            token.ThrowIfCancellationRequested();
            if(timeout.IsCancellationRequested)return ConnectResult.Unknown("等待超时，尚未确认连接；请检查远程软件");
            if(result.Status is ConnectStatus.Failed or ConnectStatus.NeedPassword or ConnectStatus.Cancelled)return result;
            return await SessionObserver.ConfirmAsync(app,code,before,context.MainWindow,linked.Token);
        }
        catch(OperationCanceledException)
        {return token.IsCancellationRequested?ConnectResult.Cancel("已停止助手操作；已有会话保持不变"):ConnectResult.Unknown("等待超时，尚未确认连接");}
        catch(InvalidOperationException ex){Log.Write("连接已停止 "+ex.Message);return ConnectResult.Fail(ex.Message);}
        catch(Exception ex){RemoteSoftwareDiscovery.Invalidate(key);Log.Write("连接异常 "+ex.GetType().Name);return ConnectResult.Fail("连接未完成，请检查客户端并重试");}
        finally{context.Cts.Dispose();} // Never WM_CLOSE a window based merely on when it appeared.
    }
}
