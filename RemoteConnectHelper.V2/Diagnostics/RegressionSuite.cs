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
        await Test("confusable code conversion preserves raw text and requires review", () => Run(() =>
        { foreach(string raw in new[]{"12345O789","12345o789","12345I789","12345l789","12345S789","12345Z789","12345B789"})
          { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("c",raw,100,100,180))); Check(r.SelectedCode is {HadCharacterConversion:true} && r.SelectedCode.RawText==raw,"conversion provenance missing"); Check(r.Decision==ConnectionDecision.Manual,"converted code auto-connected"); Check(r.Document.Blocks.Single(b=>b.Id=="c").Text==raw,"raw OCR overwritten"); } }));
        await Test("isolated confusable character can be recovered without automatic connection", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("a","12345",100,100,90),B("b","O",190,100,18),B("c","789",208,100,54))); Check(r.DeviceCode=="123450789" && r.Decision==ConnectionDecision.Manual,"split glyph recovery failed"); }));
        await Test("low code confidence is not legal-format confidence", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180,.60f))); Check(ConnectionPolicy.ValidCode(r.App,r.DeviceCode) && r.Decision==ConnectionDecision.Manual,"low-confidence code auto-connected"); }));
        await Test("one unreliable code character is not hidden by the average", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("a","12345678",100,100,160,.99f),B("b","9",260,100,20,.20f))); Check(r.SelectedCode?.MinimumOcrConfidence==.20f && r.Decision==ConnectionDecision.Manual,"weak character lost"); }));
        await Test("two near-equal targets require a choice", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("a","123456789",100,100,180),B("b","987654321",100,300,180))); Check(r.DeviceCodes.Count==2 && r.Decision==ConnectionDecision.Manual,"ambiguous targets auto-connected"); }));
        await Test("letters alone cannot manufacture a device code", () => Run(() =>
        { Check(engine.Analyze(Doc(B("s","向日葵",20,20),B("c","OOOOOOOOO",100,100,180))).DeviceCodes.Count==0,"pure letters became a code"); }));
        await Test("numeric correction never deletes an explicitly labelled all-letter password", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","向日葵",20,20),B("c","123456789",100,100,180),B("pl","验证码",350,140,100),B("pw","OOOOOOOOO",350,180,180))); Check(r.Password=="OOOOOOOOO","letter password removed as a synthetic device code"); }));
        await Test("clear images use one primary OCR and no fallback", async () =>
        { var primary=new FakeProvider(Example()); var fallback=new FakeProvider(Example()); using var pipeline=new RecognitionPipeline(primary,fallback); var r=await pipeline.AnalyzeAsync([7],default); Check(primary.Calls==1 && fallback.Calls==0 && r.Decision==ConnectionDecision.Automatic,"clear image did redundant OCR"); });
        await Test("risky code is cross-checked and conflicting engines never vote by score", async () =>
        { var p=Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180,.60f)) with {Engine="primary"}; var f=Doc(B("s","ToDesk",20,20),B("c","987654321",100,100,180,.99f)) with {Engine="fallback"}; var primary=new FakeProvider(p); var fallback=new FakeProvider(f); using var pipeline=new RecognitionPipeline(primary,fallback); var r=await pipeline.AnalyzeAsync([8],default); Check(primary.Calls==2 && fallback.Calls==1,"risk was not checked"); Check(r.DeviceCode=="123456789" && r.Decision==ConnectionDecision.Manual && r.CrossCheckIssues.Any(s=>s.Contains("设备码不一致")),"score replaced conflicting code"); Check(r.Attempts.Any(a=>a.SelectedCode?.Value=="987654321"),"alternate evidence lost"); });
        await Test("different OCR passwords are never silently corrected", async () =>
        { var p=Example() with {Engine="primary",Blocks=Example().Blocks.Select(b=>b.Id=="pwd"?b with{Text="AO1234"}:b).ToArray()}; var f=Example() with {Engine="fallback",Blocks=Example().Blocks.Select(b=>b.Id=="pwd"?b with{Text="A01234"}:b).ToArray()}; using var pipeline=new RecognitionPipeline(new FakeProvider(p),new FakeProvider(f)); var r=await pipeline.AnalyzeAsync([9],default); Check(r.Password=="AO1234" && r.Decision==ConnectionDecision.Manual && r.CrossCheckIssues.Any(s=>s.Contains("密码不一致")),"password conflict hidden"); });
        await Test("fallback failure keeps risky result editable and manual", async () =>
        { var doc=Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180,.60f)); using var pipeline=new RecognitionPipeline(new FakeProvider(doc),new UnavailableProvider()); var r=await pipeline.AnalyzeAsync([10],default); Check(r.DeviceCode=="123456789" && r.Decision==ConnectionDecision.Manual && r.CrossCheckIssues.Any(s=>s.Contains("不可用")),"fallback failure discarded result or allowed auto"); });
        await Test("missing password remains allowed and explicitly reported", () => Run(() =>
        { var r=engine.Analyze(Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180))); Check(r.Decision==ConnectionDecision.Automatic && r.Warnings.Any(s=>s.Contains("未读到可见密码")),"missing password policy changed or not explained"); }));
        await Test("independent agreement may nominate a password but cannot erase a conflict", async () =>
        { var original=Example() with {Engine="primary",Blocks=Example().Blocks.Select(b=>b.Id=="pwd"?b with{Text="AO1234"}:b).ToArray()}; var corrected=Example() with {Engine="primary",Blocks=Example().Blocks.Select(b=>b.Id=="pwd"?b with{Text="A01234"}:b).ToArray()}; using var pipeline=new RecognitionPipeline(new VariantProvider((_,contrast)=>contrast?corrected:original),new FakeProvider(corrected with {Engine="fallback"})); var r=await pipeline.AnalyzeAsync([11],default); Check(r.Password=="A01234" && r.Decision==ConnectionDecision.Manual,"agreement lost or erased conflict"); Check(r.Attempts.Any(a=>a.SelectedPassword?.Value=="AO1234"),"original password overwritten"); Check(r.SelectedPassword!.BlockIds.All(id=>r.Document.Blocks.Any(b=>b.Id==id)),"consensus source invalid"); });
        await Test("unlabelled rotated background is not a contradiction of the target region", async () =>
        { var main=Doc(B("c","123456789",100,100,180)) with {Engine="primary"}; var noise=Doc(B("n","987654321",700,400,180)) with {Engine="primary",Rotation=90}; var clear=Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180)) with {Engine="primary",Rotation=270}; using var pipeline=new RecognitionPipeline(new VariantProvider((angle,_)=>angle==0?main:angle==90?noise:clear),new FakeProvider(clear with {Engine="fallback"})); var r=await pipeline.AnalyzeAsync([12],default); Check(r.DeviceCode=="123456789" && r.Decision==ConnectionDecision.Automatic,"background rotation blocked clear target"); Check(r.Attempts.Count==3,"background evidence lost"); });
        await Test("rotated disagreement in the same original region still requires review", async () =>
        { var main=Doc(B("c","987654321",100,100,180)) with {Engine="primary"}; var clear=Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180)) with {Engine="primary",Rotation=90}; using var pipeline=new RecognitionPipeline(new VariantProvider((angle,_)=>angle==0?main:clear),new FakeProvider(clear)); var r=await pipeline.AnalyzeAsync([13],default); Check(r.Decision==ConnectionDecision.Manual && r.CrossCheckIssues.Any(s=>s.Contains("设备码不一致")),"same-region conflict dismissed as background"); });
        await Test("different engines cannot be dismissed as background", async () =>
        { var main=Doc(B("s","ToDesk",20,20),B("c","123456789",100,100,180,.60f)) with {Engine="primary"}; var other=Doc(B("c","987654321",700,400,180)) with {Engine="fallback",Rotation=90}; using var pipeline=new RecognitionPipeline(new FakeProvider(main),new FakeProvider(other)); var r=await pipeline.AnalyzeAsync([14],default); Check(r.Decision==ConnectionDecision.Manual && r.CrossCheckIssues.Any(s=>s.Contains("设备码不一致")),"engine contradiction ignored"); });
        Console.WriteLine($"RESULT {passed} passed, {failed} failed");
        return failed==0?0:1;
    }
    private sealed class FakeProvider(OcrDocument doc) : IOcrProvider
    {
        public int Calls; public string Name=>"fake";
        public Task<OcrDocument> ReadAsync(byte[] image,int rotation,bool contrast,CancellationToken token){token.ThrowIfCancellationRequested();Calls++;return Task.FromResult(doc);}
        public void Dispose(){}
    }
    private sealed class UnavailableProvider : IOcrProvider
    {
        public string Name=>"unavailable";
        public Task<OcrDocument> ReadAsync(byte[] image,int rotation,bool contrast,CancellationToken token)
            =>Task.FromException<OcrDocument>(new InvalidOperationException("offline test engine unavailable"));
        public void Dispose(){}
    }
    private sealed class VariantProvider(Func<int,bool,OcrDocument> read) : IOcrProvider
    {
        public string Name=>"variant-test";
        public Task<OcrDocument> ReadAsync(byte[] image,int rotation,bool contrast,CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(read(rotation,contrast)); }
        public void Dispose(){}
    }
}
