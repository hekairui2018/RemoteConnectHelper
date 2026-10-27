using System.Text.Json;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Discovery;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.Ocr;
using SkiaSharp;
namespace RemoteConnectHelper.V2.Diagnostics;
public static class GrayDeskTests
{
    private static void Check(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
    public static async Task<int> RunAsync()
    {
        int passed=0,failed=0;
        async Task Test(string name,Func<Task> action){try{await action();passed++;Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        Task Run(Action action){action();return Task.CompletedTask;}
        OcrTextBlock B(string id,string text,double x,double y,double width=150)=>new(id,text,new Box(x,y,width,25),.99f,"test","original");
        OcrDocument Doc(params OcrTextBlock[] blocks)=>new(blocks,1000,800,"test","original");
        var parser=new RecognitionEngine();
        await Test("reuse visible client or open its entry point when no main window exists",()=>Run(()=>{
            var window=new ScreenOps.Win{Hwnd=new IntPtr(123),W=1000,H=640};int launches=0;
            var existing=ScreenOps.AcquireMainWindow(()=>window,["client.exe"],_=>{launches++;return null;});
            Check(ReferenceEquals(existing,window)&&launches==0,"Existing main window unnecessarily launched another client");
            var opened=ScreenOps.AcquireMainWindow(()=>null,["client.exe"],_=>{launches++;return window;});
            Check(ReferenceEquals(opened,window)&&launches==1,"Missing or tray-only main window did not invoke entry point");
            launches=0;
            opened=ScreenOps.AcquireMainWindow(()=>null,["old.exe","actual.exe","ACTUAL.exe"],path=>{launches++;return path=="actual.exe"?window:null;});
            Check(ReferenceEquals(opened,window)&&launches==2,"Alternate verified path not used");
            Check(ScreenOps.AcquireMainWindow(()=>null,[],_=>throw new Exception("Unexpected launch"))==null,"No available entry should report unavailable");
        }));
        await Test("whole-window readback re-locates remote input beyond click band",()=>Run(()=>{
            RemoteConnectHelper.Recognition.OcrWord W(string text,double x,double y,double width=160)=>new(){Text=text,X=x,Y=y,W=width,H=22};
            RemoteConnectHelper.Recognition.OcrDoc D(params RemoteConnectHelper.Recognition.OcrWord[] words)=>new(){Words=words.ToList(),ImgWidth=1200,ImgHeight=800};
            var doc=D(W("设备代码",40,110),W("987 654 321",40,155),W("远程控制设备",40,260),W("123",40,310,30),W("456",80,310,30),W("789",120,310,30));
            Check(WindowCodeVerification.Evaluate(doc,"123456789",345)==CodeReadback.Matched,"Full-row split code lost when click was right of text or y shifted");
            Check(WindowCodeVerification.Evaluate(doc,"987654321",345)==CodeReadback.Mismatch,"Local ID falsely verified remote target");
            doc.Words.RemoveAll(w=>w.Y==310);
            doc.Words.Add(W("最近连接",40,400));doc.Words.Add(W("123456789",40,440));
            Check(WindowCodeVerification.Evaluate(doc,"123456789",345)==CodeReadback.Unreadable,"History accepted as remote input");
            doc.Words.Add(W("请输入设备代码",40,310));
            Check(WindowCodeVerification.Evaluate(doc,"123456789",345)==CodeReadback.Unreadable,"Empty input reported mismatch");
            doc.Words.Add(W("0123456789",40,310));
            Check(WindowCodeVerification.Evaluate(doc,"123456789",345)!=CodeReadback.Matched,"Substring of longer ID accepted");
            Check(CoordOps.VerificationFailure("ToDesk",CodeReadback.Unreadable).Status==ConnectStatus.Uncertain,"Unreadable reported as failure");
            Check(CoordOps.VerificationFailure("ToDesk",CodeReadback.Mismatch).Status==ConnectStatus.Failed,"Different target not blocked");
            var sun=D(W("远程协助他人",40,260),W("123456789",40,310));
            Check(WindowCodeVerification.Evaluate(sun,"123456789",320)==CodeReadback.Matched,"Sunlogin target row lost");
            var inline=D(W("远程协助他人",40,260),W("123456",40,310,80),W("123456",300,310,80));
            Check(WindowCodeVerification.Evaluate(inline,"123456",320)==CodeReadback.Matched,"Inline numeric password merged with device code");
            Check(OcrGeo.NumberField(OcrGeo.BuildLines(inline),290,350)!=null,"Single six-digit filled field not located");
        }));

        await Test("startup discovery adds once, updates path and preserves aliases",()=>Run(()=>{
            var prefs=new Preferences{SoftwareEntries=[new("custom-uu","我的UU",@"C:\old\GameViewer.exe","uu")]};int saves=0;
            var catalog=new SoftwareCatalog(prefs,()=>saves++);
            var running=new InstalledSoftware("uu","UU远程",@"D:\new\GameViewer.exe","已运行进程",true);
            Check(catalog.MergeVerified(running)=="updated"&&catalog.Entries.Single().Name=="我的UU"&&catalog.Entries.Single().Id=="custom-uu","Alias or stable identity lost");
            Check(catalog.Entries.Single().Path==running.Path,"Running executable path not refreshed");
            Check(catalog.MergeVerified(running)=="unchanged"&&saves==1,"Repeated discovery duplicated or rewrote unchanged entry");
            prefs.SoftwareEntries!.Clear();Check(catalog.MergeVerified(running)=="added"&&catalog.Entries.Count==1,"New running client not added");
            Check(catalog.MergeVerified(running)=="unchanged"&&catalog.Entries.Count==1,"New client duplicated");
            bool rejected=false;try{catalog.MergeDiscovered(running with{Path=@"C:\missing\GameViewer.exe"});}catch(InvalidDataException){rejected=true;}
            Check(rejected&&catalog.Entries.Count==1,"Unverified path accepted");
        }));
        await Test("ordinary clipboard text and phones stay irrelevant",()=>Run(()=>{
            foreach(string text in new[]{"你好，明天再联系","联系电话13800138000","Autodesk 123456789","ToDesk 更新通知","ToDesk 电话 13800138000"})
                Check(RemoteTextParser.Parse(text)?.SelectedCode==null,"Unrelated text selected a target");
        }));
        await Test("explicit multiline remote text parses without OCR",()=>Run(()=>{
            var r=RemoteTextParser.Parse("ToDesk\n设备代码：123 456 789\n临时密码：Ab1234");
            Check(r?.DeviceCode=="123456789"&&r.Password=="Ab1234"&&r.Decision==ConnectionDecision.Automatic,"Explicit fields lost");
            Check(r!.Document.Engine.Contains("纯文本"),"OCR used for plain text");
        }));
        await Test("inline remote text pairs fields",()=>Run(()=>{
            var r=RemoteTextParser.Parse("向日葵 识别码：123456789 验证码：Ab7890");
            Check(r?.DeviceCode=="123456789"&&r.Password=="Ab7890","Inline code/password incorrect");
        }));
        await Test("unlabelled and multiple remote text targets require review",()=>Run(()=>{
            Check(RemoteTextParser.Parse("ToDesk 123456789")?.Decision==ConnectionDecision.Manual,"Unlabelled auto-connected");
            var r=RemoteTextParser.Parse("ToDesk\n设备代码 123456789\n设备代码 987654321");
            Check(r?.Decision==ConnectionDecision.Manual,"Competing text targets auto-connected");
        }));
        await Test("split code preserves raw blocks and confidence",()=>Run(()=>{
            var doc=Doc(B("s","ToDesk",10,10),B("l","设备代码",30,80),B("a","123",30,120,55),B("b","456",95,120,55),B("c","789",160,120,55));
            var r=parser.Analyze(doc);Check(r.DeviceCode=="123456789"&&r.SelectedCode!.BlockIds.Length==3,"Split merge failed");Check(ReferenceEquals(r.Document,doc),"Original OCR overwritten");
        }));
        await Test("history and shortcuts cannot win against labelled device",()=>Run(()=>{
            var r=parser.Analyze(Doc(B("s","ToDesk",10,10),B("h","历史连接",10,80),B("hc","987654321",180,80),B("l","设备代码",10,300),B("c","123456789",180,300),B("p","Ab1234",400,300)));
            Check(r.DeviceCode=="123456789"&&r.DeviceCodes.All(c=>c.Value!="987654321"),"History candidate leaked");
        }));
        await Test("same-app two rows do not cross-pair passwords",()=>Run(()=>{
            var r=parser.Analyze(Doc(B("s","ToDesk",10,10),B("a","123456789",30,150),B("pa","Ab1234",400,150),B("b","987654321",30,350),B("pb","Cd5678",400,350)));
            foreach(var c in r.DeviceCodes){var pw=r.Passwords.Where(x=>x.DeviceCandidateId==c.Id).MaxBy(x=>x.RuleScore);Check(pw?.Value==(c.Value=="123456789"?"Ab1234":"Cd5678"),"Cross-row password selected");}
        }));
        await Test("converted and low-confidence codes remain manual",()=>Run(()=>{
            var r=parser.Analyze(Doc(B("s","ToDesk",10,10),B("c","I23456789",30,130)));
            Check(r.SelectedCode?.HadCharacterConversion==true&&r.Decision==ConnectionDecision.Manual,"Converted code auto-connected");
            r=parser.Analyze(Doc(B("s","ToDesk",10,10),B("c","123456789",30,130) with{Confidence=.6f}));
            Check(r.Decision==ConnectionDecision.Manual,"Low confidence auto-connected");
        }));
        await Test("software identity rejects Autodesk, setup and renamed files",()=>Run(()=>{
            var d=RemoteSoftwareDiscovery.Definitions.Single(x=>x.Id=="todesk");
            Check(!RemoteSoftwareDiscovery.IdentityMatches(d,"ToDesk.exe","Autodesk AutoCAD"),"Autodesk accepted");
            Check(!RemoteSoftwareDiscovery.IdentityMatches(d,"ToDesk.exe","ToDesk Setup Installer"),"Installer accepted");
            Check(!RemoteSoftwareDiscovery.IdentityMatches(d,"other.exe","ToDesk"),"Wrong filename accepted");
            Check(RemoteSoftwareDiscovery.IdentityMatches(d,"ToDesk.exe","ToDesk Client"),"Client identity rejected");
        }));
        await Test("non-PE fake exe cannot be cached",()=>Run(()=>{
            string dir=Path.Combine(Path.GetTempPath(),"graydesk-identity-"+Guid.NewGuid());Directory.CreateDirectory(dir);string path=Path.Combine(dir,"ToDesk.exe");
            try{File.WriteAllText(path,"not an executable");Check(!RemoteSoftwareDiscovery.Validate(RemoteSoftwareDiscovery.Definitions[1],path),"Fake exe accepted");}
            finally{File.Delete(path);Directory.Delete(dir);}
        }));
        await Test("unsupported discoveries cannot claim connector support",()=>Run(()=>{
            Check(RemoteSoftwareDiscovery.Definitions.Count(x=>x.CanConnect)==7,"Unsupported connector advertised");
            Check(new Preferences().WatchClipboard&&new Preferences().ClipboardConsent==false,"Default listen preference or consent boundary changed");
            Check(new Preferences().ShowFloatingBall,"Floating entry default is off");
            var closed=System.Text.Json.JsonSerializer.Deserialize<Preferences>("{\"WatchClipboard\":false,\"ShowFloatingBall\":false,\"ClipboardConsent\":true}");
            Check(closed!=null&&!closed.WatchClipboard&&!closed.ShowFloatingBall&&closed.ClipboardConsent,"Explicit tray-off preference not preserved across reload");
        }));
        foreach(string app in new[]{"向日葵","ToDesk"})
        {
            SessionSample sample=new(42,true,true,true,true,false,false,false,"");
            await Test(app+" evidence: two stable independent positive samples",()=>Run(()=>{var d=new SessionEvidence();Check(d.Observe(sample)==SessionState.Connecting&&d.Observe(sample)==SessionState.Confirmed,"Confirmation failed");}));
            await Test(app+" evidence: old session, window alone and absent errors never succeed",()=>Run(()=>{
                foreach(var s in new[]{sample with{NewWindow=false},sample with{ExplicitConnected=false},sample with{TargetMatches=false},sample with{SessionControl=false},sample with{Loading=true}})
                {var d=new SessionEvidence();for(int i=0;i<3;i++)Check(d.Observe(s)!=SessionState.Confirmed,"False confirmation");}
            }));
            await Test(app+" evidence: wrong password, offline, waiting validation, timeout",()=>Run(()=>{
                foreach(string reason in new[]{"验证码错误","对方离线"})Check(new SessionEvidence().Observe(sample with{Failure=true,Reason=reason})==SessionState.Failed,"Failure ignored");
                Check(new SessionEvidence().Observe(sample with{Verification=true})==SessionState.Verification,"Verification ignored");
                Check(new SessionEvidence().Observe(sample with{ExplicitConnected=false,SessionControl=false})==SessionState.Uncertain,"Timeout became success");
            }));
            await Test(app+" evidence: different windows cannot accumulate confirmation",()=>Run(()=>{var d=new SessionEvidence();d.Observe(sample);Check(d.Observe(sample with{Window=43})!=SessionState.Confirmed,"Mixed windows confirmed");}));
        }
        await Test("initiated and uncertain retain customer; confirmed alone clears",async()=>{
            var result=RemoteTextParser.Parse("ToDesk\n设备代码123456789\n密码Ab1234")!;
            var next=ConnectResult.Ok();
            await using var c=new AssistantController((_,_,_)=>Task.FromResult(result),(_,_,_,_)=>Task.FromResult(next));
            await c.AcceptTextAsync("example",result);Check(c.Stage==AssistantStage.Uncertain&&c.Result!=null,"Initiated became confirmed");
            c.ForgetFinished(c.SessionId);Check(c.Result!=null,"Uncertain customer cleared");
            next=ConnectResult.Confirm();await c.ConnectAsync(RemoteApp.ToDesk,"123456789","Ab1234");
            Check(c.Stage==AssistantStage.Confirmed,"Confirmed status missing");c.ForgetFinished(c.SessionId);Check(c.Result==null,"Confirmed view retained");
        });
        await Test("ordinary background image gets only one primary OCR without fallback",async()=>{
            var primary=new CounterOcr();using var pipeline=new RecognitionPipeline(primary);
            var r=await pipeline.AnalyzeAsync([1,2,3],CancellationToken.None,null,true);
            Check(primary.Calls==1&&r.SelectedCode==null,"Irrelevant image ran full OCR rounds");
        });
        await Test("labelled but incomplete remote screenshot can request manual repair",()=>Run(()=>{
            var result=parser.Analyze(Doc(B("s","ToDesk",10,10),B("l","设备代码",10,70)));
            Check(RecognitionPipeline.LooksRemote(result)&&result.Decision==ConnectionDecision.Manual,"Incomplete remote screenshot silently discarded");
            result=parser.Analyze(Doc(B("s","普通聊天",10,10),B("l","明天见",10,70)));
            Check(!RecognitionPipeline.LooksRemote(result),"Chat picture classified as remote content");
        }));
        await Test("ToDesk adjacent to Chinese labels parses and Autodesk remains excluded",()=>Run(()=>{
            Check(RemoteTextParser.Parse("ToDesk设备代码：123456789 验证码：Ab1234")?.DeviceCode=="123456789","Adjacent Chinese label not parsed");
            Check(!RemoteTextParser.Suspected("Autodesk设备代码123456789"),"Autodesk boundary failed");
        }));
        await Test("copied image files accepted by extension, unrelated file paths ignored",()=>Run(()=>{
            Check(ClipboardReader.IsImageFile(@"D:\图片\客户.JPG")&&ClipboardReader.IsImageFile(@"E:\test.png"),"Image file extensions rejected");
            Check(!ClipboardReader.IsImageFile(@"D:\资料.docx")&&!ClipboardReader.IsImageFile(@"C:\image.jpg.exe"),"Unrelated file accepted");
        }));
        await Test("floating opacity is shared preference and clamped",()=>Run(()=>{
            var p=new Preferences{FloatingOpacity=.7};Check(p.EffectiveFloatingOpacity==.7,"Opacity not retained");
            p.FloatingOpacity=0;Check(p.EffectiveFloatingOpacity==.1,"Invisible default allowed");
            p.FloatingOpacity=double.NaN;Check(p.EffectiveFloatingOpacity==.85,"Non-finite opacity allowed");
        }));
        await Test("helper screenshot is never automatically connected as a customer image",()=>Run(()=>{
            var result=parser.Analyze(Doc(B("title","灰Desk",10,10),B("software","向日葵",10,70),
                B("label","设备码",10,120),B("code","123456789",180,120),B("pwd","Ab1234",400,120),B("button","查看原图",10,300)));
            Check(result.DeviceCode=="123456789"&&result.Decision==ConnectionDecision.Manual,"Default app in helper screenshot triggered automatic connection");
        }));
        await Test("connection history compares matching digit positions instead of repeated digit counts",()=>Run(()=>{
            Check(UI.ConnectionCard.MatchingDigits("123 456 789","123456780")==8,"Exact-position match score wrong");
            Check(UI.ConnectionCard.MatchingDigits("123456789","987654321")==1,"Wrong-position digits incorrectly counted");
            Check(UI.ConnectionCard.MatchingDigits("111","111111111")==3,"Repeated digits inflated match count");
            Check(UI.ConnectionCard.MatchingDigits("","123456789")==0,"Empty entry should preserve usual history order");
        }));
        await Test("text preview gives measured field boxes without image OCR",()=>Run(()=>{
            var preview=TextPreview.Create("ToDesk\n设备代码：123 456 789\n临时密码：Ab1234");
            Check(preview.Result?.DeviceCode=="123456789"&&preview.Result.Password=="Ab1234","Rendered text lost labelled fields");
            using var bitmap=SKBitmap.Decode(preview.Image);var doc=preview.Result!.Document;
            Check(doc.Width==bitmap.Width&&doc.Height==bitmap.Height&&doc.Engine.Contains("纯文本"),"Text coordinates or engine mismatch");
            Check(preview.Result.SelectedCode!.Bounds.Bottom<=bitmap.Height&&preview.Result.SelectedPassword!.Bounds.Right<=bitmap.Width,"Field outside preview");
        }));
        await Test("text preview keeps multiple targets and numeric passwords",()=>Run(()=>{
            var preview=TextPreview.Create("ToDesk\n设备代码：123456789\n临时密码：Ab1234\n\n设备代码：987654321\n临时密码：754321");
            Check(preview.Result?.DeviceCodes.Select(c=>c.Value).Distinct().Count()==2,"Second text device missing");
            Check(preview.Result!.Passwords.Any(c=>c.Value=="754321"),"Numeric password was discarded");
        }));
        await Test("ordinary text can be previewed without becoming a connection target",()=>Run(()=>{
            var preview=TextPreview.Create("普通文字\n第二行");Check(preview.Image.Length>0&&preview.Result==null,"Ordinary text became a target");
        }));
        await Test("preview excludes unrelated low evidence numbers but retains labelled alternatives",()=>Run(()=>{
            DeviceCodeCandidate C(string id,string value,RemoteApp app,double score,params string[] evidence)=>new(id,value,app,new Box(20,20+score,180,25),new Box(20,20+score,180,25),.99f,score,[],evidence);
            var selected=C("best","123456789",RemoteApp.Sunlogin,90,"设备码标签邻近");var noise=C("noise","234567890",RemoteApp.Sunlogin,40);var other=C("other","987654321",RemoteApp.ToDesk,86,"设备码标签邻近");
            var r=new RecognitionResult(Doc(),[],[selected,noise,other],[],selected,null,ConnectionDecision.Manual,"helper screenshot",[]);
            Check(PreviewCandidates.Codes(r).Select(c=>c.Code.Value).SequenceEqual(new[]{"123456789","987654321"}),"Noise retained or second software lost");
            var filled=PreviewCandidates.ForFill(r);Check(filled.DeviceCode=="123456789"&&filled.Decision==ConnectionDecision.Manual,"Fill preference wrong or competing targets auto-connect");
            Check(r.SelectedCode==selected&&r.DeviceCodes.Count==3,"Raw recognition evidence mutated");
        }));
        await Test("preview retains two credible passwords but excludes unrelated low score tokens",()=>Run(()=>{
            var code=new DeviceCodeCandidate("best","123456789",RemoteApp.Sunlogin,new Box(20,20,180,25),new Box(20,20,180,25),.99f,90,[],[]);
            PasswordCandidate P(string v,double score,params string[] evidence)=>new(v,code.Id,code.App,new Box(score*10,80,80,25),.99f,score,[],evidence);
            var passwords=new[]{P("Ab1234",95,"密码标签邻近"),P("Cd5678",90,"密码标签邻近"),P("2026",25)};
            var choice=new PreviewCandidates.Choice(code,passwords);
            Check(PreviewCandidates.Passwords(choice).Select(c=>c.Value).SequenceEqual(new[]{"Ab1234","Cd5678"}),"Password alternatives or noise filtering wrong");
        }));
        await Test("small helper title above password label is never a password candidate",()=>Run(()=>{
            var result=parser.Analyze(new OcrDocument([
                new("title","灰Desk",new Box(60,5,50,10),.99f,"test","original"),
                new("subtitle","Ab1234",new Box(65,18,40,9),.99f,"test","original"),
                new("brand","向日葵",new Box(20,40,45,10),.99f,"test","original"),
                new("codeLabel","设备码",new Box(20,55,35,10),.99f,"test","original"),
                new("code","123456789",new Box(20,68,90,12),.99f,"test","original"),
                new("pwdLabel","密码",new Box(20,85,25,10),.99f,"test","original"),
                new("pwd","Cd5678",new Box(20,100,65,12),.99f,"test","original")
            ],300,180,"test","original"));
            Check(result.Password=="Cd5678"&&result.Passwords.All(p=>p.Value!="Desk"&&p.Value!="Ab1234"),"Title/subtitle treated as password");
        }));
        await Test("password label must point right or below, not any nearby alphabetic text",()=>Run(()=>{
            var result=parser.Analyze(Doc(B("brand","向日葵",20,10),B("codeLabel","识别码",20,60),B("code","123456789",20,100),
                B("pwdLabel","密码",400,100,50),B("title","Settings",340,60,80),B("value","Ab1234",465,100,110)));
            Check(result.Password=="Ab1234"&&result.Passwords.All(p=>p.Value!="Settings"),"Nearby heading included");
        }));
        await Test("explicitly labelled pure-letter and numeric passwords still work",()=>Run(()=>{
            foreach(string password in new[]{"abcd","abcde","abcdef","1234","12345","754321"}){
                var preview=TextPreview.Create("ToDesk\n设备代码：123456789\n临时密码："+password);
                Check(preview.Result?.Password==password,"Valid labelled password rejected: "+password);
            }
        }));
        await Test("four and five character passwords near device are retained",()=>Run(()=>{
            foreach(string value in new[]{"a123","a1234","De5k"}){
                var result=parser.Analyze(Doc(B("brand","向日葵",20,10),B("codeLabel","识别码",20,60),B("code","123456789",20,100),B("short",value,210,100,65)));
                Check(result.Passwords.Any(p=>p.Value==value),"Four/five character password was filtered by length");
            }
        }));
        await Test("password candidates shorter than four remain excluded",()=>Run(()=>{
            foreach(string value in new[]{"a12","123","abc"}){
                var result=parser.Analyze(Doc(B("brand","向日葵",20,10),B("codeLabel","识别码",20,60),B("code","123456789",20,100),B("pwdLabel","密码",210,60,65),B("short",value,210,100,65)));
                Check(result.Passwords.All(p=>p.Value!=value),"Under-four password candidate accepted");
            }
        }));
        await Test("deleted software cannot be launched even by automatic recognition",async()=>{
            int calls=0;var result=RemoteTextParser.Parse("ToDesk 设备代码：123456789 密码：Ab1234")!;
            await using var controller=new AssistantController((_,_,_)=>Task.FromResult(result),(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Confirm());});
            controller.SoftwareAllowed=_=>false;await controller.AcceptTextAsync("removed-device",result);
            Check(calls==0&&controller.Stage==AssistantStage.NeedInput,"Removed software was connected");
        });
        await Test("manual success requires new stable connected session and ignores prior sessions",()=>Run(()=>{
            var tracker=new ManualSessionTracker();
            ClientSessionSample S(long id,bool connected=true,bool controls=true,bool verify=false,bool loading=false)=>new(RemoteApp.ToDesk,new(id,false,false,connected,controls,verify,false,loading,"synthetic"));
            Check(tracker.Observe([S(1)])==null&&tracker.Observe([S(1)])==null,"Preexisting remote session closed assistant");
            Check(tracker.Observe([S(1),S(2,false,false,true)])==null,"Password prompt marked successful");
            Check(tracker.Observe([S(1),S(2,false,true,false,true)])==null,"Connecting marked successful");
            Check(tracker.Observe([S(1),S(2)])==null&&tracker.Observe([S(1),S(2)])==RemoteApp.ToDesk,"Manual session was not confirmed after two stable samples");
        }));
        await Test("manual verification in existing window can finish but errors and missing controls cannot",()=>Run(()=>{
            var tracker=new ManualSessionTracker();var sample=new SessionSample(9,false,false,false,false,true,false,false,"synthetic");
            IReadOnlyList<ClientSessionSample> S(SessionSample s)=>[new(RemoteApp.Sunlogin,s)];
            tracker.Observe(S(sample));
            Check(tracker.Observe(S(sample with{Verification=false,Failure=true}))==null,"Failure became successful");
            Check(tracker.Observe(S(sample with{Verification=false,ExplicitConnected=true}))==null,"Status without session control succeeded");
            var done=sample with{Verification=false,ExplicitConnected=true,SessionControl=true};
            Check(tracker.Observe(S(done))==null&&tracker.Observe(S(done))==RemoteApp.Sunlogin,"Existing prompt transition was not accepted");
            Check(tracker.Observe([])==null,"Closed window retained success");
        }));
        await Test("observed manual success is revision checked and does not launch a client",async()=>{
            int calls=0;await using var c=new AssistantController((_,_,_)=>Task.FromResult(RemoteTextParser.Parse("ToDesk 设备代码：123456789")!),(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Unknown());});
            long old=c.Revision;await c.NewManualAsync();
            Check(!c.ConfirmObservedSession(c.SessionId,old,RemoteApp.ToDesk),"Stale probe completed current task");
            c.SoftwareAllowed=_=>false;Check(!c.ConfirmObservedSession(c.SessionId,c.Revision,RemoteApp.ToDesk),"Removed software completed task");
            c.SoftwareAllowed=_=>true;Check(c.ConfirmObservedSession(c.SessionId,c.Revision,RemoteApp.ToDesk)&&c.Stage==AssistantStage.Confirmed&&calls==0,"Manual completion launched client or did not confirm");
        });
        await Test("AnyDesk ID and UU text have their own software identity",()=>Run(()=>{
            foreach(var item in new[]{("AnyDesk\n设备码：1476266466\n密码：Ab123456",RemoteApp.AnyDesk,"1476266466"),("网易UU远程\n本设备ID：968532196\n设备验证码：Ab1234",RemoteApp.UU,"968532196")}){
                var result=RemoteTextParser.Parse(item.Item1);Check(result?.App==item.Item2&&result.DeviceCode==item.Item3,"New software misclassified");
                Check(ConnectionPolicy.ValidCode(item.Item2,item.Item3),"Valid new software code rejected");
            }
            Check(!ConnectionPolicy.ValidCode(RemoteApp.UU,"1476266466"),"UU accepted unsupported length");
        }));
        await Test("AnyDesk connects by arguments and password pipe without shell interpolation",()=>Run(()=>{
            var start=AdditionalClientConnector.AnyDeskStart("C:/Test/AnyDesk.exe","1476266466",true);
            Check(!start.UseShellExecute&&start.RedirectStandardInput&&start.ArgumentList.SequenceEqual(new[]{"1476266466","--with-password"}),"Unsafe or wrong AnyDesk command");
            Check(!start.Arguments.Contains("secret")&&!start.ArgumentList.Contains("--set-password"),"Local access settings or credential command line altered");
        }));
        await Test("RustDesk eight digit IDs are recognized without relaxing other clients",()=>Run(()=>{
            var result=RemoteTextParser.Parse("RustDesk\n设备码：98542363\n一次性密码：Ab1234");
            Check(result?.App==RemoteApp.RustDesk&&result.DeviceCode=="98542363","RustDesk eight digit ID missing");
            Check(RemoteTextParser.Parse("RustDesk 设备码：1422777")?.DeviceCode=="1422777","Official seven-digit RustDesk example rejected");
            Check(!ConnectionPolicy.ValidCode(RemoteApp.ToDesk,"98542363")&&!ConnectionPolicy.ValidCode(RemoteApp.Sunlogin,"98542363"),"Eight-digit RustDesk rule leaked to other clients");
            var start=AdditionalClientConnector.RustDeskStart("C:/Test/rustdesk.exe","98542363","test&value");
            Check(!start.UseShellExecute&&start.ArgumentList.SequenceEqual(new[]{"--connect","98542363","--password","test&value"}),"RustDesk connection arguments could alter local password or interpolate shell");
            Check(RemoteSoftwareDiscovery.Definitions.Single(d=>d.Id=="rustdesk").CanConnect,"RustDesk connector not registered");
        }));
        await Test("client error OCR never claims success and keeps pending requests separate",()=>Run(()=>{
            foreach(string text in new[]{"不能连接本机识别码","无法对同账号的设备发起远程协助","连接错误 Failed to connect: the target device is offline or does not exist.","密码错误，请重新输入"})Check(NegativeClientStatus.FromText(text)?.Status==ConnectStatus.Failed,"Explicit client failure lost");
            foreach(string text in new[]{"已连接","连接成功","无错误","允许控制本设备"})Check(NegativeClientStatus.FromText(text)==null,"OCR established success or unrelated text became failure");
            Check(NegativeClientStatus.FromText("请等待对方接受你的连接")?.Status==ConnectStatus.NeedPassword,"Approval wait was not retained");
        }));
        await Test("adaptive fields reject local IDs, logins and ambiguous partner controls",()=>Run(()=>{
            AdaptiveClientConnector.Field F(string id,string label,bool password=false)=>new(id,label,password,true);
            Check(AdaptiveClientConnector.SelectCode([F("own","Your ID"),F("remote","Partner ID"),F("login","email")])=="remote","Adaptive input confused own ID/login with partner");
            Check(AdaptiveClientConnector.SelectCode([F("a","Partner ID"),F("b","远程设备ID")])==null,"Ambiguous partner fields were guessed");
            Check(AdaptiveClientConnector.SelectPassword([F("local","设置永久密码",true),F("remote","Password",true)])=="remote","Local permanent password modified");
        }));
        await Test("Sunlogin success requires renderer, target, signal and quality without pending status",()=>Run(()=>{
            Check(SunloginSessionStatus.Sufficient(true,true,true,true,false),"Complete session evidence rejected");
            Check(!SunloginSessionStatus.Sufficient(true,true,true,true,true),"Loading/verification confirmed");
            Check(!SunloginSessionStatus.Sufficient(false,true,true,true,false)&&!SunloginSessionStatus.Sufficient(true,false,true,true,false)&&!SunloginSessionStatus.Sufficient(true,true,false,true,false)&&!SunloginSessionStatus.Sufficient(true,true,true,false,false),"Partial toolbar/session falsely confirmed");
        }));
        await Test("explicit trigger can retry cached image while passive duplicate stays blocked",async()=>{
            using var bitmap=new SKBitmap(4,4);bitmap.Erase(SKColors.White);using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);var image=encoded.ToArray();
            var recognized=parser.Analyze(Doc(B("s","ToDesk",10,10),B("c","123456789",30,130)));
            int calls=0;await using var controller=new AssistantController((_,_,_)=>Task.FromResult(recognized),(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Unknown());});
            await controller.RecognizeAsync(image);await controller.RecognizeAsync(image);Check(calls==1,"Passive duplicate retried");
            await controller.RecognizeAsync(image,explicitRetry:true);Check(calls==2,"Explicit ball retry was blocked");
        });
        await Test("window title and duplicate UIA names cannot merge device IDs",()=>Run(()=>{
            Check(SessionObserver.MatchesTarget(["123456789","123456789"],"123456789"),"Duplicated title masked target");
            Check(!SessionObserver.MatchesTarget(["123","456","789"],"123456789"),"Unrelated labels merged into target");
            Check(!SessionObserver.MatchesTarget(["01234567890"],"123456789"),"Substring of longer ID matched");
        }));
        Console.WriteLine($"GRAYDESK RESULT {passed} passed, {failed} failed");return failed==0?0:1;
    }
    private sealed class CounterOcr:IOcrProvider
    {
        public string Name=>"fake";public int Calls;
        public Task<OcrDocument> ReadAsync(byte[] image,int rotation,bool enhance,CancellationToken token){Calls++;return Task.FromResult(new OcrDocument([],100,100,"fake","test"));}
        public void Dispose(){}
    }
}
