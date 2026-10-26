using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.UI;
namespace RemoteConnectHelper.V2.Diagnostics;
public static class ConnectionStateTests
{
    private static System.Drawing.Bitmap Draw(string text)
    {
        var bitmap=new System.Drawing.Bitmap(900,300);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap)){
            graphics.Clear(System.Drawing.Color.FromArgb(22,22,22));
            using var font=new System.Drawing.Font("Microsoft YaHei",26);
            graphics.DrawString(text,font,System.Drawing.Brushes.White,30,120);
        }
        return bitmap;
    }
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);int result=1;var rows=new List<object>();
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            AssistantController? controller=null;ConnectionCard? card=null;Window? fixture=null;
            void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
            void Capture(string name){
                card!.UpdateLayout();var visual=(FrameworkElement)card.FindName("WindowVisual");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
                using var stream=File.Create(Path.Combine(output,name+".png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
            }
            try{
                Check(NegativeClientStatus.FromText("识 别 码 不 存 在 或 不 在 线")?.Status==ConnectStatus.Failed,"Exact screenshot error including split OCR words is a failure");
                Check(NegativeClientStatus.FromText("请输入连接密码")?.Message.Contains("等待输入密码")==true,"Password entry is a distinct pending state");
                Check(NegativeClientStatus.FromText("等待对方同意")?.Message.Contains("等待对方接受")==true,"Approval is separate from password entry");
                Check(NegativeClientStatus.FromText("密码错误，请重新输入")?.Status==ConnectStatus.Failed,"Wrong password remains a failure");
                Check(NegativeClientStatus.FromText("连接成功")==null,"OCR text alone cannot confirm success");
                using(var bitmap=Draw("识别码不存在或不在线"))Check((await NegativeClientStatus.ReadBitmapAsync(bitmap,CancellationToken.None))?.Status==ConnectStatus.Failed,"Real isolated PP-OCR reads offline error");
                using(var bitmap=Draw("请输入连接密码"))Check((await NegativeClientStatus.ReadBitmapAsync(bitmap,CancellationToken.None))?.Status==ConnectStatus.NeedPassword,"Real isolated PP-OCR reads password prompt");
                fixture=new Window{Title="连接状态受控测试",Width=760,Height=300,Left=50,Top=80,Topmost=true,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,Background=System.Windows.Media.Brushes.Black,Content=new System.Windows.Controls.TextBlock{Text="识别码不存在或不在线",Foreground=System.Windows.Media.Brushes.White,FontSize=28,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=System.Windows.HorizontalAlignment.Center}};
                fixture.Show();await Task.Delay(150);
                Check((await NegativeClientStatus.ReadAsync(new WindowInteropHelper(fixture).Handle,CancellationToken.None))?.Status==ConnectStatus.Failed,"Live owned status window is read through PP-OCR");
                fixture.Hide();Check(await NegativeClientStatus.ReadAsync(new WindowInteropHelper(fixture).Handle,CancellationToken.None)==null,"Hidden client is not replaced with stale background capture");fixture.Close();fixture=null;
                var handle=new IntPtr(1);
                SessionSample Loading(IntPtr h,bool fresh,string code)=>new(h.ToInt64(),fresh,false,false,false,false,false,true,"fixture");
                var failed=await SessionObserver.ConfirmCoreAsync(()=>[handle],Loading,(_,_)=>Task.FromResult(NegativeClientStatus.FromText("识别码不存在或不在线")),"fixture",[],handle,CancellationToken.None,TimeSpan.FromSeconds(2));
                Check(failed.Status==ConnectStatus.Failed,"OCR error terminates connection observation immediately");
                var fromOcr=await SessionObserver.ConfirmCoreAsync(()=>[handle],(h,fresh,code)=>{Thread.Sleep(1200);return Loading(h,fresh,code);},(_,_)=>Task.FromResult(NegativeClientStatus.FromText("识别码不存在或不在线")),"fixture",[],handle,CancellationToken.None,TimeSpan.FromSeconds(2));
                Check(fromOcr.Status==ConnectStatus.Failed,"Unresponsive UIA still falls back to status OCR");
                var password=await SessionObserver.ConfirmCoreAsync(()=>[handle],Loading,(_,_)=>Task.FromResult(NegativeClientStatus.FromText("请输入密码")),"fixture",[],handle,CancellationToken.None,TimeSpan.FromSeconds(2));
                Check(password.Status==ConnectStatus.NeedPassword,"Password prompt leaves connecting state");
                int reads=0,negativeCalls=0;
                var confirmed=await SessionObserver.ConfirmCoreAsync(()=>[handle],(h,fresh,code)=>{reads++;return new(h.ToInt64(),fresh,true,true,true,false,false,false,"fixture");},(_,_)=>{negativeCalls++;return Task.FromResult<ConnectResult?>(null);},"fixture",[],handle,CancellationToken.None,TimeSpan.FromSeconds(2));
                Check(confirmed.Status==ConnectStatus.Confirmed&&reads>=2&&negativeCalls==0,"Success needs stable session evidence and never scans established remote desktop for errors");
                var unknown=await SessionObserver.ConfirmCoreAsync(()=>[],Loading,(_,_)=>Task.FromResult<ConnectResult?>(null),"fixture",[],handle,CancellationToken.None,TimeSpan.FromMilliseconds(150));
                Check(unknown.Status==ConnectStatus.Uncertain,"No evidence ends as unconfirmed rather than endless connecting");
                ConnectResult next=failed;
                controller=new AssistantController((_,_,_)=>throw new InvalidOperationException("No image OCR in UI stage fixture"),(_,_,_,_)=>Task.FromResult(next));
                card=new ConnectionCard(controller);card.EnterEditor();
                var parsed=RemoteTextParser.Parse("向日葵 识别码：123456789")!;await controller.AcceptTextAsync("fixture",parsed,false);
                Check(card.FindName("RecognitionDetails")==null&&card.FindName("DetailsText")==null,"Recognition details entry and content are removed");
                await controller.ConnectAsync(RemoteApp.Sunlogin,"123456789","");
                Check(controller.Stage==AssistantStage.Failed&&!controller.Busy&&((System.Windows.Controls.TextBlock)card.FindName("TitleText")).Text=="连接失败","Failure is visible and clears connecting state");
                Check(((System.Windows.Controls.Button)card.FindName("ConnectButton")).IsEnabled&&((System.Windows.Controls.TextBox)card.FindName("CodeBox")).Text=="123456789","Failure retains code and restores retry");Capture("连接失败具体原因");
                next=password;await controller.ConnectAsync(RemoteApp.Sunlogin,"123456789","");
                Check(controller.Stage==AssistantStage.NeedInput&&!controller.Busy&&((System.Windows.Controls.TextBlock)card.FindName("TitleText")).Text=="等待输入密码","Password phase is shown as waiting for password");Capture("等待输入密码");
                var failureSample=new SessionSample(1,false,true,false,false,false,true,false,"验证码或密码错误，请核对后重试");
                Check(!controller.ApplyObservedConnectionState(controller.SessionId,controller.Revision-1,RemoteApp.Sunlogin,failureSample)&&!controller.ApplyObservedConnectionState(controller.SessionId,controller.Revision,RemoteApp.ToDesk,failureSample),"Stale task and unrelated client cannot change visible status");
                Check(controller.ApplyObservedConnectionState(controller.SessionId,controller.Revision,RemoteApp.Sunlogin,failureSample)&&controller.Stage==AssistantStage.Failed,"Wrong password after waiting phase updates failure without a new click");
                next=NegativeClientStatus.FromText("等待对方同意")!;await controller.ConnectAsync(RemoteApp.Sunlogin,"123456789","");
                Check(((System.Windows.Controls.TextBlock)card.FindName("TitleText")).Text=="等待对方确认","Approval phase has its own visible title");
                next=unknown;await controller.ConnectAsync(RemoteApp.Sunlogin,"123456789","");
                Check(controller.Stage==AssistantStage.Uncertain&&!controller.Busy,"Unknown status restores user control");
                next=confirmed;await controller.ConnectAsync(RemoteApp.Sunlogin,"123456789","");
                Check(controller.Stage==AssistantStage.Confirmed&&((System.Windows.Controls.TextBlock)card.FindName("TitleText")).Text=="已确认连接成功","Only confirmed result enters success state");
                result=0;
            }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.GetType().Name+": "+ex.Message});}
            finally{fixture?.Close();card?.ShutdownCard();if(controller!=null)await controller.DisposeAsync();File.WriteAllText(Path.Combine(output,"连接状态验收.json"),JsonSerializer.Serialize(new{Passed=result==0,Scope="Real isolated PP-OCR, own synthetic status window, injected observation and connector; no real remote client operations",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        }));app.Run();return result;
    }
}
