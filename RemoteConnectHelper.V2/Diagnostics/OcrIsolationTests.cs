using System.Diagnostics;
using System.Text.Json;
using RemoteConnectHelper.V2.Ocr;
namespace RemoteConnectHelper.V2.Diagnostics;
public static class OcrIsolationTests
{
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();int failures=0;
        void Check(bool ok,string name){rows.Add(new{Name=name,Passed=ok});if(!ok)throw new InvalidOperationException(name);}
        byte[] image;
        using(var bitmap=new System.Drawing.Bitmap(1000,400)){
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap)){
                graphics.Clear(System.Drawing.Color.White);
                using var font=new System.Drawing.Font("Microsoft YaHei",34);
                graphics.DrawString("ToDesk",font,System.Drawing.Brushes.Black,30,20);
                graphics.DrawString("设备代码 123 456 789",font,System.Drawing.Brushes.Black,30,110);
                graphics.DrawString("临时密码 Ab123456",font,System.Drawing.Brushes.Black,30,210);
            }
            using var stream=new MemoryStream();bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);image=stream.ToArray();
        }
        async Task Case(string name,Func<Task> test){try{await test();}catch(Exception ex){rows.Add(new{Name=name,Passed=false,Error=ex.GetType().Name+": "+ex.Message});failures++;}}
        await Case("Real OCR worker",async()=>{
            using var provider=new IsolatedRapidOcrProvider();
            var document=await provider.ReadAsync(image,0,false,CancellationToken.None);
            File.WriteAllText(Path.Combine(output,"synthetic-worker-result.json"),JsonSerializer.Serialize(document));
            Check(document.Width==1000&&document.Height==400&&string.Concat(document.Blocks.Select(b=>b.Text)).Contains("123"),"Real PP-OCRv5 returns text and original geometry through pipes");
            int? pid=provider.WorkerId;Check(pid.HasValue&&pid!=Environment.ProcessId,"Inference runs outside caller process");
            for(int i=0;i<5;i++)await provider.ReadAsync(image,0,false,CancellationToken.None);
            Check(provider.WorkerId==pid,"Repeated recognition reuses the isolated engine");
            provider.Dispose();bool gone;try{using var process=Process.GetProcessById(pid!.Value);gone=process.WaitForExit(3000);}catch(ArgumentException){gone=true;}
            Check(gone,"Dispose ends only the owned OCR worker");
        });
        await Case("Unexpected process exit",async()=>{
            using var provider=new IsolatedRapidOcrProvider{TestWorkerArgument="--ocr-worker-test-exit"};
            bool failed=false;try{await provider.ReadAsync(image,0,false,CancellationToken.None);}catch(IOException){failed=true;}
            Check(failed&&provider.WorkerId==null,"Unexpected worker exit is caught without terminating caller");
        });
        await Case("Cancellation",async()=>{
            using var provider=new IsolatedRapidOcrProvider{TestWorkerArgument="--ocr-worker-test-hang"};
            using var cancel=new CancellationTokenSource(1000);
            bool cancelled=false;try{await provider.ReadAsync(image,0,false,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&provider.WorkerId==null,"Cancelling blocked inference kills worker and preserves caller");
        });
        await Case("Timeout",async()=>{
            using var provider=new IsolatedRapidOcrProvider{TestWorkerArgument="--ocr-worker-test-hang",RequestTimeout=TimeSpan.FromMilliseconds(500)};
            bool timedOut=false;try{await provider.ReadAsync(image,0,false,CancellationToken.None);}catch(TimeoutException){timedOut=true;}
            Check(timedOut&&provider.WorkerId==null,"Timed-out inference is stopped and caller stays available");
        });
        await Case("Native-like termination",async()=>{
            using var provider=new IsolatedRapidOcrProvider{TestWorkerArgument="--ocr-worker-test-hang"};
            var read=provider.ReadAsync(image,0,false,CancellationToken.None);
            for(int i=0;i<100&&provider.WorkerId==null;i++)await Task.Delay(20);
            int? pid=provider.WorkerId;Check(pid.HasValue,"Failure fixture has separate worker");
            using(var process=Process.GetProcessById(pid!.Value)){process.Kill();await process.WaitForExitAsync();}
            bool failed=false;try{await read;}catch(IOException){failed=true;}
            Check(failed&&provider.WorkerId==null,"Force-killed inference cannot kill desktop caller");
        });
        await Case("PP-OCR failure is explicit without alternate engine",async()=>{
            using var pipeline=new RecognitionPipeline(new IsolatedRapidOcrProvider{TestWorkerArgument="--ocr-worker-test-exit"});
            bool failed=false;try{await pipeline.AnalyzeAsync(image,CancellationToken.None);}catch(InvalidOperationException ex){failed=ex.Message.Contains("PP-OCR");}
            Check(failed,"PP-OCR worker failure must report unavailable instead of switching engines");
        });
        File.WriteAllText(Path.Combine(output,"OCR隔离验收.json"),JsonSerializer.Serialize(new{Passed=failures==0,Tests=rows},new JsonSerializerOptions{WriteIndented=true}));
        return failures==0?0:1;
    }
}
