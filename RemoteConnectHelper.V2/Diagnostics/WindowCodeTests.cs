using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Connections;
using SD=System.Drawing;

namespace RemoteConnectHelper.V2.Diagnostics;

internal static class WindowCodeTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var image=new SD.Bitmap(1200,800);
        using(var graphics=SD.Graphics.FromImage(image)){
            graphics.Clear(SD.Color.White);
            using var font=new SD.Font("Microsoft YaHei",24);
            graphics.DrawString("设备代码  987 654 321",font,SD.Brushes.Black,40,80);
            graphics.DrawString("远程控制设备",font,SD.Brushes.Black,40,260);
            graphics.DrawString("123 456 789",font,SD.Brushes.Black,40,315);
            graphics.DrawString("最近连接  246 813 579",font,SD.Brushes.Black,40,620);
        }
        image.Save(Path.Combine(output,"whole-window-fixture.png"),SD.Imaging.ImageFormat.Png);
        var result=WindowCodeVerification.ReadAsync(image,"123456789",365,CancellationToken.None).GetAwaiter().GetResult();
        var hidden=WindowCodeVerification.ReadAsync(image,"123456789",365,CancellationToken.None,_=>false).GetAwaiter().GetResult();
        bool passed=result==CodeReadback.Matched&&hidden==CodeReadback.Unreadable;
        File.WriteAllText(Path.Combine(output,"result.json"),System.Text.Json.JsonSerializer.Serialize(new{
            Passed=passed,WholeWindow=result.ToString(),OccludedWords=hidden.ToString(),
            Scope="Synthetic offline full-window fixture with real OCR; no remote client operated."}));
        Console.WriteLine("WINDOW READBACK "+(passed?"PASS":"FAIL")+" whole="+result+" occluded="+hidden);
        return passed?0:1;
    }
}
