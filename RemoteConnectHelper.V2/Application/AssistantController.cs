using System.Security.Cryptography;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Ocr;

namespace RemoteConnectHelper.V2.Application;
public enum AssistantStage { Ready, Recognizing, Recognized, Connecting, Launched, NeedInput, Cancelled, Failed, Confirmed, Uncertain }

/// <summary>One customer, explicit retries, generation-checked callbacks. No window operations.</summary>
public sealed class AssistantController : IAsyncDisposable
{
    private readonly OperationHost _operations = new();
    private readonly RecognitionPipeline? _pipeline;
    private readonly Func<byte[], CancellationToken, Action<string>?, Task<RecognitionResult>> _analyze;
    private readonly Func<RemoteApp, string, string, CancellationToken, Task<ConnectResult>> _connect;
    private long _revision;
    private string? _imageKey;
    private bool _lastReadOnly;
    private readonly HashSet<string> _attemptedImages = [];
    private readonly Queue<string> _attemptOrder = new();
    public event Action? Changed;
    public AssistantStage Stage { get; private set; } = AssistantStage.Ready;
    public string Status { get; private set; } = "截图后按 Ctrl+Alt+R，或从托盘读取";
    public RecognitionResult? Result { get; private set; }
    public RecognitionResult? PreviewResult {get;private set;}
    public Task<RecognitionResult> AnalyzePreviewAsync(byte[] image,CancellationToken token)=>_analyze(image,token,null);
    public void ApplyPreviewResult(byte[] image,RecognitionResult result,long revision) {
        if(revision!=_revision||!ReferenceEquals(Image,image)||Busy)return;
        PreviewResult=result;Changed?.Invoke();
    }
    public string? PreviewText {get;private set;}
    public byte[]? Image { get; private set; }
    public int PreviewRotation {get;private set;}
    private readonly Func<byte[],CancellationToken,Task<int>> _detectOrientation;
    public Task<int> DetectOrientationAsync(byte[] image,CancellationToken token)=>_detectOrientation(image,token);
    public void ApplyPreviewOrientation(byte[] image,int rotation,long revision) {
        if(revision!=_revision||!ReferenceEquals(Image,image)||Busy)return;
        PreviewRotation=rotation;Changed?.Invoke();
    }
    public long SessionId { get; private set; }
    public long Revision => _revision;
    public bool Busy => Stage is AssistantStage.Recognizing or AssistantStage.Connecting;
    public Func<RemoteApp,bool> SoftwareAllowed {get;set;}=app=>RemoteApps.Supported.Contains(app);
    public bool AutoConnect { get; set; } = true;
    public bool HasAttemptedConnection {get;private set;}
    public int DuplicateTriggers { get; private set; }
    public AssistantController(
        Func<byte[], CancellationToken, Action<string>?, Task<RecognitionResult>>? analyze = null,
        Func<RemoteApp, string, string, CancellationToken, Task<ConnectResult>>? connect = null,
        Func<byte[],CancellationToken,Task<int>>? detectOrientation = null)
    {
        if (analyze == null) { _pipeline = new(); _analyze = (image,token,progress)=>_pipeline.AnalyzeAsync(image,token,progress); }
        else _analyze = analyze;
        _detectOrientation=detectOrientation??((image,token)=>_pipeline?.DetectOrientationAsync(image,token)??Task.FromResult(0));
        _connect = connect ?? new ClientConnector().ConnectAsync;
    }
    public Task<RecognitionResult> PreviewRemoteAsync(byte[] image,CancellationToken token)
        => _pipeline!=null ? _pipeline.AnalyzeAsync(image,token,null,true) : _analyze(image,token,null);
    public Task RecognizeAsync(byte[] image, RecognitionResult? prepared = null, bool? allowAutoConnect = null, bool refill = false, bool explicitRetry=false)
    {
        string key = Convert.ToHexString(SHA256.HashData(image));
        if(explicitRetry){if(Busy)return Task.CompletedTask;_attemptedImages.Remove(key);_imageKey=null;}
        if(key==_imageKey&&_lastReadOnly&&allowAutoConnect!=false){prepared??=Result;}
        else if (!refill && key == _imageKey && Stage != AssistantStage.Ready) { DuplicateTriggers++; return Task.CompletedTask; }
        _lastReadOnly=allowAutoConnect==false;
        _imageKey = key; SessionId++; HasAttemptedConnection=false;
        long revision = Interlocked.Increment(ref _revision);
        Result = null; PreviewResult=null;PreviewText=null; Image = null;
        Publish(revision, AssistantStage.Recognizing, "正在读取新截图…");
        return ExecuteAsync(revision, async token =>
        {
            using var data = SkiaSharp.SKData.CreateCopy(image);
            using var codec = SkiaSharp.SKCodec.Create(data);
            if (codec == null) throw new InvalidDataException("图片无法打开，可以重新截图或手动填写");
            if ((long)codec.Info.Width * codec.Info.Height > 50_000_000) throw new InvalidDataException("图片过大，请截取远程信息区域");
            token.ThrowIfCancellationRequested();
            if (revision != _revision) return;
            Image = image;
            try{PreviewRotation=await DetectOrientationAsync(image,token);}catch(OperationCanceledException){throw;}catch{PreviewRotation=0;}
            token.ThrowIfCancellationRequested();if(revision!=_revision)return;
            Publish(revision, AssistantStage.Recognizing, "正在识别远程信息…");
            var result = prepared ?? await _analyze(image, token, text => Publish(revision, AssistantStage.Recognizing, text));
            token.ThrowIfCancellationRequested();
            if (revision != _revision) return;
            Result = result;
            Log.Write($"识别完成 session={SessionId} app={result.App} decision={result.Decision}");
            if ((allowAutoConnect??AutoConnect) && result.Decision == ConnectionDecision.Automatic && !_attemptedImages.Contains(key))
                await ConnectCoreAsync(revision, result.App, result.DeviceCode, result.Password, token);
            else Publish(revision, AssistantStage.NeedInput,
                allowAutoConnect!=false&&_attemptedImages.Contains(key) ? "这张截图已经发起过连接，需要时请明确点击重试"
                : result.Decision == ConnectionDecision.Automatic ? "识别完成，可以核对后连接" : result.Reason);
        });
    }
    public Task AcceptTextAsync(string text, RecognitionResult result, bool? allowAutoConnect=null,byte[]? previewImage=null,bool refill=false,bool explicitRetry=false)
    {
        string key=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        if(explicitRetry){if(Busy)return Task.CompletedTask;_attemptedImages.Remove(key);_imageKey=null;}
        if(!refill&&key==_imageKey && Stage!=AssistantStage.Ready&&!(_lastReadOnly&&allowAutoConnect!=false)){DuplicateTriggers++;return Task.CompletedTask;}
        _lastReadOnly=allowAutoConnect==false;
        _imageKey=key;SessionId++;HasAttemptedConnection=false;long revision=Interlocked.Increment(ref _revision);
        Result=null;PreviewResult=null;Image=previewImage;PreviewText=text;PreviewRotation=0;Publish(revision,AssistantStage.Recognizing,"正在读取远程文本…");
        return ExecuteAsync(revision,async token=>{
            token.ThrowIfCancellationRequested();if(revision!=_revision)return;Result=result;
            if((allowAutoConnect??AutoConnect) && result.Decision==ConnectionDecision.Automatic && !_attemptedImages.Contains(key))
                await ConnectCoreAsync(revision,result.App,result.DeviceCode,result.Password,token);
            else Publish(revision,AssistantStage.NeedInput,allowAutoConnect!=false&&_attemptedImages.Contains(key)?"此内容已尝试连接，请核对后明确重试":result.Reason);
        });
    }
    public Task ConnectAsync(RemoteApp app, string code, string password)
    {
        if (Busy) return Task.CompletedTask;
        long revision = Interlocked.Increment(ref _revision);
        return ExecuteAsync(revision, token => ConnectCoreAsync(revision, app, code, password, token));
    }
    public Task ConnectAdaptiveAsync(Discovery.SoftwareEntry entry,string code,string password)
    {
        if(Busy)return Task.CompletedTask;
        long revision=Interlocked.Increment(ref _revision);
        return ExecuteAsync(revision,async token=>{
            if(entry.Path==null){Publish(revision,AssistantStage.NeedInput,"请先给所选软件设置启动路径");return;}
            HasAttemptedConnection=true;Publish(revision,AssistantStage.Connecting,"正在自适应定位"+entry.Name+"的连接控件…");
            var result=await AdaptiveClientConnector.ConnectAsync(entry.Path,code.Trim().Replace(" ",""),password,token);
            token.ThrowIfCancellationRequested();
            Publish(revision,result.Status==ConnectStatus.Failed?AssistantStage.Failed:result.Status==ConnectStatus.Cancelled?AssistantStage.Cancelled:AssistantStage.NeedInput,result.Message);
        });
    }
    private async Task ConnectCoreAsync(long revision, RemoteApp app, string code, string password, CancellationToken token)
    {
        if(!SoftwareAllowed(app)){Publish(revision,AssistantStage.NeedInput,"请先在远程软件列表中添加对应软件");return;}
        code = code.Trim().Replace(" ", "");
        if (!ConnectionPolicy.ValidCode(app, code)) { Publish(revision, AssistantStage.NeedInput, "请选择软件并填写完整的数字设备码"); return; }
        token.ThrowIfCancellationRequested();
        if (revision != _revision) return;
        if (_imageKey != null && _attemptedImages.Add(_imageKey))
        {
            _attemptOrder.Enqueue(_imageKey);
            while (_attemptOrder.Count > 64) _attemptedImages.Remove(_attemptOrder.Dequeue());
        }
        HasAttemptedConnection=true;
        Publish(revision, AssistantStage.Connecting, "正在打开" + RemoteApps.Name(app) + "并连接…");
        var result = await _connect(app, code, password, token);
        token.ThrowIfCancellationRequested();
        Publish(revision, result.Status switch {
            ConnectStatus.Confirmed => AssistantStage.Confirmed,
            ConnectStatus.Uncertain or ConnectStatus.Launched => AssistantStage.Uncertain, ConnectStatus.NeedPassword => AssistantStage.NeedInput,
            ConnectStatus.Cancelled => AssistantStage.Cancelled, _ => AssistantStage.Failed
        }, result.Message);
    }
    private async Task ExecuteAsync(long revision, Func<CancellationToken, Task> action)
    {
        try { await _operations.RunAsync(async token => { if (revision == _revision) await action(token); }); }
        catch (OperationCanceledException) { Publish(revision, AssistantStage.Cancelled, "已停止，可以修改后重试"); }
        catch (Exception ex) { Log.Write("操作失败 " + ex.GetType().Name); Publish(revision, AssistantStage.Failed, "处理未完成：" + ex.Message); }
    }
    public async Task StopAsync()
    {
        long revision = Interlocked.Increment(ref _revision);
        await _operations.StopAsync();
        Publish(revision, AssistantStage.Cancelled, "已停止，可以修改后重试");
    }
    public async Task PreviewClipboardAsync(byte[]? image,string? text=null)
    {
        if(Busy)return;
        if(image!=null){using var data=SkiaSharp.SKData.CreateCopy(image);using var codec=SkiaSharp.SKCodec.Create(data);if(codec==null||(long)codec.Info.Width*codec.Info.Height>50_000_000)return;}
        long revision=Interlocked.Increment(ref _revision);await _operations.StopAsync();if(revision!=_revision)return;
        SessionId++;_imageKey=null;_lastReadOnly=false;Result=null;PreviewResult=null;Image=image;PreviewText=text;PreviewRotation=0;HasAttemptedConnection=false;
        Publish(revision,AssistantStage.Ready,"已缓存新内容，点击识别或悬浮球");
    }
    public void ClearPreview(){
        if(Busy)return;Interlocked.Increment(ref _revision);Image=null;PreviewText=null;PreviewResult=null;Changed?.Invoke();
    }
    public async Task NewManualAsync()
    {
        long revision = Interlocked.Increment(ref _revision);
        await _operations.StopAsync();
        if (revision != _revision) return;
        SessionId++; _imageKey = null; Result = null; PreviewResult=null;PreviewText=null; Image = null; HasAttemptedConnection=false;
        Publish(revision, AssistantStage.NeedInput, "填写设备码和验证码；没有验证码可留空");
    }
    public void ForgetFinished(long session)
    {
        if (SessionId != session || Stage != AssistantStage.Confirmed) return;
        Result = null; PreviewResult=null;PreviewText=null; Image = null;
        Publish(_revision, AssistantStage.Confirmed, "已确认连接；界面的截图与密码已清空");
    }
    public bool ConfirmObservedSession(long session,long revision,RemoteApp app)
    {
        if(SessionId!=session||_revision!=revision||Busy||Stage==AssistantStage.Confirmed||!SoftwareAllowed(app))return false;
        Publish(_revision,AssistantStage.Confirmed,"已确认远程软件连接成功");return true;
    }
    internal bool ApplyObservedConnectionState(long session,long revision,RemoteApp app,RemoteConnectHelper.V2.Connections.SessionSample sample)
    {
        if(SessionId!=session||_revision!=revision||Busy||Stage==AssistantStage.Confirmed||!HasAttemptedConnection||Result?.App!=app||!sample.TargetMatches)return false;
        var stage=sample.Failure?AssistantStage.Failed:sample.Verification?AssistantStage.NeedInput:(AssistantStage?)null;
        if(stage==null)return false;
        if(Stage!=stage.Value||Status!=sample.Reason)Publish(revision,stage.Value,sample.Reason);
        return true;
    }
    public void ShowMessage(string text) => Publish(_revision, Busy ? Stage : AssistantStage.NeedInput, text);
    private void Publish(long revision, AssistantStage stage, string text)
    { if (revision != _revision) return; if(stage==AssistantStage.Confirmed&&Stage!=stage)Log.Write($"连接成功 session={SessionId}"); Stage = stage; Status = text; Changed?.Invoke(); }
    public async ValueTask DisposeAsync() { Interlocked.Increment(ref _revision); await _operations.StopAsync(); _pipeline?.Dispose(); }
}
