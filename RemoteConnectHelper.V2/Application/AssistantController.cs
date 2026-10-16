using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Ocr;

namespace RemoteConnectHelper.V2.Application;

public enum AssistantStage { Ready, Recognizing, Recognized, Connecting, Launched, NeedInput, Cancelled, Failed }

public sealed class AssistantController : IAsyncDisposable
{
    private readonly OperationHost _operations = new();
    private readonly RecognitionPipeline _pipeline = new();
    private readonly ClientConnector _connector = new();
    private long _revision;
    public event Action? Changed;
    public AssistantStage Stage { get; private set; } = AssistantStage.Ready;
    public string Status { get; private set; } = "截图后点「读取截图」，或按 Ctrl+Alt+R";
    public RecognitionResult? Result { get; private set; }
    public byte[]? Image { get; private set; }
    public bool Busy => Stage is AssistantStage.Recognizing or AssistantStage.Connecting;
    public bool AutoConnect { get; set; } = true;

    public Task RecognizeAsync(byte[] image) => ExecuteAsync(async token =>
    {
        using var encoded = SkiaSharp.SKData.CreateCopy(image);
        using var codec = SkiaSharp.SKCodec.Create(encoded);
        if (codec == null) throw new InvalidDataException("这不是可识别的图片");
        if ((long)codec.Info.Width * codec.Info.Height > 50_000_000) throw new InvalidDataException("图片过大，请截取需要识别的区域");
        Image = image; Result = null;
        SetStage(AssistantStage.Recognizing, "正在读取图片中的文字…");
        var result = await _pipeline.AnalyzeAsync(image, token, text => SetStage(AssistantStage.Recognizing, text));
        token.ThrowIfCancellationRequested(); Result = result;
        SetStage(AssistantStage.Recognized, result.Reason);
        Log.Write($"识别完成 engine={result.Document.Engine} blocks={result.Document.Blocks.Count} app={result.App} codePresent={result.DeviceCode.Length > 0} passwordPresent={result.Password.Length > 0} decision={result.Decision}");
        if (AutoConnect && result.Decision == ConnectionDecision.Automatic)
            await ConnectCoreAsync(result.App, result.DeviceCode, result.Password, token);
        else SetStage(AssistantStage.NeedInput, result.Decision == ConnectionDecision.Automatic
            ? "识别完成，可核对或修改后点「连接」" : result.Reason);
    });

    public Task ConnectAsync(RemoteApp app, string code, string password) => ExecuteAsync(token => ConnectCoreAsync(app, code, password, token));

    private async Task ConnectCoreAsync(RemoteApp app, string code, string password, CancellationToken token)
    {
        if (!ConnectionPolicy.ValidCode(app, code)) { SetStage(AssistantStage.NeedInput, "请选择软件并填写完整的数字设备码"); return; }
        token.ThrowIfCancellationRequested();
        SetStage(AssistantStage.Connecting, "正在打开" + (app == RemoteApp.Sunlogin ? "向日葵" : "ToDesk") + "并连接…");
        var result = await _connector.ConnectAsync(app, code, password, token);
        token.ThrowIfCancellationRequested();
        SetStage(result.Status switch
        {
            ConnectStatus.Launched => AssistantStage.Launched,
            ConnectStatus.NeedPassword => AssistantStage.NeedInput,
            ConnectStatus.Cancelled => AssistantStage.Cancelled,
            _ => AssistantStage.Failed
        }, result.Message);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action)
    {
        long revision = Interlocked.Increment(ref _revision);
        try { await _operations.RunAsync(action); }
        catch (OperationCanceledException) { if (revision == _revision) SetStage(AssistantStage.Cancelled, "已停止，可以修改后重试或读取新截图"); }
        catch (Exception ex) { Log.Write("操作失败 " + ex.GetType().Name); if (revision == _revision) SetStage(AssistantStage.Failed, "处理未完成：" + ex.Message); }
    }
    public async Task StopAsync() { long revision = Interlocked.Increment(ref _revision); await _operations.StopAsync(); if(revision == _revision) SetStage(AssistantStage.Cancelled, "已停止，可以修改后重试或读取新截图"); }
    public void ShowMessage(string text) => SetStage(Busy ? Stage : AssistantStage.NeedInput, text);
    private void SetStage(AssistantStage stage, string text) { Stage = stage; Status = text; Changed?.Invoke(); }
    public async ValueTask DisposeAsync() { await _operations.StopAsync(); _pipeline.Dispose(); }
}
