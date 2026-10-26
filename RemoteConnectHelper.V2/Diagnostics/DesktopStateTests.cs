using System.Security.Cryptography;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Parsing;
using SkiaSharp;

namespace RemoteConnectHelper.V2.Diagnostics;

public static class DesktopStateTests
{
    private static void Check(bool value,string error){if(!value)throw new InvalidOperationException(error);}
    private static byte[] Image(int key)
    {
        using var bitmap=new SKBitmap(4,4);bitmap.Erase(new SKColor((byte)key,50,70));
        using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);return encoded.ToArray();
    }
    private static RecognitionResult Result(string code="123456789",string password="Ab1234",bool manual=false)
    {
        var doc=new OcrDocument([
            new("brand","ToDesk",new Box(20,20,100,25),.99f,"test","original"),
            new("code",code,new Box(100,100,180,25),.99f,"test","original"),
            new("pwd",password,new Box(400,100,100,25),.99f,"test","original")
        ],800,400,"test","original");
        var result=new RecognitionEngine().Analyze(doc);
        return manual?result with{Decision=ConnectionDecision.Manual,Reason="test ambiguity"}:result;
    }
    public static async Task<int> RunAsync()
    {
        int pass=0,fail=0;
        async Task Test(string name,Func<Task> action){try{await action();pass++;Console.WriteLine("PASS "+name);}catch(Exception ex){fail++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}
        await Test("30 consecutive customers connect exactly once with matching fields",async()=>{
            var expected=new Dictionary<string,RecognitionResult>();
            var calls=new List<(string,string)>();
            await using var controller=new AssistantController((image,_,_)=>Task.FromResult(expected[Convert.ToHexString(SHA256.HashData(image))]),
                (_,code,pwd,_)=>{calls.Add((code,pwd));return Task.FromResult(ConnectResult.Confirm());});
            for(int i=1;i<=30;i++)
            {
                var image=Image(i);var code=(123456700+i).ToString();var pwd="Ab"+i.ToString("D4");
                expected.Add(Convert.ToHexString(SHA256.HashData(image)),Result(code,pwd));
                await controller.RecognizeAsync(image);await controller.RecognizeAsync(image);
                Check(calls.Count==i,"Duplicate automatic attempt");Check(calls[^1]==(code,pwd),"Mixed customer fields");
                controller.ForgetFinished(controller.SessionId);Check(controller.Image==null&&controller.Result==null,"Terminal did not clear view data");
            }
        });
        await Test("same image after a different customer still requires explicit retry",async()=>{
            int calls=0;
            await using var c=new AssistantController((_,_,_)=>Task.FromResult(Result()),(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Confirm());});
            await c.RecognizeAsync(Image(1));await c.RecognizeAsync(Image(2));await c.RecognizeAsync(Image(1));
            Check(calls==2&&c.Stage==AssistantStage.NeedInput,"Old image connected automatically again");
            await c.ConnectAsync(RemoteApp.ToDesk,"123456789","Ab1234");Check(calls==3,"Explicit retry unavailable");
        });
        await Test("ambiguous recognition never invokes connector",async()=>{
            int calls=0;
            await using var c=new AssistantController((_,_,_)=>Task.FromResult(Result(manual:true)),(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Confirm());});
            await c.RecognizeAsync(Image(1));Check(calls==0&&c.Stage==AssistantStage.NeedInput,"Safety policy changed");
        });
        await Test("auto-connect preference off remains off",async()=>{
            int calls=0;
            await using var c=new AssistantController((_,_,_)=>Task.FromResult(Result()),(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Confirm());}){AutoConnect=false};
            await c.RecognizeAsync(Image(1));Check(calls==0,"Disabled auto-connect ignored");
        });
        await Test("failed attempt supports explicit edited retry without OCR",async()=>{
            int reads=0,calls=0;string sent="";
            await using var c=new AssistantController((_,_,_)=>{reads++;return Task.FromResult(Result());},(_,_,pwd,_)=>{calls++;sent=pwd;return Task.FromResult(calls==1?ConnectResult.Fail("mock failure"):ConnectResult.Confirm());});
            await c.RecognizeAsync(Image(1));Check(c.Stage==AssistantStage.Failed,"Failure lost");
            await c.ConnectAsync(RemoteApp.ToDesk,"123456789","aB0  ");Check(reads==1&&calls==2&&sent=="aB0  ","Retry re-read OCR or altered password");
        });
        await Test("late old OCR ignores cancellation but cannot publish or connect",async()=>{
            var release=new TaskCompletionSource();Action<string>? late=null;int reads=0,calls=0;
            await using var c=new AssistantController(async(_,_,progress)=>{int n=++reads;if(n==1){late=progress;await release.Task;}return Result(n==1?"123456789":"987654321");},
                (_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Confirm());});
            var first=c.RecognizeAsync(Image(1));await Task.Delay(10);
            var second=c.RecognizeAsync(Image(2));late?.Invoke("OLD CUSTOMER");Check(c.Status!="OLD CUSTOMER","Old progress published");
            release.SetResult();await Task.WhenAll(first,second);Check(calls==1&&c.Result?.DeviceCode=="987654321","Stale OCR connected or overwrote");
        });
        await Test("manual reset clears old image and waits for cancellation cleanup",async()=>{
            var entered=new TaskCompletionSource();bool cleaned=false;
            await using var c=new AssistantController((_,_,_)=>Task.FromResult(Result()),async(_,_,_,token)=>{entered.SetResult();try{await Task.Delay(Timeout.Infinite,token);}finally{await Task.Delay(30);cleaned=true;}return ConnectResult.Cancel();});
            var first=c.RecognizeAsync(Image(1));await entered.Task;await c.NewManualAsync();await first;
            Check(cleaned&&c.Stage==AssistantStage.NeedInput&&c.Result==null&&c.Image==null,"Manual reset leaked or overlapped");
        });
        await Test("in-flight double-click does not cancel or restart connector",async()=>{
            int calls=0;var entered=new TaskCompletionSource();var finish=new TaskCompletionSource();
            await using var c=new AssistantController((_,_,_)=>Task.FromResult(Result()),async(_,_,_,_)=>{calls++;entered.SetResult();await finish.Task;return ConnectResult.Confirm();});
            var work=c.RecognizeAsync(Image(1));await entered.Task;await c.ConnectAsync(RemoteApp.ToDesk,"987654321","wrong");
            finish.SetResult();await work;Check(calls==1,"Double-click restarted connection");
        });
        await Test("invalid image remains recoverable and never invokes OCR or driver",async()=>{
            int reads=0,calls=0;
            await using var c=new AssistantController((_,_,_)=>{reads++;return Task.FromResult(Result());},(_,_,_,_)=>{calls++;return Task.FromResult(ConnectResult.Confirm());});
            await c.RecognizeAsync([1,2,3]);Check(c.Stage==AssistantStage.Failed&&reads==0&&calls==0,"Invalid image leaked into OCR");
            await c.NewManualAsync();await c.ConnectAsync(RemoteApp.ToDesk,"123456789","");Check(calls==1,"Cannot recover with manual entry");
        });
        Console.WriteLine($"STATE RESULT {pass} passed, {fail} failed");return fail==0?0:1;
    }
}
