using RemoteConnectHelper.Automation;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.UI;
namespace RemoteConnectHelper.V2.Diagnostics;
public static class ExplicitConnectTests
{
    private static byte[] Image(bool other){
        using var bitmap=new System.Drawing.Bitmap(200,100);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.Clear(other?System.Drawing.Color.Blue:System.Drawing.Color.White);
        using var stream=new MemoryStream();bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return stream.ToArray();
    }
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);int exit=1;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            var rows=new List<object>();var imageA=Image(false);var imageB=Image(true);var clipboard=imageA;bool missing=false,failRead=false;int calls=0,analyses=0;bool pinnedWhileConnecting=true;string? lastCode=null;
            var parser=new RecognitionEngine();
            async Task<RecognitionResult> Analyze(byte[] bytes,CancellationToken token,Action<string>? progress){
                analyses++;await Task.Delay(10,token);bool other=bytes.SequenceEqual(imageB);
                var doc=new OcrDocument([new("brand","向日葵",new Box(10,10,80,20),.99f,"fixture","fixture"),new("label","识别码",new Box(10,50,80,20),.99f,"fixture","fixture"),new("code",other?"987654321":"123456789",new Box(10,80,160,24),.99f,"fixture","fixture")],500,300,"fixture","fixture");
                var result=parser.Analyze(doc);
                return result with{Decision=ConnectionDecision.Manual,Reason="密码包含易混或低置信度字符，请核对原图",SelectedCode=missing?null:result.SelectedCode,DeviceCodes=missing?[]:result.DeviceCodes};
            }
            await using var controller=new AssistantController(Analyze,(_,code,_,_)=>{calls++;lastCode=code;return Task.FromResult(ConnectResult.Fail("受控模拟：没有连接任何远程软件"));},(_,_)=>Task.FromResult(0));
            var prefs=new Preferences{WatchClipboard=false,ClipboardConsent=false,AutoConnect=false};
            var host=new DesktopHost(controller,prefs,(_,_)=>Task.FromResult<ClipboardSnapshot?>(failRead?null:new(10,clipboard,null)));
            controller.Changed+=()=>{if(controller.Stage==AssistantStage.Connecting&&(!host.Card.Topmost||host.Card.PinnedPreviewWindow?.Topmost!=true))pinnedWhileConnecting=false;};
            void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
            void Capture(string name){
                host.Card.UpdateLayout();var root=(FrameworkElement)host.Card.FindName("WindowVisual");
                var bitmap=new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),(int)Math.Ceiling(root.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
                using var stream=File.Create(Path.Combine(output,name+".png"));var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
            }
            try{
                host.OpenCurrent();await host.ReadClipboardAsync(false,true);await Task.Delay(100);
                Check(calls==0&&controller.Result?.Decision==ConnectionDecision.Manual,"Recognize-only fills without connecting");
                var risk=(System.Windows.Controls.Border)host.Card.FindName("RiskBox");var text=(System.Windows.Controls.TextBlock)host.Card.FindName("RiskText");
                Check(risk.IsVisible&&text.Text.Contains("密码包含易混"),"Exact reason is visible instead of suppressed");Capture("需核对原因显示在软件右侧");
                await host.ReadAndConnectAsync(useDisplayed:false);
                Check(calls==1&&lastCode=="123456789","Explicit click connects selected valid fields despite Manual classification");
                Check(!prefs.WatchClipboard&&!prefs.AutoConnect,"Explicit action works with background listening and automatic connection disabled");
                await host.ReadAndConnectAsync(useDisplayed:false);Check(calls==2,"Repeated explicit click retries once");
                clipboard=imageB;await host.ReadAndConnectAsync(useDisplayed:false);Check(calls==3&&lastCode=="987654321","Ball action reads new clipboard rather than stale displayed image");
                failRead=true;await host.ReadAndConnectAsync(useDisplayed:false);Check(calls==4&&lastCode=="987654321","Empty clipboard explicitly retries current original image");failRead=false;
                missing=true;await host.ReadAndConnectAsync(useDisplayed:false);Check(calls==4,"Incomplete target stays blocked without recursive recognition");missing=false;
                int priorAnalyses=analyses;missing=false;await host.ReadAndConnectAsync(useDisplayed:false);
                Check(analyses>priorAnalyses&&calls==5,"Same screenshot always reruns recognition and connection");
                int beforeBusy=calls;var first=host.ReadAndConnectAsync(useDisplayed:false);var second=host.ReadAndConnectAsync(useDisplayed:false);await Task.WhenAll(first,second);
                Check(calls==beforeBusy+1,"Clicks while busy do not start parallel connections");
                int beforeMain=calls;
                ((System.Windows.Controls.Button)host.Card.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                for(int i=0;i<100&&(calls==beforeMain||controller.Busy);i++)await Task.Delay(10);
                Check(calls==beforeMain+1,"Main recognize-and-connect button reruns current image");
                host.SetMainVisible(false);Check(!host.MainVisible,"Main visibility toggle hides editor");
                host.SetMainVisible(true);Check(host.MainVisible,"Main visibility toggle reopens editor");
                await host.ReadClipboardAsync(false,true,useDisplayed:false);host.Card.Width=320;await Task.Delay(150);
                Check(System.Windows.Controls.Grid.GetRow(risk)==1&&risk.IsVisible&&text.Text.Contains("密码包含易混"),"Narrow editor moves full reason below software selector");Capture("窄窗口完整显示原因");
                host.Card.Width=384;await Task.Delay(100);host.Card.RestorePreviewVisibility(true);await Task.Delay(100);
                host.SetMainVisible(false);Check(host.Card.PinnedPreviewWindow?.IsVisible==false,"Main hide also hides preview");
                host.SetMainVisible(true);await Task.Delay(100);Check(host.Card.PinnedPreviewWindow?.IsVisible==true,"Main reopen restores preview");
                Check(pinnedWhileConnecting&&host.Card.Topmost,"Connecting and retry never lower either assistant window");
                exit=0;
            }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.GetType().Name+": "+ex.Message});}
            finally{await host.ShutdownAsync();host.Card.ShutdownCard();File.WriteAllText(Path.Combine(output,"明确识别连接验收.json"),JsonSerializer.Serialize(new{Passed=exit==0,Scope="Real WPF windows, fake OCR and connector; no remote software launched, no input injected",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        }));app.Run();return exit;
    }
}
