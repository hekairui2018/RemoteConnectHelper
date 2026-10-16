using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Ocr;
using RemoteConnectHelper.V2.Parsing;

namespace RemoteConnectHelper.V2.Diagnostics;

public static class RegressionSuite
{
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); }
    private static OcrTextBlock B(string id, string text, double x, double y, double width = 80, float? confidence = .95f)
        => new(id, text, new Box(x, y, width, 25), confidence, "synthetic", "original");
    private static OcrDocument Doc(params OcrTextBlock[] blocks) => new(blocks, 1000, 600, "synthetic", "original");
    private static OcrDocument Example() => Doc(B("brand", "向日葵", 30, 20), B("label", "本机识别码", 100, 75),
        B("a", "123", 100, 115, 50), B("b", "456", 165, 115, 50), B("c", "789", 230, 115, 50),
        B("pwd", "Ab1234", 410, 115, 100));
    public static async Task<int> RunAsync()
    {
        int passed = 0, failed = 0;
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        Task Run(Action test) { test(); return Task.CompletedTask; }
        var engine = new RecognitionEngine();
        await Test("grouped device code with side-by-side password", () => Run(() =>
        { var r = engine.Analyze(Example()); Check(r.DeviceCode == "123456789" && r.Password == "Ab1234", "code/password pairing failed"); Check(r.Decision == ConnectionDecision.Automatic, "known app requires confirmation"); }));
        await Test("unrelated page labels are not passwords", () => Run(() =>
        { var doc = Example(); var r = engine.Analyze(doc with { Blocks = doc.Blocks.Append(B("web", "LEDLE", 0, 115, 55)).ToArray() }); Check(r.Password == "Ab1234", "web text stole password"); }));
        await Test("password remains case-sensitive", () => Run(() =>
        { Check(engine.Analyze(Example()).Password == "Ab1234", "case changed"); }));
        await Test("unknown app remains manual", () => Run(() =>
        { var r = engine.Analyze(Doc(B("code", "123456789", 100, 100, 180))); Check(r.DeviceCode == "123456789" && r.Decision == ConnectionDecision.Manual, "unknown app guessed"); }));
        await Test("empty password does not block known app", () => Run(() =>
        { var r = engine.Analyze(Doc(B("brand", "ToDesk", 20, 20), B("code", "123456789", 100, 100, 180))); Check(r.Decision == ConnectionDecision.Automatic && r.Password == "", "empty password blocked"); }));
        await Test("two windows bind codes to their own software", () => Run(() =>
        { var r = engine.Analyze(Doc(B("s", "向日葵", 20, 20), B("t", "ToDesk", 650, 20), B("sc", "123456789", 40, 100, 180), B("tc", "987654321", 680, 100, 180))); Check(r.DeviceCodes.Single(c => c.Value == "123456789").App == RemoteApp.Sunlogin, "left code assigned wrong app"); Check(r.DeviceCodes.Single(c => c.Value == "987654321").App == RemoteApp.ToDesk, "right code assigned wrong app"); }));
        await Test("telephone numbers are not device codes", () => Run(() =>
        { Check(engine.Analyze(Doc(B("s", "向日葵", 20, 20), B("p", "13800138000", 100, 100, 180))).DeviceCodes.Count == 0, "phone matched"); }));
        await Test("IP addresses and order numbers are not device codes", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("ip","192.168.1.88",100,100,180),B("order","订单号",20,300),B("n","8872634591",140,300,180),B("label","设备代码",20,200),B("c","123456789",150,200,180))); Check(r.DeviceCode=="123456789","IP/order number chosen"); Check(r.DeviceCodes.Count==1,"noise code leaked"); }));
        await Test("nearby password can select partner input over unrelated local ID", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","向日葵",20,20),B("local","本机识别码",20,80,120),B("lc","987654321",170,80,180),B("partner","请输入伙伴识别码",20,300,160),B("pc","123456789",240,300,180),B("pl","验证码",20,370,120),B("pw","Ab1234",240,370,100))); Check(r.DeviceCode=="123456789" && r.Password=="Ab1234","unrelated local ID won over explicit code/password pair"); }));
        await Test("unknown OCR confidence stays unknown", () => Run(() =>
        { var r = engine.Analyze(Doc(B("s", "向日葵", 20, 20), B("c", "123456789", 100, 100, 180, null))); Check(r.SelectedCode?.OcrConfidence == null && r.Decision == ConnectionDecision.Automatic, "invented score or confidence blocked auto"); }));
        await Test("password label does not attach unrelated file list in the same row", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",400,20),B("c","123456789",400,160,180),B("label","临时密码",800,130,100),B("file","R508R507",20,160,75))); Check(r.Password=="","file list mistaken for password"); }));
        await Test("Sunlogin assistance panel identifies cropped software", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","远程协助他人",20,250,160),B("c","123456789",100,150,180),B("pw","Ab1234",400,150,100))); Check(r.App==RemoteApp.Sunlogin && r.Decision==ConnectionDecision.Automatic,"Sunlogin panel not identified"); }));
        await Test("all code formats validated", () => Run(() =>
        { foreach(string c in new[]{"", "12345678", "12345678901", "abcdefghi", "１２３４５６７８９"}) Check(!ConnectionPolicy.ValidCode(RemoteApp.Sunlogin,c), "invalid code accepted"); Check(ConnectionPolicy.ValidCode(RemoteApp.Sunlogin,"1234567890"), "Sunlogin 10 digits rejected"); Check(!ConnectionPolicy.ValidCode(RemoteApp.ToDesk,"1234567890"), "ToDesk 10 digits accepted"); }));
        await Test("rotated bounds map back to original pixels", () => Run(() =>
        { var original = new Box(20, 30, 100, 15); Check(ImagePreparation.ToOriginal(new Box(255,20,15,100),90,400,300)==original,"90 degrees"); Check(ImagePreparation.ToOriginal(new Box(280,255,100,15),180,400,300)==original,"180 degrees"); Check(ImagePreparation.ToOriginal(new Box(30,280,15,100),270,400,300)==original,"270 degrees"); }));
        await Test("candidate provenance points at source blocks", () => Run(() =>
        { var r=engine.Analyze(Example()); Check(r.SelectedCode!.BlockIds.All(id=>r.Document.Blocks.Any(b=>b.Id==id)),"missing code source"); Check(r.SelectedPassword!.DeviceCandidateId==r.SelectedCode.Id,"unpaired password"); }));
        await Test("cache uses complete image bytes", async () =>
        { var provider=new FakeProvider(Example()); using var pipeline=new RecognitionPipeline(provider,new FakeProvider(Example())); await pipeline.AnalyzeAsync([1,2,3],default); await pipeline.AnalyzeAsync([1,2,3],default); await pipeline.AnalyzeAsync([1,2,4],default); Check(provider.Calls==2,"stale or missing cache"); });
        await Test("cancelled request does not start OCR", async () =>
        { var provider=new FakeProvider(Example()); using var pipeline=new RecognitionPipeline(provider,new FakeProvider(Example())); using var cts=new CancellationTokenSource(); cts.Cancel(); try { await pipeline.AnalyzeAsync([1],cts.Token); throw new Exception("continued"); } catch(OperationCanceledException){} Check(provider.Calls==0,"OCR ran after cancellation"); });
        await Test("replacement waits for previous cleanup", async () =>
        { var host=new OperationHost(); var entered=new TaskCompletionSource(); bool cleaned=false; var first=host.RunAsync(async token=>{entered.SetResult(); try{await Task.Delay(Timeout.Infinite,token);}finally{await Task.Delay(30);cleaned=true;}}); await entered.Task; await host.RunAsync(_=>{Check(cleaned,"overlapping operations");return Task.CompletedTask;}); try{await first;}catch(OperationCanceledException){} });
        await Test("failure permits retry and stop", async () =>
        { var host=new OperationHost(); try{await host.RunAsync(_=>Task.FromException(new Exception("local failure")));}catch(Exception){} bool retried=false; await host.RunAsync(_=>{retried=true;return Task.CompletedTask;}); await host.StopAsync(); Check(retried,"failure locked runner"); });
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed==0?0:1;
    }
    private sealed class FakeProvider(OcrDocument doc) : IOcrProvider
    {
        public int Calls; public string Name=>"fake";
        public Task<OcrDocument> ReadAsync(byte[] image,int rotation,bool contrast,CancellationToken token){token.ThrowIfCancellationRequested();Calls++;return Task.FromResult(doc);}
        public void Dispose(){}
    }
}
