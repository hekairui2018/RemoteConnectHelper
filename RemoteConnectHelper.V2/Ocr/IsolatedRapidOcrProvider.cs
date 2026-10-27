using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
namespace RemoteConnectHelper.V2.Ocr;

internal sealed record OcrWorkerRequest(string Kind,byte[] Image,int Rotation=0,bool Contrast=false);
internal sealed record OcrWorkerResponse(OcrDocument? Document=null,int Rotation=0,string? Error=null);

/// <summary>Keep native inference outside the desktop process. Payloads stay in anonymous pipes.</summary>
public sealed class IsolatedRapidOcrProvider : IOcrProvider
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private Process? _process;
    private bool _disposed;
    internal int? WorkerId=>_process is {HasExited:false} process?process.Id:null;
    internal string? TestWorkerArgument {get;init;}
    internal TimeSpan RequestTimeout {get;init;}=TimeSpan.FromSeconds(60);
    public string Name=>"RapidOCR · PP-OCRv5 中文（独立进程）";
    public async Task<OcrDocument> ReadAsync(byte[] image,int rotation,bool contrast,CancellationToken token)
        =>(await SendAsync(new("read",image,rotation,contrast),token)).Document??throw new InvalidDataException("OCR进程未返回文字结果");
    public async Task<int> DetectOrientationAsync(byte[] image,CancellationToken token)
        =>(await SendAsync(new("orientation",image),token)).Rotation;
    private Process EnsureWorker()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(_process is {HasExited:false})return _process;
        ResetWorker();
        var start=new ProcessStartInfo(Environment.ProcessPath??throw new InvalidOperationException("无法定位识别程序")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false),
            WorkingDirectory=AppContext.BaseDirectory
        };
        if(System.IO.Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(IsolatedRapidOcrProvider).Assembly.Location);
        start.ArgumentList.Add(TestWorkerArgument??"--ocr-worker");
        if(TestWorkerArgument==null)start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var process=new Process{StartInfo=start};
        if(!process.Start()){process.Dispose();throw new InvalidOperationException("无法启动OCR进程");}
        _process=process;
        _=DrainErrorsAsync(process);
        Log.Write($"OCR工作进程启动 pid={process.Id}");
        return process;
    }
    private static async Task DrainErrorsAsync(Process process)
    {
        // Native libraries can write diagnostics. Drain without retaining image/recognized text.
        try{var buffer=new char[2048];while(await process.StandardError.ReadAsync(buffer.AsMemory())>0){}}catch{}
    }
    private async Task<OcrWorkerResponse> SendAsync(OcrWorkerRequest request,CancellationToken token)
    {
        if(request.Image.Length is 0 or >64*1024*1024)throw new InvalidDataException("图片为空或超过64MB，请缩小截图后重试");
        await _gate.WaitAsync(token);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        Process? process=null;
        try{
            process=EnsureWorker();
            Log.Write($"OCR请求开始 kind={request.Kind} bytes={request.Image.Length} rotation={request.Rotation}");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            string? line=await process.StandardOutput.ReadLineAsync(timeout.Token);
            if(line==null)throw new IOException("OCR工作进程已退出");
            var response=JsonSerializer.Deserialize<OcrWorkerResponse>(line)??throw new InvalidDataException("OCR进程返回无效结果");
            if(response.Error!=null)throw new InvalidOperationException("OCR工作进程异常："+response.Error);
            Log.Write($"OCR请求完成 kind={request.Kind} blocks={response.Document?.Blocks.Count??0}");
            return response;
        }catch(OperationCanceledException) when(!token.IsCancellationRequested){
            Log.Write("OCR工作进程超时，结束当前识别进程");ResetWorker();throw new TimeoutException("PP-OCR识别超时，请重试");
        }catch(Exception ex){
            string exit="running";try{if(process?.HasExited==true)exit=$"0x{unchecked((uint)process.ExitCode):X8}";}catch{}
            Log.Write($"OCR工作进程失败 kind={request.Kind} type={ex.GetType().Name} exit={exit}");
            ResetWorker();throw;
        }finally{_gate.Release();}
    }
    private void ResetWorker()
    {
        var process=_process;_process=null;if(process==null)return;
        try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch{}
        process.Dispose();
    }
    public void Dispose(){_disposed=true;ResetWorker();}
}

public static class OcrWorker
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]private static extern uint SetErrorMode(uint mode);
    public static int Run(string? fixture=null,int parentPid=0)
    {
        // Called before Application, clipboard listeners, tray, hotkeys and real connectors are created.
        SetErrorMode(3); // Suppress native fault/error dialogs in this worker only.
        using var parent=parentPid>0?Process.GetProcessById(parentPid):null;
        using var parentMonitor=parent==null?null:new System.Threading.Timer(_=>{
            try{if(parent.HasExited)Environment.Exit(74);}catch{Environment.Exit(74);}
        },null,1000,1000);
        using var input=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false));
        using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
        using var engine=new RapidOcrProvider();
        while(input.ReadLine() is string line){
            if(fixture=="exit")return 73;
            if(fixture=="hang"){Thread.Sleep(Timeout.Infinite);return 0;}
            OcrWorkerResponse response;
            try{
                var request=JsonSerializer.Deserialize<OcrWorkerRequest>(line)??throw new InvalidDataException();
                if(request.Image.Length is 0 or >64*1024*1024)throw new InvalidDataException();
                response=request.Kind switch{
                    "read"=>new(engine.ReadAsync(request.Image,request.Rotation,request.Contrast,CancellationToken.None).GetAwaiter().GetResult()),
                    "orientation"=>new(Rotation:engine.DetectOrientationAsync(request.Image,CancellationToken.None).GetAwaiter().GetResult()),
                    _=>throw new InvalidDataException()
                };
            }catch(Exception ex){response=new(Error:ex.GetType().Name);}
            output.WriteLine(JsonSerializer.Serialize(response));
        }
        return 0;
    }
}
