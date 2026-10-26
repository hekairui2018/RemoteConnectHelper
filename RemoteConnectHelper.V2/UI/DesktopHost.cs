using System.Diagnostics;
using RemoteConnectHelper.V2.Connections;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.Discovery;

namespace RemoteConnectHelper.V2.UI;

/// <summary>Explicit UI triggers only; background clipboard never overwrites an editing/busy session.</summary>
public sealed class DesktopHost
{
    private readonly AssistantController _controller;
    private readonly Preferences _preferences;
    private long _inputRevision;
    private long _explicitFallbackRevision=-1;
    private CancellationTokenSource? _reading;
    private CancellationTokenSource? _orienting;
    private uint _lastSequence;
    private long _backgroundRevision;
    private long _clipboardReadRevision;
    private ClipboardSnapshot? _cachedClipboard;
    private readonly List<Task> _reads=new();
    private System.Windows.Window? _help;
    private SoftwareManagerWindow? _softwareWindow;
    private readonly SoftwareCatalog _software;
    private readonly Func<Func<bool>,CancellationToken,Task<ClipboardSnapshot?>> _readClipboard;
    private readonly DispatcherTimer _sessionTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private ManualSessionTracker _sessionTracker=new();
    private readonly Func<Task<IReadOnlyList<ClientSessionSample>>>? _sampleSessions;
    private Task<IReadOnlyList<ClientSessionSample>>? _sampling;
    private bool _polling;private bool _shuttingDown;private long _visibilityRevision;
    public ConnectionCard Card {get;}
    public bool PendingClipboard {get;private set;}
    public event Action? StateChanged;
    public DesktopHost(AssistantController controller,Preferences preferences,
        Func<Func<bool>,CancellationToken,Task<ClipboardSnapshot?>>? readClipboard=null,
        Func<Task<IReadOnlyList<ClientSessionSample>>>? sampleSessions=null)
    {
        _controller=controller;_preferences=preferences;_controller.AutoConnect=preferences.AutoConnect;
        _readClipboard=readClipboard??ClipboardReader.ReadAsync;
        _software=new(preferences,readClipboard==null?preferences.Save:()=>{});_controller.SoftwareAllowed=_software.Contains;
        Card=new(controller,_software);
        _sampleSessions=sampleSessions??(readClipboard==null?()=>{var result=_controller.Result;return Task.Run(()=>_controller.HasAttemptedConnection&&result!=null&&result.App!=Domain.RemoteApp.Unknown?ManualSessionTracker.CaptureFor(result.App,result.DeviceCode):ManualSessionTracker.Capture());}:null);
        _sessionTimer.Tick+=async(_,_)=>await PollSessionAsync();
        Card.IsVisibleChanged+=(_,_)=>{_visibilityRevision++;_sessionTracker=new();if(Card.IsVisible&&_sampleSessions!=null&&!_shuttingDown)_sessionTimer.Start();else _sessionTimer.Stop();};
        Card.RecognizeAndConnectRequested+=()=>_=ReadAndConnectAsync();
        Card.PreviewCleared+=()=>{_cachedClipboard=null;_orienting?.Cancel();Interlocked.Increment(ref _backgroundRevision);PendingClipboard=false;};
        Card.RecognizeRequested+=()=>_=ReadClipboardAsync(false,true);
        Card.ReadRequested+=()=>_=ReadClipboardAsync(false);
        Card.OpenRequested+=()=>_=OpenFileAsync();
        Card.ManualRequested+=()=>_=ManualAsync();
        Card.FileDropped+=path=>_=ReadFileAsync(path);
        Card.CopiedText+=()=>_lastSequence=ClipboardReader.GetClipboardSequenceNumber();
        controller.Changed+=()=>StateChanged?.Invoke();
    }
    public async Task RefreshRunningSoftwareAsync()
    {
        try{
            var running=await Task.Run(RemoteSoftwareDiscovery.DiscoverRunning);
            await Card.Dispatcher.InvokeAsync(()=>{
                if(_shuttingDown)return;
                int added=0,updated=0;
                foreach(var software in running)try{
                    string change=_software.MergeDiscovered(software);
                    if(change=="added")added++;else if(change=="updated")updated++;
                }catch(Exception ex){Log.Write("后台软件发现未完成 "+ex.GetType().Name);}
                Log.Write($"启动后台发现：已运行{running.Length}款，新增{added}款，更新路径{updated}款");
            });
        }catch(Exception ex){Log.Write("启动后台软件发现失败 "+ex.GetType().Name);}
    }
    public void SetEntryPlacement(System.Windows.Window? ball)=>Card.SetEntryPlacement(ball);
    public bool AcceptBackgroundImage => _preferences.WatchClipboard && _preferences.ClipboardConsent
        && !_controller.Busy && (!Card.DraftIsDirty||!Card.IsKeyboardFocusWithin);
    public bool ProcessClipboardNotification(bool hasImage)
    {
        if(!_preferences.WatchClipboard||!hasImage){PendingClipboard=false;StateChanged?.Invoke();return false;}
        if(!AcceptBackgroundImage){PendingClipboard=true;StateChanged?.Invoke();return false;}
        return true;
    }
    public Task ReadClipboardAsync(bool background,bool recognizeOnly=false,bool useDisplayed=true,bool forceRecognize=false)
    {
        _reads.RemoveAll(t=>t.IsCompleted);var task=ReadClipboardCoreAsync(background,recognizeOnly,useDisplayed,forceRecognize);_reads.Add(task);return task;
    }
    public async Task ShutdownAsync()
    {
        _shuttingDown=true;_visibilityRevision++;_sessionTimer.Stop();
        _orienting?.Cancel();Interlocked.Increment(ref _backgroundRevision);_cachedClipboard=null;Interlocked.Increment(ref _inputRevision);_reading?.Cancel();
        try{await Task.WhenAll(_reads.ToArray());}catch(OperationCanceledException){}
        _reads.Clear();_help?.Close();_softwareWindow?.Close();
    }
    internal async Task PollSessionAsync()
    {
        if(_polling||_shuttingDown||!Card.IsVisible||_sampleSessions==null||_controller.Stage==AssistantStage.Confirmed)return;
        if(_sampling is {IsCompleted:false})return;
        _polling=true;long visibility=_visibilityRevision,session=_controller.SessionId,revision=_controller.Revision;
        try{
            _sampling=_sampleSessions();var samples=await _sampling.WaitAsync(TimeSpan.FromSeconds(2));
            if(_shuttingDown||visibility!=_visibilityRevision||session!=_controller.SessionId||revision!=_controller.Revision||!Card.IsVisible)return;
            var app=_sessionTracker.Observe(samples);
            if(app.HasValue)_controller.ConfirmObservedSession(session,revision,app.Value);
            else if(!samples.Any(s=>s.Sample.ExplicitConnected&&s.Sample.SessionControl&&!s.Sample.Loading)){
                foreach(var pending in samples.OrderByDescending(s=>s.Sample.Failure))
                    if(_controller.ApplyObservedConnectionState(session,revision,pending.App,pending.Sample))break;
            }
        }catch(TimeoutException){}catch(Exception ex){Log.Write("连接状态检查未完成 "+ex.GetType().Name);}
        finally{_polling=false;}
    }
    public async Task ReadAndConnectAsync(bool useDisplayed=true){
        if(_controller.Busy){Log.Write("明确识别并连接：当前操作忙，忽略重复点击");return;}
        if(useDisplayed&&Card.DraftIsDirty&&Card.CanConnectFilled){await Card.ConnectFilledAsync();return;}
        long session=_controller.SessionId;
        var reading=ReadClipboardAsync(false,true,useDisplayed,forceRecognize:true);long revision=_inputRevision;await reading;
        if(revision!=_inputRevision||_controller.Busy)return;
        if(_controller.SessionId>session&&_controller.Stage is AssistantStage.Cancelled or AssistantStage.Failed)return;
        bool refreshed=_controller.SessionId>session&&_controller.Result!=null;
        if(!refreshed&&_explicitFallbackRevision!=revision)return;
        if(!Card.CanConnectFilled){Log.Write("明确识别并连接：没有可连接的完整软件和设备码");return;}
        Log.Write($"明确识别并连接：使用已选字段 app={_controller.Result?.App} decision={_controller.Result?.Decision}");
        await Card.ConnectFilledAsync();
    }
    private async Task CacheClipboardAsync()
    {
        if(!_preferences.WatchClipboard||!_preferences.ClipboardConsent)return;
        long captureRevision=Interlocked.Increment(ref _clipboardReadRevision),startRevision=_backgroundRevision;
        var snap=await _readClipboard(()=>captureRevision==_clipboardReadRevision&&startRevision==_backgroundRevision,CancellationToken.None);
        if(snap==null||captureRevision!=_clipboardReadRevision||startRevision!=_backgroundRevision||!_preferences.WatchClipboard)return;
        if(snap.Sequence==_lastSequence)return;_lastSequence=snap.Sequence;
        _orienting?.Cancel();long revision=Interlocked.Increment(ref _backgroundRevision);
        if(snap.Image==null&&snap.Text==null)return;
        _cachedClipboard=snap;PendingClipboard=snap.Image!=null||!string.IsNullOrWhiteSpace(snap.Text);StateChanged?.Invoke();
        if(PendingClipboard&&AcceptBackgroundImage){
            if(snap.Image==null&&snap.Text!=null){
                var preview=TextPreview.Create(snap.Text);
                if(revision!=_backgroundRevision||!AcceptBackgroundImage)return;
                await _controller.PreviewClipboardAsync(preview.Image,snap.Text);
                if(preview.Result!=null)_controller.ApplyPreviewResult(preview.Image,preview.Result,_controller.Revision);
                if(Card.IsVisible)Card.ShowClipboardPending();return;
            }
            await _controller.PreviewClipboardAsync(snap.Image);if(Card.IsVisible)Card.ShowClipboardPending();
            if(snap.Image!=null){
                using var source=new CancellationTokenSource();_orienting=source;long sessionRevision=_controller.Revision;
                try {
                    int rotation=await _controller.DetectOrientationAsync(snap.Image,source.Token);
                    if(revision==_backgroundRevision&&AcceptBackgroundImage&&!source.IsCancellationRequested)
                        _controller.ApplyPreviewOrientation(snap.Image,rotation,sessionRevision);
                    if(revision!=_backgroundRevision||!AcceptBackgroundImage||source.IsCancellationRequested)return;
                    var result=await _controller.AnalyzePreviewAsync(snap.Image,source.Token);
                    if(revision==_backgroundRevision&&AcceptBackgroundImage&&!source.IsCancellationRequested)
                        _controller.ApplyPreviewResult(snap.Image,result,sessionRevision);
                }finally{if(ReferenceEquals(_orienting,source))_orienting=null;}
            }
        }
    }
    private async Task ReadClipboardCoreAsync(bool background,bool recognizeOnly,bool useDisplayed,bool forceRecognize)
    {
        if(background){try{await CacheClipboardAsync();}catch(OperationCanceledException){}catch{}return;}
        _orienting?.Cancel();Interlocked.Increment(ref _backgroundRevision);Card.PreserveVisiblePosition();Card.BringForwardOnce();
        _reading?.Cancel();var source=new CancellationTokenSource();_reading=source;
        long revision=Interlocked.Increment(ref _inputRevision);
        try
        {
            // The fill button acts on the displayed/marked image, even if the clipboard has since changed.
            var displayed=recognizeOnly&&useDisplayed?_controller.Image:null;
            var prepared=displayed!=null&&!forceRecognize?Card.MarkedPreviewResult:null;
            var snap=displayed!=null?new ClipboardSnapshot(_lastSequence,displayed,_controller.PreviewText)
                :await _readClipboard(()=>revision==_inputRevision,source.Token);
            if(revision!=_inputRevision)return;
            if(snap==null&&forceRecognize){
                _explicitFallbackRevision=revision;
                if(_controller.Image!=null)snap=new ClipboardSnapshot(_lastSequence,_controller.Image,_controller.PreviewText);
                else if(Card.CanConnectFilled){Log.Write("明确识别并连接：剪贴板无内容，沿用当前已填信息重试");return;}
            }
            if(snap==null){_controller.ShowMessage("剪贴板中没有可读取的图片或远程信息，请先复制截图");Card.RevealCurrent();return;}
            _lastSequence=snap.Sequence;
            if(snap.Text!=null)
            {
                var textPreview=prepared==null?TextPreview.Create(snap.Text):null;
                var result=prepared??textPreview?.Result;
                if(result==null){if(!background){_controller.ShowMessage("没有找到远程图片或明确格式的远程文本");Card.RevealCurrent();}return;}
                PendingClipboard=false;await _controller.AcceptTextAsync(snap.Text,result,recognizeOnly?false:true,displayed??textPreview?.Image,refill:recognizeOnly,explicitRetry:!recognizeOnly);return;
            }
            if(snap.Image!=null)
            {
                PendingClipboard=false;
                await _controller.RecognizeAsync(snap.Image,prepared,recognizeOnly?false:true,refill:recognizeOnly,explicitRetry:!recognizeOnly);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception ex){if(!background){_controller.ShowMessage("读取未完成："+ex.GetType().Name);Card.RevealCurrent();}}
        finally{if(ReferenceEquals(_reading,source))_reading=null;source.Dispose();}
    }
    public async Task OpenFileAsync()
    {
        string? path;
        try{path=ImageFilePicker.Show(Card,_preferences);}finally{_preferences.Save();}
        if(path!=null)await ReadFileAsync(path);
    }
    private void MarkPending(bool relevant)
    {
        PendingClipboard=relevant;StateChanged?.Invoke();
        if(relevant&&Card.IsVisible)Card.ShowClipboardPending();
    }
    public async Task ReadFileAsync(string path)
    {
        _orienting?.Cancel();Interlocked.Increment(ref _backgroundRevision);Card.BringForwardOnce();
        _reading?.Cancel();long revision=Interlocked.Increment(ref _inputRevision);
        try{var bytes=await File.ReadAllBytesAsync(path);if(revision!=_inputRevision)return;PendingClipboard=false;await _controller.RecognizeAsync(bytes);if(revision==_inputRevision)Card.RevealCurrent();}
        catch(Exception ex){if(revision==_inputRevision){_controller.ShowMessage("图片未能打开："+ex.Message);Card.RevealCurrent();}}
    }
    public async Task ManualAsync()
    {
        _orienting?.Cancel();Interlocked.Increment(ref _backgroundRevision);_reading?.Cancel();Interlocked.Increment(ref _inputRevision);await _controller.NewManualAsync();Card.EnterEditor();
    }
    public bool MainVisible=>Card.IsVisible&&Card.WindowState!=WindowState.Minimized;
    public void SetMainVisible(bool visible){if(visible)OpenCurrent();else Card.HideToTray();}
    public void OpenCurrent(){Card.PreserveVisiblePosition();Card.EnterEditor();}
    public Task StopAsync()=>_controller.StopAsync();
    public void ApplySettings(){_controller.AutoConnect=_preferences.AutoConnect;if(!_preferences.WatchClipboard){_reading?.Cancel();_orienting?.Cancel();Interlocked.Increment(ref _backgroundRevision);}_preferences.Save();}
    public void OpenLogs()
    {Directory.CreateDirectory(Log.Root);Process.Start(new ProcessStartInfo("explorer.exe",Log.Root){UseShellExecute=true});}
    public async void ShowSoftware(){
        if(_softwareWindow!=null){_softwareWindow.Activate();return;}
        var window=new SoftwareManagerWindow(_software);_softwareWindow=window;window.Closed+=(_,_)=>_softwareWindow=null;window.Show();
        foreach(var entry in _software.Entries.Where(e=>e.ConnectorId!=null&&e.Path==null).ToArray()){
            try{var found=await Task.Run(()=>RemoteSoftwareDiscovery.Find(entry.ConnectorId!));if(_softwareWindow==window&&found!=null&&_software.Entries.Any(e=>e.Id==entry.Id))_software.ReplacePath(entry.Id,found.Path);}catch{}
        }
    }
    public void ShowQuickHelp()
    {
        var window=new System.Windows.Window{Title="灰Desk · 快捷操作说明",Width=380,Height=300,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterScreen};
        window.Content=new System.Windows.Controls.TextBlock{Margin=new Thickness(22),TextWrapping=TextWrapping.Wrap,Text="复制内容：监听会预览图片和文字，并框出设备码、验证码；不填输入框、不连接。\n\n单击悬浮球 / Ctrl+Alt+R：随时读取当前剪贴板并识别填码，不受监听开关影响；目标明确时发起连接，有歧义时等待核对。\n识别按钮：把当前图中框出的内容填入输入框，不连接。\n识别并连接 / 输入框内 Enter：已有填写内容时按输入连接；空输入先识别当前预览。\n\n预览：双击全图，滚轮缩放；图片内拖动平移，标题栏和预览底栏拖动窗口。\nEsc：停止本次操作或收回助手。"};window.Show();
    }
    public void ShowHelp()
    {
        if(_help!=null){_help.Activate();return;}
        var window=new System.Windows.Window{Title="灰Desk · 软件说明",Width=440,Height=550,ResizeMode=ResizeMode.NoResize,
            WindowStartupLocation=WindowStartupLocation.CenterScreen,ShowInTaskbar=false,Background=ThemeManager.Brush("ThemeWhite")};
        _help=window;window.Closed+=(_,_)=>_help=null;
        var panel=new System.Windows.Controls.StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new System.Windows.Controls.TextBlock{Text="灰Desk 使用说明",FontSize=23,Foreground=ThemeManager.Brush("Theme33445F")});
        panel.Children.Add(new System.Windows.Controls.TextBlock{Text="实时监听：开启后，复制图片或文字会自动更新预览并框出设备码、密码，不填入、不连接。关闭后不自动读取。\n\n主动读取：点悬浮球或按 Ctrl+Alt+R，随时读取当前剪贴板并识别填码，不受监听开关影响。目标明确且已开启自动连接时才连接。\n\n识别与连接：点「识别」把当前预览框中的内容填入输入框，不连接；点「识别并连接」可先识别再连接；已有填写内容时按输入连接。\n\n预览操作：右键号码框，可设为设备码、密码或复制；选框后 Ctrl+C 复制。双击全图，滚轮缩放；图片内拖动平移，头部和底栏拖动窗口。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,16),LineHeight=19});
        var consent=new System.Windows.Controls.CheckBox{IsChecked=_preferences.ClipboardConsent,Margin=new Thickness(0,8,0,0)};
        consent.Content=new System.Windows.Controls.TextBlock{Text="我已知悉并允许在本机读取剪贴板，不上传内容。",TextWrapping=TextWrapping.Wrap,MaxWidth=350};panel.Children.Add(consent);
        var save=new System.Windows.Controls.Button{Content="保存并关闭",Margin=new Thickness(0,20,0,0),Padding=new Thickness(12,7,12,7)};
        save.Click+=(_,_)=>{_preferences.ClipboardConsent=consent.IsChecked==true;ApplySettings();window.Close();};
        panel.Children.Add(save);window.Content=new System.Windows.Controls.ScrollViewer{Content=panel,VerticalScrollBarVisibility=System.Windows.Controls.ScrollBarVisibility.Auto};window.Show();DesktopWindows.Place(window,DesktopWindows.WorkAreaAtPointer());
    }
}
