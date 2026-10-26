using System.Windows;
using System.Windows.Controls;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.UI;
using SkiaSharp;
using Window=System.Windows.Window;
using Button=System.Windows.Controls.Button;

namespace RemoteConnectHelper.V2.Diagnostics;

/// <summary>Interactive demo with no real OCR, clipboard, hotkey, tray or native connection dependency.</summary>
public static class DesktopDemo
{
    public static int Run()
    {
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        int generation=0;string scene="clear";
        async Task<RecognitionResult> Analyze(byte[] _,CancellationToken token,Action<string>? progress)
        {
            string current=scene;await Task.Delay(300,token);
            var blocks=new List<OcrTextBlock>{
                new("brand","向日葵",new Box(30,20,100,25),.99f,"安全演示（模拟识别）","synthetic")};
            if(current!="no-code")
            {
                blocks.Add(new("code","123456789",new Box(40,110,220,35),.99f,"安全演示","synthetic"));
                blocks.Add(new("pwd","Ab1234",new Box(330,110,140,35),.99f,"安全演示","synthetic"));
            }
            var r=new RecognitionEngine().Analyze(new OcrDocument(blocks,800,300,"安全演示（模拟识别）","synthetic"));
            return current=="ambiguous"?r with{Decision=ConnectionDecision.Manual,Reason="模拟：两个OCR读数存在冲突，请对照原图"}:r;
        }
        async Task<ConnectResult> Connect(RemoteApp _,string code,string password,CancellationToken token)
        {
            string current=scene;await Task.Delay(2200,token);
            return current=="uncertain"?ConnectResult.Unknown("模拟：尚未确认连接；保留卡片等待检查"):current=="failure"?ConnectResult.NeedPwd("模拟失败：请修改密码再试；演示不会连接任何电脑"):ConnectResult.Confirm("模拟：已确认连接。演示没有执行真实连接");
        }
        var controller=new AssistantController(Analyze,Connect);
        var card=new ConnectionCard(controller);
        var panel=new StackPanel{Margin=new Thickness(22)};
        panel.Children.Add(new TextBlock{Text="灰Desk · 安全交互演示",FontSize=24,Foreground=System.Windows.Media.Brushes.MidnightBlue});
        panel.Children.Add(new TextBlock{Text="全部为模拟客户。这里不会连接任何电脑、读取真实剪贴板或注册全局热键。\n点下面的场景，再观察小卡；可以在模拟聊天框继续打字。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)});
        var row=new WrapPanel();
        foreach(var entry in new[]{("清晰客户A","clear"),("有歧义的客户B","ambiguous"),("没有读到设备码","no-code"),("连接失败 / 补密码","failure"),("状态未确认","uncertain")})
        {
            var button=new Button{Content=entry.Item1,Margin=new Thickness(0,0,8,8),Padding=new Thickness(9,7,9,7)};
            button.Click+=async(_,_)=>{
                scene=entry.Item2;generation++;
                using var bitmap=new SKBitmap(800,300);using var canvas=new SKCanvas(bitmap);canvas.Clear(SKColors.White);
                using var font=new SKFont(SKTypeface.FromFamilyName("Consolas"),30);using var paint=new SKPaint{Color=SKColors.DarkSlateBlue,IsAntialias=true};
                canvas.DrawText("SAFE DEMO / "+generation,35,60,font,paint);
                canvas.DrawText(scene=="no-code"?"NO REMOTE CODE":"123 456 789       Ab1234",35,140,font,paint);
                using var image=bitmap.Encode(SKEncodedImageFormat.Png,100);await controller.RecognizeAsync(image.ToArray());
            };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        panel.Children.Add(new System.Windows.Controls.TextBox{Text="模拟聊天输入框：在此输入、回车；助手提示不应抢焦点",AcceptsReturn=true,Height=85,TextWrapping=TextWrapping.Wrap,Padding=new Thickness(8),Margin=new Thickness(0,8,0,0)});
        var window=new Window{Title="安全演示 · 不会连接真实客户",Width=575,Height=420,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        card.ReadRequested+=()=>controller.ShowMessage("安全演示不读取真实截图，请在演示窗口选择下一位客户");
        card.ManualRequested+=async()=>{await controller.NewManualAsync();card.EnterEditor();};
        ((Button)card.FindName("OpenButton")).IsEnabled=false;
        window.Closed+=async(_,_)=>{await controller.DisposeAsync();card.ShutdownCard();app.Shutdown();};
        app.MainWindow=window;window.Show();app.Run();return 0;
    }
}
