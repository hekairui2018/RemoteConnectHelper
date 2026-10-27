using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.Discovery;
using Button=System.Windows.Controls.Button;
using Window=System.Windows.Window;
using Panel=System.Windows.Controls.Panel;
using Brush=System.Windows.Media.Brush;

namespace RemoteConnectHelper.V2.UI;

public partial class ConnectionCard : Window
{
    private readonly AssistantController _controller;
    private readonly SoftwareCatalog _catalog;
    private sealed record RecentTarget(RemoteApp App,string Code,bool Favorite);
    private readonly List<RecentTarget> _recent=[];

    private readonly DispatcherTimer _finishTimer=new() { Interval=TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _frontTimer=new() { Interval=TimeSpan.FromMilliseconds(800) };
    private long _presentedSession=-1;
    private bool _frontPulseActive;
    private RecognitionResult? _shownResult;
    private long _session=-1, _finishedRevision=-1, _timerSession;
    private bool _previewFieldChosen;
    public bool PreviewExpanded=>_expanded;
    public void RestorePreviewVisibility(bool expanded){_expanded=expanded;ImagePanel.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;UpdatePreviewToggle();ConfigureLayout();}
    private bool _updating, _hiddenByUser, _canClose;
    private bool _expanded=true;
    private RecognitionResult? _shownPreview;
    public RecognitionResult? MarkedPreviewResult {
        get {
            var result=_controller.Result??_controller.PreviewResult;
            return result==null?null:_previewFieldChosen?result with{SelectedCode=_codeMark??result.SelectedCode,SelectedPassword=_passwordMark}:PreviewCandidates.ForFill(result);
        }
    }
    private DeviceCodeCandidate? _codeMark;
    private PasswordCandidate? _passwordMark;
    private IReadOnlyList<PasswordCandidate> _selectionPasswords=[];
    private OriginalImageWindow? _original;
    private Window? _placementBall;
    private System.Windows.Point? _manualPosition;
    private bool _previewFocusMode,_windowSizing;
    private double _compactHeight=500,_expandedHeight=500,_compactWidth=384,_previewWidth=384;
    private readonly DispatcherTimer _singleClickZoom=new(){Interval=TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime+20)};
    private System.Windows.Point _singleClickPoint;private long _singleClickSession;

    private bool _draggingHeader,_draggingPreview,_previewMoved,_focusFieldsPending,_focusedRegion;
    private System.Windows.Point _headerStart,_headerOrigin,_previewStart;
    private double _panStartX,_panStartY;
    public double PreviewZoom=>ImageZoom.ScaleX;
    public Vector PreviewPan=>new(ImagePan.X,ImagePan.Y);
    public void PreserveVisiblePosition()
    {
        if(!IsVisible||WindowState!=WindowState.Normal)return;
        var rect=DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _manualPosition=rect.TopLeft;_placementBall=null;AnchorArea=DesktopWindows.WorkAreaAtPoint(rect.X+rect.Width/2,rect.Y+rect.Height/2);
    }
    public void SetEntryPlacement(Window? ball)
    {
        if(IsVisible&&WindowState!=WindowState.Minimized){PreserveVisiblePosition();return;}
        _placementBall=ball;_manualPosition=null;
        AnchorArea=ball==null?DesktopWindows.WorkAreaAtPointer():DesktopWindows.WorkAreaFor(ball);
    }
    public void CollapseOriginalForEntry()
    {
        _expanded=false;ImagePanel.Visibility=Visibility.Collapsed;UpdatePreviewToggle();ConfigureLayout();
    }
    private void PositionCard()
    {
        if(WindowState!=WindowState.Normal)return;
        Rect? beside=null;
        if(_placementBall is {IsVisible:true} && _manualPosition==null && !(_expanded&&_previewDock=="top"))
        {AnchorArea=DesktopWindows.WorkAreaFor(_placementBall);beside=DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(_placementBall).Handle);}
        DesktopWindows.Place(this,AnchorArea,beside,_manualPosition);
    }
    public void MoveCardTo(System.Windows.Point point)
    {
        AnchorArea=DesktopWindows.WorkAreaAtPoint(point.X+Width*DesktopWindows.DpiScale(this)/2,point.Y+Height*DesktopWindows.DpiScale(this)/2);
        _manualPosition=point;_placementBall=null;DesktopWindows.MoveOnly(this,point);
    }
    public event Action? RecognizeRequested;
    public event Action? RecognizeAndConnectRequested;
    public event Action? PreviewCleared;
    public event Action? ReadRequested;
    public event Action? OpenRequested;
    public event Action? ManualRequested;
    public bool IsEditing {get;private set;}
    public bool DraftIsDirty {get;private set;}
    public Rect AnchorArea {get;set;}
    public int PassiveShows {get;private set;}
    public int ExplicitActivations {get;private set;}
    public int TerminalHides {get;private set;}
    public int ExplicitRaises {get;private set;}
    public int AutomaticPresentations {get;private set;}
    public ConnectionCard(AssistantController controller,SoftwareCatalog? catalog=null)
    {
        _catalog=catalog??new SoftwareCatalog(new Infrastructure.Preferences());ThemeManager.Ensure();InitializeComponent();ThemeManager.Changed+=UpdateWallpaper; _controller=controller;InitializePinnedPreview();_catalog.Changed+=RefreshSoftware; AnchorArea=DesktopWindows.WorkAreaAtPointer();
        StateChanged+=(_,_)=>{MaximizeButton.Content=WindowState==WindowState.Maximized?"❐":"□";if(WindowState==WindowState.Maximized){MaxWidth=double.PositiveInfinity;MaxHeight=double.PositiveInfinity;}Dispatcher.BeginInvoke(new Action(()=>{ConfigureLayout();DrawOverlay();}));};
        IsVisibleChanged+=(_,_)=>UpdateWallpaperPlayback();StateChanged+=(_,_)=>UpdateWallpaperPlayback();
        SizeChanged+=(_,_)=>{UpdateWindowDimensions();UpdatePanelGeometry();UpdatePreviewOpacity();};
        SourceInitialized+=(_,_)=>{DesktopWindows.Passive(this,true);System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(WindowSizingHook);};
        _singleClickZoom.Tick+=(_,_)=>{_singleClickZoom.Stop();if(IsVisible&&_singleClickSession==_controller.SessionId&&SourceImage.Source!=null)ZoomPreview(PreviewZoom*1.35,_singleClickPoint);};
        _finishTimer.Tick+=(_,_)=>FinishIfCurrent();
        _frontTimer.Tick+=(_,_)=>EndFrontPulse();
        _controller.Changed+=OnStateChanged;
        RefreshSoftware();_updating=true;SelectApp(RemoteApp.Sunlogin);_updating=false;
        ApplyState();
    }
    private void OnStateChanged()
    { if(Dispatcher.CheckAccess()) ApplyState(); else Dispatcher.BeginInvoke(new Action(ApplyState)); }
    public void ApplyState()
    {
        if(_session!=_controller.SessionId)
        {
            ReadButton.Content="重新读取";ResetContentTransform();
            _finishTimer.Stop(); _session=_controller.SessionId; _hiddenByUser=false;
            _previewFieldChosen=false;_selectedPreviewField=null;_shownResult=null;_shownPreview=null; _codeMark=null;_passwordMark=null; DraftIsDirty=false;
            _selectionPasswords=[];
            _updating=true;SelectApp(RemoteApp.Sunlogin);CodeBox.Clear();PasswordBox.Clear();_updating=false;
            CodeOptions.Children.Clear();PasswordOptions.Children.Clear();
            SourceImage.Source=null;Overlay.Children.Clear();NoImage.Visibility=Visibility.Visible;
            SourceImage.Tag=null;
            _original?.Close();_original=null;
            ImagePanel.Visibility=_expanded?Visibility.Visible:Visibility.Collapsed;UpdatePreviewToggle();SetCompact();
        }
        if(_controller.Image is {} image && SourceImage.Tag!=image)
        {
            using var stream=new MemoryStream(image);
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
            _rawPreview=bitmap;_displayRotation=-1;SourceImage.Tag=image;NoImage.Visibility=Visibility.Collapsed;
        }
        if(_controller.Image!=null&&_rawPreview!=null&&_displayRotation!=_controller.PreviewRotation){
            _displayRotation=_controller.PreviewRotation;
            var display=new TransformedBitmap(_rawPreview,new RotateTransform(_displayRotation));display.Freeze();SourceImage.Source=display;
            ResetContentTransform();DrawOverlay();
        }
        if(_controller.Result==null&&_controller.PreviewResult is {} preview&&!ReferenceEquals(preview,_shownPreview)){
            _shownPreview=preview;RememberRecognized(preview);_codeMark=preview.SelectedCode;_passwordMark=preview.SelectedPassword;
            _focusFieldsPending=_previewFocusMode;DrawOverlay();
        }
        if(_controller.Result is {} result && !ReferenceEquals(result,_shownResult))
        {
            _shownResult=result;RememberRecognized(result);_updating=true;
            SelectApp(result.App==RemoteApp.Unknown?RemoteApp.Sunlogin:result.App);CodeBox.Text=result.DeviceCode;PasswordBox.Text=result.Password;
            _codeMark=result.SelectedCode;_passwordMark=result.SelectedPassword;
            _selectionPasswords=result.Passwords;_focusFieldsPending=_previewFocusMode;
            _updating=false;DraftIsDirty=false;BuildCodes();BuildPasswords(_codeMark?.Id);DrawOverlay();
        }
        if(_controller.Image==null && _controller.Result==null && _controller.Stage==AssistantStage.Confirmed)
        { ClearSensitive(); }
        UpdateWindowDimensions();
        PreviewEngineText.Text=_controller.PreviewText==null?"文字识别：PP-OCRv5 · RapidOCR":"文字解析：本机直接解析 · 无需 OCR";
        TitleText.Text=_controller.Stage switch {
            AssistantStage.Recognizing=>"正在识别",AssistantStage.Connecting=>"正在连接",
            AssistantStage.NeedInput when _controller.HasAttemptedConnection&&(_controller.Status.Contains("密码")||_controller.Status.Contains("验证码"))=>"等待输入密码",
            AssistantStage.NeedInput when _controller.HasAttemptedConnection&&_controller.Status.Contains("等待")=>"等待对方确认",
            AssistantStage.NeedInput=>"请确认连接信息",AssistantStage.Failed=>"连接失败",
            AssistantStage.Cancelled=>"已停止",AssistantStage.Confirmed=>"已确认连接成功",AssistantStage.Uncertain=>"尚未确认连接",_=>"等待截图"
        };
        SubtitleText.Text=_controller.Result is {} r ? (r.App==RemoteApp.Unknown?"向日葵（待确认）":r.AppName)+" · "+Mask(r.DeviceCode) : "Ctrl+Alt+R 读取当前截图";
        CompactStatus.Text=_controller.Status;RiskText.Text=_controller.Status;
        RiskBox.Visibility=_controller.Stage==AssistantStage.Ready?Visibility.Collapsed:Visibility.Visible;
        RiskText.Visibility=string.IsNullOrWhiteSpace(RiskText.Text)?Visibility.Collapsed:Visibility.Visible;
        RiskText.ToolTip=RiskText.Text;
        PasswordState.Text=PasswordBox.Text.Length>0 ? "按原图核对大小写，易混字符不要猜" :
            _controller.Result?.Warnings.FirstOrDefault(s=>s.Contains("隐藏"))??"未读到密码也允许尝试，需要时补填";
        ConnectionStatus.Text=_controller.Status;
        ConnectionStatus.Visibility=Visibility.Collapsed;
        bool busy=_controller.Busy;PreviewCloseButton.IsEnabled=!busy;
        AppBox.IsEnabled=SoftwarePickerButton.IsEnabled=CodeBox.IsEnabled=PasswordBox.IsEnabled=CodeOptions.IsEnabled=PasswordOptions.IsEnabled=!busy;
        SoftwareName.Text=(AppBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"请选择软件";
        RecognizeButton.IsEnabled=!busy;
        SubtitleText.Visibility=_controller.Result==null?Visibility.Collapsed:Visibility.Visible;
        StopButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
        ConnectButton.Visibility=Visibility.Visible;
        ConnectButton.Content=_controller.Stage==AssistantStage.Connecting?"连接中…":_controller.Stage==AssistantStage.Recognizing?"识别中…":_controller.HasAttemptedConnection?"重新连接 ↵":"识别并连接 ↵";
        UpdateConnect();
        CandidatesPanel.Visibility=Visibility.Collapsed;
        PasswordCandidatesPanel.Visibility=Visibility.Collapsed;
        RefreshCandidateButtons();
        BuildRecent();
        if(_controller.Stage==AssistantStage.Ready || _hiddenByUser) return;
        if(_controller.Stage==AssistantStage.Connecting)EndFrontPulse();
        if(_presentedSession!=_controller.SessionId && _controller.Stage is not (AssistantStage.Connecting or AssistantStage.Confirmed)
            && (_controller.Image!=null||_controller.Result!=null))
        {
            _presentedSession=_controller.SessionId;AutomaticPresentations++;ShowPassive();PulseFront();
        }
        if(_controller.Stage==AssistantStage.Confirmed)
        {
            if(_finishedRevision==_controller.Revision) return;
            _finishedRevision=_controller.Revision;_timerSession=_session;
            SetCompact(); ShowPassive(); _finishTimer.Start(); return;
        }
        _finishTimer.Stop();
        if(!IsVisible) ShowPassive();
    }
    private static string Mask(string code)=>code.Length>4?"••• "+code[^4..]:code;
    public void ShowPassive()
    {
        if(_hiddenByUser)return;
        DesktopWindows.Passive(this,true);ShowActivated=false;
        PositionCard();
        if(!IsVisible){Show();PassiveShows++;}
    }
    public void RevealCurrent()
    {
        _hiddenByUser=false;
        if(_controller.Stage==AssistantStage.Ready) EnterEditor();
        else ShowPassive();
    }
    public void BringForwardOnce()
    {
        _hiddenByUser=false;ShowPassive();PulseFront();ExplicitRaises++;
    }
    private void PulseFront(){_frontTimer.Stop();_frontPulseActive=true;DesktopWindows.BeginFrontPulse(this);_frontTimer.Start();}
    private void EndFrontPulse()
    {
        _frontTimer.Stop();if(!_frontPulseActive)return;
        _frontPulseActive=false;DesktopWindows.EndFrontPulse(this);
    }
    public void EnterEditor()
    {
        _finishTimer.Stop();_timerSession=-1;
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        _hiddenByUser=false;IsEditing=true;ConfigureLayout();
        CompactStatus.Visibility=Visibility.Collapsed;EditorScroll.Visibility=Visibility.Visible;
        ConnectButton.Visibility=Visibility.Visible;
        DesktopWindows.Passive(this,false);PositionCard();
        if(!IsVisible)Show();
        PulseFront();Activate();ExplicitActivations++;if(!_controller.Busy)
        {
            if(_controller.Status.Contains("密码") && _controller.Stage is AssistantStage.NeedInput or AssistantStage.Failed)
            {PasswordBox.Focus();PasswordBox.SelectAll();}
            else {CodeBox.Focus();CodeBox.SelectAll();}
        }
        UpdateConnect();
    }
    public void SetCompact()
    {
        IsEditing=false;ConfigureLayout();
        CompactStatus.Visibility=Visibility.Collapsed;EditorScroll.Visibility=Visibility.Visible;ConnectButton.Visibility=Visibility.Visible;
        DesktopWindows.Passive(this,true);PositionCard();
    }
    public void FinishIfCurrent()
    {
        _finishTimer.Stop();
        if(_controller.Stage!=AssistantStage.Confirmed || _controller.SessionId!=_timerSession)return;
        EndFrontPulse();Hide();TerminalHides++;_hiddenByUser=true;RemoteConnectHelper.V2.Infrastructure.Log.Write("成功后自动收起 session="+_timerSession);
        ClearSensitive();_controller.ForgetFinished(_timerSession);
    }
    private void ClearSensitive()
    {
        _updating=true;CodeBox.Clear();PasswordBox.Clear();SelectApp(RemoteApp.Sunlogin);_updating=false;
        _shownResult=null;_codeMark=null;_passwordMark=null;SourceImage.Source=null;SourceImage.Tag=null;
        _selectionPasswords=[];
        CodeOptions.Children.Clear();PasswordOptions.Children.Clear();Overlay.Children.Clear();
        NoImage.Visibility=Visibility.Visible;_original?.Close();_original=null;DraftIsDirty=false;
    }
    public void HideToTray(){_singleClickZoom.Stop();EndFrontPulse();Hide();_hiddenByUser=true;_original?.Close();_original=null;}
    public void ShowClipboardPending()
    {
        RiskText.Text="已缓存新内容，点识别或悬浮球读取";
        ReadButton.Content="重新读取";
    }
    private readonly Dictionary<TextBlock,string> _wallpaperLabelKeys=[];
    private WallpaperPlayer? _wallpaper;
    public bool HasWallpaper=>_wallpaper!=null;
    public void LoadWallpaper(string? path){
        WallpaperPlayer? next=string.IsNullOrWhiteSpace(path)?null:new WallpaperPlayer(path);
        _wallpaper?.Dispose();_wallpaper=next;
        if(next!=null){next.FrameChanged+=UpdateWallpaper;UpdateWallpaper();}
        else{ShellBackground.SetResourceReference(Border.BackgroundProperty,"ThemeFCFDFF");EditorBackground.SetResourceReference(Border.BackgroundProperty,"ThemeFCFDFF");EditorScroll.Background=System.Windows.Media.Brushes.Transparent;PreviewArea.SetResourceReference(Grid.BackgroundProperty,"ThemeEDF2FA");ImageToolbar.SetResourceReference(DockPanel.BackgroundProperty,"ThemeWhite");PreviewFooter.SetResourceReference(Border.BackgroundProperty,"ThemeWhite");}
        if(next==null){foreach(var pair in _wallpaperLabelKeys)pair.Key.SetResourceReference(TextBlock.ForegroundProperty,pair.Value);_wallpaperLabelKeys.Clear();}
        UpdateWallpaperPlayback();
    }
    private void UpdateWallpaper(){
        if(_wallpaper==null)return;
        // One brush across the complete shell; all controls and the original preview remain above it.
        ShellBackground.Background=new ImageBrush(_wallpaper.Frame){Stretch=Stretch.UniformToFill,AlignmentX=AlignmentX.Center,AlignmentY=AlignmentY.Center};
        EditorBackground.Background=System.Windows.Media.Brushes.Transparent;PreviewArea.Background=System.Windows.Media.Brushes.Transparent;
        EditorScroll.Background=System.Windows.Media.Brushes.Transparent;ImageToolbar.Background=System.Windows.Media.Brushes.Transparent;PreviewFooter.Background=System.Windows.Media.Brushes.Transparent;
        ApplyWallpaperLabelContrast();
    }
    private void ApplyWallpaperLabelContrast(){
        if(_wallpaper==null)return;var frame=_wallpaper.Frame;byte[] sample=new byte[4];
        var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);converted.CopyPixels(new Int32Rect(frame.PixelWidth/2,frame.PixelHeight/2,1,1),sample,4,0);
        double luminance=(sample[2]*299+sample[1]*587+sample[0]*114)/1000d;
        var foreground=new SolidColorBrush(luminance>145?System.Windows.Media.Color.FromRgb(35,49,70):System.Windows.Media.Color.FromRgb(235,241,250));foreground.Freeze();
        void Visit(DependencyObject parent,bool insideControl){
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){
                var child=VisualTreeHelper.GetChild(parent,i);bool control=insideControl||child is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox;
                if(child is TextBlock label&&!control){
                    if(!_wallpaperLabelKeys.ContainsKey(label))_wallpaperLabelKeys[label]=ThemeManager.KeyForBrush(label.Foreground)??"Theme33445F";
                    var color=foreground.Color;
                    if(WindowVisual.ActualWidth>0&&WindowVisual.ActualHeight>0&&label.ActualWidth>0){
                        var point=PointInCard(label,new System.Windows.Point(label.ActualWidth/2,label.ActualHeight/2));
                        double fit=Math.Max(WindowVisual.ActualWidth/frame.PixelWidth,WindowVisual.ActualHeight/frame.PixelHeight);
                        int x=(int)Math.Clamp((point.X-(WindowVisual.ActualWidth-frame.PixelWidth*fit)/2)/fit,0,frame.PixelWidth-1);
                        int y=(int)Math.Clamp((point.Y-(WindowVisual.ActualHeight-frame.PixelHeight*fit)/2)/fit,0,frame.PixelHeight-1);
                        converted.CopyPixels(new Int32Rect(x,y,1,1),sample,4,0);double brightness=(sample[2]*299+sample[1]*587+sample[0]*114)/1000d;
                        color=brightness>145?System.Windows.Media.Color.FromRgb(35,49,70):System.Windows.Media.Color.FromRgb(235,241,250);
                    }
                    label.Foreground=new SolidColorBrush(color);
                }
                Visit(child,control);
            }
        }
        Visit(WindowVisual,false);if(_previewPin!=null)Visit(PreviewVisual,false);
    }
    private void UpdateWindowDimensions(){
        if(WindowSizeText==null)return;double dpi=DesktopWindows.DpiScale(this);
        var dimensions=new Rect(0,0,ActualWidth*dpi,ActualHeight*dpi);
        bool combined=_expanded&&_previewDock!=null&&_previewPin?.IsVisible==true;
        if(combined){
            dimensions=DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            dimensions.Union(DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(_previewPin!).Handle));
        }
        WindowSizeText.Text=$"{Math.Round(dimensions.Width)}×{Math.Round(dimensions.Height)}";
        string scope=combined?"合并后的整体界面":"单区界面";
        WindowSizeText.ToolTip=$"{scope}：{WindowSizeText.Text} 屏幕像素";
        PreviewHeading.Text=$"界面分辨率 {WindowSizeText.Text}";
        PreviewHeading.ToolTip=$"{scope}：{WindowSizeText.Text} 屏幕像素\n左右吸附显示总宽，上下吸附显示总高；分开显示单区尺寸。";
    }
    private void UpdateWallpaperPlayback()=>_wallpaper?.SetPlaying(IsVisible&&WindowState!=WindowState.Minimized);
    public void ShutdownCard(){ClosePinnedPreview();_catalog.Changed-=RefreshSoftware;ThemeManager.Changed-=UpdateWallpaper;_wallpaper?.Dispose();_singleClickZoom.Stop();_canClose=true;_finishTimer.Stop();EndFrontPulse();_controller.Changed-=OnStateChanged;_original?.Close();Close();}
    private bool ValidSelectedCode=>ConnectionPolicy.ValidCode(AppSelection,CodeBox.Text)||(AppSelection==RemoteApp.Unknown&&(AppBox.SelectedItem as ComboBoxItem)?.Tag is SoftwareEntry {Path:not null}&&System.Text.RegularExpressions.Regex.IsMatch(CodeBox.Text.Replace(" ",""),@"^\d{7,12}$"));
    private RemoteApp AppSelection=>(AppBox.SelectedItem as ComboBoxItem)?.Tag is SoftwareEntry entry?entry.App:RemoteApp.Unknown;
    private void SelectApp(RemoteApp app){AppBox.SelectedIndex=AppBox.Items.OfType<ComboBoxItem>().Select((item,index)=>(item,index)).FirstOrDefault(pair=>pair.item.Tag is SoftwareEntry entry&&entry.App==app).index;}
    private void RefreshSoftware(){
        string? selected=((AppBox.SelectedItem as ComboBoxItem)?.Tag as SoftwareEntry)?.Id;bool updating=_updating;_updating=true;
        AppBox.Items.Clear();AppBox.Items.Add(new ComboBoxItem());foreach(var entry in _catalog.Entries)AppBox.Items.Add(new ComboBoxItem{Content=entry.Name,Tag=entry});
        AppBox.SelectedIndex=0;if(selected!=null)foreach(ComboBoxItem item in AppBox.Items)if(item.Tag is SoftwareEntry entry&&entry.Id==selected){AppBox.SelectedItem=item;break;}
        _updating=updating;if(_controller!=null){UpdateConnect();SoftwareName.Text=(AppBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"请选择软件";}
    }
    private void UpdateConnect()
    {
        bool valid=ValidSelectedCode;ConnectButton.IsEnabled=!_controller.Busy&&(valid||string.IsNullOrWhiteSpace(CodeBox.Text)&&_controller.Image!=null&&AppSelection!=RemoteApp.Unknown);
        EntryHint.Text=AppSelection==RemoteApp.Unknown&&AppBox.SelectedIndex>0?"自适应定位；不明确时请在客户端核对":_controller.Busy?"可点停止取消本次操作":!valid?"请输入完整设备码":_controller.HasAttemptedConnection?"修改后可直接重新连接":"没有验证码可留空";
    }
    private void Input_Changed(object sender,TextChangedEventArgs e)
    {
        if(_updating||_controller==null)return;
        DraftIsDirty=true;if(ReferenceEquals(sender,CodeBox))_codeMark=null;else _passwordMark=null;
        PasswordState.Text=PasswordBox.Text.Length>0?"按原图核对大小写，易混字符不要猜":"未读到密码也允许尝试，需要时补填";
        DrawOverlay();UpdateConnect();RefreshCandidateButtons();if(ReferenceEquals(sender,CodeBox))BuildRecent();
    }
    private void SoftwarePicker_Click(object sender,RoutedEventArgs e)
    {
        if(_controller.Busy)return;
        var menu=new System.Windows.Controls.ContextMenu{PlacementTarget=SoftwarePickerButton,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom,MinWidth=180,MaxHeight=320};
        for(int i=1;i<AppBox.Items.Count;i++)
        {
            int index=i;var label=(AppBox.Items[i] as ComboBoxItem)?.Content?.ToString();
            var item=new System.Windows.Controls.MenuItem{Header=label,IsCheckable=true,IsChecked=AppBox.SelectedIndex==i};
            item.Click+=(_,_)=>AppBox.SelectedIndex=index;menu.Items.Add(item);
        }
        SoftwarePickerButton.ContextMenu=menu;menu.IsOpen=true;
    }
    private void App_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(SoftwareName!=null)SoftwareName.Text=(AppBox.SelectedItem as ComboBoxItem)?.Content?.ToString()??"请选择软件";
        if(!_updating&&_controller!=null){DraftIsDirty=true;UpdateConnect();}
    }
    private void BuildCodes()
    {
        CodeOptions.Children.Clear();
        if(_shownResult==null)return;
        var choices=PreviewCandidates.Codes(_shownResult);
        foreach(var choice in choices.Take(12))
            AddOption(CodeOptions,choice.Code.Value,()=>SelectCode(choice.Code,choice.Passwords));
    }
    private void SelectCode(DeviceCodeCandidate code,IReadOnlyList<PasswordCandidate>? source=null)
    {
        if(_controller.Busy)return;
        _selectionPasswords=source??_shownResult?.Passwords??[];
        var password=_selectionPasswords.Where(p=>p.DeviceCandidateId==code.Id).MaxBy(p=>p.RuleScore);
        _updating=true;SelectApp(code.App==RemoteApp.Unknown?RemoteApp.Sunlogin:code.App);CodeBox.Text=code.Value;PasswordBox.Text=password?.Value??"";_updating=false;
        _previewFieldChosen=true;_codeMark=code;_passwordMark=password;_focusFieldsPending=_previewFocusMode;DraftIsDirty=true;BuildPasswords(code.Id);RefreshCandidateButtons();DrawOverlay();UpdateConnect();BuildRecent();
    }
    private void CodeCandidates_Click(object sender,RoutedEventArgs e)=>ShowCandidates(CodeCandidateButton,CodeOptions);
    private void PasswordCandidates_Click(object sender,RoutedEventArgs e)=>ShowCandidates(PasswordCandidateButton,PasswordOptions);
    private IEnumerable<Button> AlternativeChoices(Panel options)
    {
        bool device=ReferenceEquals(options,CodeOptions);
        string Key(string value)=>device?string.Concat(value.Where(c=>!char.IsWhiteSpace(c)&&c!='-')):value;
        string current=Key(device?CodeBox.Text:PasswordBox.Text);
        return options.Children.OfType<Button>().Where(b=>Key(b.Content?.ToString()??"")!=current)
            .GroupBy(b=>Key(b.Content?.ToString()??""),StringComparer.Ordinal).Select(g=>g.First());
    }
    private void RefreshCandidateButtons()
    {
        CodeCandidateButton.Visibility=AlternativeChoices(CodeOptions).Any()?Visibility.Visible:Visibility.Collapsed;
        PasswordCandidateButton.Visibility=AlternativeChoices(PasswordOptions).Any()?Visibility.Visible:Visibility.Collapsed;
        CodeCandidateButton.IsEnabled=PasswordCandidateButton.IsEnabled=!_controller.Busy;
    }
    private void ShowCandidates(Button anchor,Panel options)
    {
        if(_controller.Busy)return;
        var menu=new System.Windows.Controls.ContextMenu{PlacementTarget=anchor,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
        foreach(Button choice in AlternativeChoices(options))
        {
            var item=new System.Windows.Controls.MenuItem{Header=choice.Content};
            item.Click+=(_,_)=>choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));menu.Items.Add(item);
        }
        anchor.ContextMenu=menu;if(menu.Items.Count>0)menu.IsOpen=true;else RefreshCandidateButtons();
    }
    private void BuildPasswords(string? codeId)
    {
        PasswordOptions.Children.Clear();
        var sameTarget=_shownResult?.Attempts.Where(a=>a.SelectedCode?.Value==CodeBox.Text && a.SelectedCode.App==AppSelection)
            .SelectMany(a=>a.Passwords.Where(p=>p.DeviceCandidateId==a.SelectedCode!.Id))??[];
        var source=_selectionPasswords.Count>0?_selectionPasswords:_shownResult?.Passwords??[];
        foreach(var p in source.Where(p=>p.DeviceCandidateId==codeId).Concat(sameTarget).GroupBy(p=>p.Value).Select(g=>g.First()).Take(8))
            AddOption(PasswordOptions,p.Value,()=>{_updating=true;PasswordBox.Text=p.Value;_updating=false;_passwordMark=p;DraftIsDirty=true;RefreshCandidateButtons();DrawOverlay();});
    }
    private static void AddOption(Panel panel,string text,Action select)
    {
        var button=new Button{Content=text,FontSize=11,Padding=new Thickness(7,3,7,3),Margin=new Thickness(0,0,5,4)};
        button.Click+=(_,_)=>select();panel.Children.Add(button);
    }
    private void UpdatePreviewOpacity()
    {
        WindowVisual.OpacityMask=null;SyncPinnedPreview();UpdateIndependentPreviewMask();
    }
    private BitmapSource? _rawPreview;
    private int _displayRotation=-1;
    private Box DisplayBox(Box b)=>_rawPreview==null?b:RemoteConnectHelper.V2.Ocr.PreviewOrientation.ToDisplay(b,_displayRotation,_rawPreview.PixelWidth,_rawPreview.PixelHeight);
    private sealed record PreviewField(string Value,Box Bounds,bool Password,DeviceCodeCandidate? Code=null,PasswordCandidate? Secret=null);
    private PreviewField? _selectedPreviewField;
    public event Action? CopiedText;
    internal Action<string> WriteClipboard {get;set;}=System.Windows.Clipboard.SetText;
    private IReadOnlyList<PreviewField> PreviewFields()
    {
        var fields=new List<PreviewField>();
        void Add(PreviewField field){
            if(string.IsNullOrWhiteSpace(field.Value)||field.Bounds.Width<=0||field.Bounds.Height<=0)return;
            bool SamePlace(Box a,Box b){double area=Math.Max(0,Math.Min(a.Right,b.Right)-Math.Max(a.X,b.X))*Math.Max(0,Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Y,b.Y));return area/Math.Max(1,Math.Min(a.Width*a.Height,b.Width*b.Height))>.65;}
            if(!fields.Any(f=>SamePlace(f.Bounds,field.Bounds)))fields.Add(field);
        }
        void Code(DeviceCodeCandidate c)=>Add(new(c.Value,c.Bounds,c.Value.Any(char.IsLetter),c));
        void Password(PasswordCandidate c)=>Add(new(c.Value,c.Bounds,true,null,c));
        if(_codeMark!=null)Code(_codeMark);if(_passwordMark!=null)Password(_passwordMark);
        var result=_controller.Result??_controller.PreviewResult;
        if(result!=null){
            foreach(var choice in PreviewCandidates.Codes(result)){
                Code(choice.Code);foreach(var password in PreviewCandidates.Passwords(choice))Password(password);
            }
        }
        return fields;
    }
    private void SetPreviewField(PreviewField field)
    {
        if(_controller.Busy)return;
        _previewFieldChosen=true;_updating=true;
        if(field.Password){PasswordBox.Text=field.Value;_passwordMark=field.Secret;}
        else{CodeBox.Text=string.Concat(field.Value.Where(c=>!char.IsWhiteSpace(c)&&c!='-'));_codeMark=field.Code;
            if(field.Code?.App is RemoteApp.Sunlogin or RemoteApp.ToDesk)SelectApp(field.Code.App);}
        _updating=false;DraftIsDirty=true;DrawOverlay();UpdateConnect();RefreshCandidateButtons();BuildRecent();
        DesktopWindows.Passive(this,false);IsEditing=true;Activate();
        var target=field.Password?PasswordBox:CodeBox;target.Focus();target.SelectAll();
    }
    private bool CopyFieldText(string value)
    {
        try{WriteClipboard(value);CopiedText?.Invoke();return true;}
        catch(System.Runtime.InteropServices.ExternalException){_controller.ShowMessage("复制未完成，请再试一次");return false;}
    }
    internal bool CopySelectedPreview()=>_selectedPreviewField!=null&&CopyFieldText(_selectedPreviewField.Value);
    private void SelectPreviewField(PreviewField field,System.Windows.Shapes.Rectangle shape){
        _selectedPreviewField=field;shape.Focus();
        foreach(var mark in Overlay.Children.OfType<System.Windows.Shapes.Rectangle>())mark.Fill=System.Windows.Media.Brushes.Transparent;
        shape.Fill=new SolidColorBrush(System.Windows.Media.Color.FromArgb(24,61,112,232));
    }
    private System.Windows.Controls.ContextMenu FieldMenu(PreviewField field)
    {
        var menu=new System.Windows.Controls.ContextMenu();
        var assign=new System.Windows.Controls.MenuItem{Header=field.Password?"设为密码":"设为设备码",IsEnabled=!_controller.Busy};
        assign.Click+=(_,_)=>SetPreviewField(field);
        var copy=new System.Windows.Controls.MenuItem{Header="复制"};
        copy.Click+=(_,_)=>CopyFieldText(field.Value);
        menu.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.C&&Keyboard.Modifiers==ModifierKeys.Control){e.Handled=true;CopyFieldText(field.Value);menu.IsOpen=false;}};
        menu.Items.Add(assign);menu.Items.Add(copy);return menu;
    }
    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if(SourceImage.Source is not BitmapSource source||PreviewArea.ActualWidth<=0)return;
        double scale=Math.Min(PreviewArea.ActualWidth/source.PixelWidth,PreviewArea.ActualHeight/source.PixelHeight);
        double dx=(PreviewArea.ActualWidth-source.PixelWidth*scale)/2,dy=(PreviewArea.ActualHeight-source.PixelHeight*scale)/2;
        foreach(var field in PreviewFields()){
            Box b=DisplayBox(field.Bounds);double pad=Math.Clamp(b.Height*.2,4,12);
            double left=Math.Max(0,b.X-pad),top=Math.Max(0,b.Y-pad);
            double right=Math.Min(source.PixelWidth,b.Right+pad),bottom=Math.Min(source.PixelHeight,b.Bottom+pad);
            var shape=new System.Windows.Shapes.Rectangle{Width=Math.Max(2,(right-left)*scale),Height=Math.Max(2,(bottom-top)*scale),
                Focusable=true,Fill=System.Windows.Media.Brushes.Transparent,Stroke=(Brush)new BrushConverter().ConvertFromString(field.Password?"#D48B2F":"#3D70E8")!,
                StrokeThickness=1.8/Math.Max(1,PreviewZoom),ToolTip=field.Value+" · 右键"+(field.Password?"设为密码":"设为设备码"),Tag=field.Value};
            shape.PreviewMouseLeftButtonDown+=(_,_)=>SelectPreviewField(field,shape);
            shape.ContextMenu=FieldMenu(field);shape.ContextMenuOpening+=(_,_)=>{SelectPreviewField(field,shape);_singleClickZoom.Stop();shape.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().First().IsEnabled=!_controller.Busy;};
            Canvas.SetLeft(shape,dx+left*scale);Canvas.SetTop(shape,dy+top*scale);Overlay.Children.Add(shape);
        }
        if(_focusFieldsPending&&ImagePanel.IsVisible){_focusFieldsPending=false;FocusFields();}
        UpdatePreviewOpacity();
    }
    private async Task SubmitAsync()
    {
        if(!ConnectButton.IsEnabled)return;
        if(!ValidSelectedCode){RecognizeAndConnectRequested?.Invoke();return;}
        var app=AppSelection;string code=CodeBox.Text,password=PasswordBox.Text;
        IsEditing=false;DesktopWindows.Passive(this,true);
        if(app==RemoteApp.Unknown&&(AppBox.SelectedItem as ComboBoxItem)?.Tag is SoftwareEntry entry)await _controller.ConnectAdaptiveAsync(entry,code,password);
        else await _controller.ConnectAsync(app,code,password);
    }
    internal bool CanConnectFilled=>ConnectButton.IsEnabled&&ValidSelectedCode;
    public Task ConnectFilledAsync()=>SubmitAsync();
    private void Recognize_Click(object sender,RoutedEventArgs e)=>RecognizeRequested?.Invoke();
    private void Connect_Click(object sender,RoutedEventArgs e)=>RecognizeAndConnectRequested?.Invoke();
    private async void Stop_Click(object sender,RoutedEventArgs e)=>await _controller.StopAsync();
    private void Card_MouseDown(object sender,MouseButtonEventArgs e)
    {if(!IsEditing && !_controller.Busy) {DesktopWindows.Passive(this,false);IsEditing=true;Activate();ExplicitActivations++;}}
    private void Minimize_Click(object sender,RoutedEventArgs e){EndFrontPulse();WindowState=WindowState.Minimized;}
    private void Maximize_Click(object sender,RoutedEventArgs e)
    {
        EndFrontPulse();MaxWidth=double.PositiveInfinity;MaxHeight=double.PositiveInfinity;
        WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
        if(WindowState==WindowState.Normal){ConfigureLayout();PositionCard();}
    }
    private IntPtr WindowSizingHook(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message==0x231){_windowSizing=true;}
        else if(message==0x232)
        {
            _windowSizing=false;
            if(WindowState==WindowState.Normal){RememberSessionSize();PreserveVisiblePosition();}
        }
        else if(message==0x84&&WindowState==WindowState.Normal)
        {
            var rect=DesktopWindows.NativeBounds(window);double x=unchecked((short)lParam.ToInt64()),y=unchecked((short)(lParam.ToInt64()>>16));
            double edge=6*DesktopWindows.DpiScale(this);bool left=x<rect.Left+edge,right=x>=rect.Right-edge,top=y<rect.Top+edge,bottom=y>=rect.Bottom-edge;
            handled=true;return new IntPtr(top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:1);
        }
        return IntPtr.Zero;
    }
    private void RememberSessionSize()
    {
        if(WindowState!=WindowState.Normal)return;
        _compactWidth=Math.Max(320,ActualWidth);_previewWidth=_compactWidth;
        _compactHeight=_expandedHeight=Math.Max(420,ActualHeight);
    }
    private void UpdatePreviewMode(){FullImageButton.Tag=_previewFocusMode?"":"Selected";FocusFieldsButton.Tag=_previewFocusMode?"Selected":"";}
    private void UpdatePanelGeometry()
    {
        if(SoftwareStatusRow!=null){
            bool narrow=EditorScroll.ActualWidth<330;
            Grid.SetRow(RiskBox,narrow?1:0);Grid.SetColumn(RiskBox,narrow?0:1);Grid.SetColumnSpan(RiskBox,narrow?2:1);
            RiskBox.Margin=narrow?new Thickness(0,0,0,12):new Thickness(12,0,0,12);
        }
        if(BodyGrid==null)return;
        BodyGrid.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);BodyGrid.ColumnDefinitions[1].Width=new GridLength(0);
        BodyGrid.RowDefinitions[0].Height=new GridLength(1,GridUnitType.Star);BodyGrid.RowDefinitions[1].Height=new GridLength(0);
        EditorScroll.Width=double.NaN;EditorScroll.MaxHeight=double.PositiveInfinity;EditorScroll.HorizontalAlignment=System.Windows.HorizontalAlignment.Stretch;
        EditorBackground.Visibility=Visibility.Collapsed;
    }
    private void Hide_Click(object sender,RoutedEventArgs e)=>HideToTray();
    private void Read_Click(object sender,RoutedEventArgs e)=>ReadRequested?.Invoke();
    private void Open_Click(object sender,RoutedEventArgs e)=>OpenRequested?.Invoke();
    private void Manual_Click(object sender,RoutedEventArgs e)=>ManualRequested?.Invoke();
    private void ClearRecent_Click(object sender,RoutedEventArgs e){_recent.Clear();BuildRecent();}
    internal static int MatchingDigits(string query,string code)
    {
        var a=new string(query.Where(char.IsAsciiDigit).ToArray());var b=new string(code.Where(char.IsAsciiDigit).ToArray());
        int matches=0;for(int i=0;i<Math.Min(a.Length,b.Length);i++)if(a[i]==b[i])matches++;return matches;
    }
    private void RememberRecognized(RecognitionResult result){
        foreach(var choice in PreviewCandidates.Codes(result).Reverse()){
            var code=choice.Code;RememberTarget(code.App,code.Value);
        }
    }
    private void RememberTarget(RemoteApp app,string code){
        if(!ConnectionPolicy.ValidCode(app,code))return;
        bool favorite=_recent.Any(t=>t.App==app&&t.Code==code&&t.Favorite);
        _recent.RemoveAll(t=>t.App==app&&t.Code==code);_recent.Insert(0,new(app,code,favorite));if(_recent.Count>6)_recent.RemoveAt(_recent.Count-1);
    }
    private void BuildRecent()
    {
        RecentOptions.Children.Clear();RecentOptions.Visibility=_recent.Count==0?Visibility.Collapsed:Visibility.Visible;NoRecent.Visibility=_recent.Count==0?Visibility.Visible:Visibility.Collapsed;
        var query=new string(CodeBox.Text.Where(char.IsAsciiDigit).ToArray());
        foreach(var target in _recent.OrderByDescending(t=>MatchingDigits(query,t.Code)).ThenByDescending(t=>t.Favorite).Take(6))
        {
            var row=new Grid{Margin=new Thickness(0,0,4,5)};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(13)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(13)});
            var star=new Button{Content=target.Favorite?"★":"☆",Padding=new Thickness(0),FontSize=9,ToolTip="收藏 / 取消收藏（本次运行）"};
            star.Click+=(_,_)=>{int i=_recent.IndexOf(target);if(i>=0)_recent[i]=target with{Favorite=!target.Favorite};BuildRecent();};
            var code=new Button{Content=target.Code,Padding=new Thickness(0,3,0,3),FontSize=9,ToolTip=RemoteApps.Name(target.App)+" · 回填设备码，不会自动连接"};
            code.Click+=async(_,_)=>{if(_controller.Busy)return;if(!_catalog.Contains(target.App)){_controller.ShowMessage("请先在远程软件列表中添加对应软件");return;}await _controller.NewManualAsync();_updating=true;SelectApp(target.App);CodeBox.Text=target.Code;PasswordBox.Clear();_updating=false;DraftIsDirty=true;EnterEditor();UpdateConnect();};
            var remove=new Button{Content="×",Padding=new Thickness(0),FontSize=9,ToolTip="删除此记录"};
            remove.Click+=(_,_)=>{_recent.Remove(target);BuildRecent();};
            Grid.SetColumn(code,1);Grid.SetColumn(remove,2);row.Children.Add(star);row.Children.Add(code);row.Children.Add(remove);RecentOptions.Children.Add(row);
        }
    }
    public void ShowOriginal(){if(!_expanded)Original_Click(this,new RoutedEventArgs());}
    private void PreviewClose_Click(object sender,RoutedEventArgs e){
        if(_controller.Busy)return;PreviewCleared?.Invoke();_controller.ClearPreview();
        SourceImage.Source=null;SourceImage.Tag=null;_rawPreview=null;_shownPreview=null;_codeMark=null;_passwordMark=null;
        Overlay.Children.Clear();NoImage.Visibility=Visibility.Visible;_finishTimer.Stop();ResetContentTransform();UpdateConnect();e.Handled=true;
    }
    private void UpdatePreviewToggle(){
        if(PreviewToggleText==null)return;string label=_expanded?"收起预览":"打开预览";PreviewToggleText.Text=label;System.Windows.Automation.AutomationProperties.SetName(OriginalButton,label);
        double target=_expanded?180:0;
        if(IsLoaded)PreviewArrowRotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(target,TimeSpan.FromMilliseconds(140)));
        else PreviewArrowRotation.Angle=target;
    }
    private void Original_Click(object sender,RoutedEventArgs e)
    {
        RememberSessionSize();_expanded=!_expanded;ImagePanel.Visibility=_expanded?Visibility.Visible:Visibility.Collapsed;
        UpdatePreviewToggle();ConfigureLayout();
        Dispatcher.BeginInvoke(new Action(()=>{DrawOverlay();if(_previewFocusMode)FocusFields();}),DispatcherPriority.Loaded);
    }
    private void ConfigureLayout()
    {
        MinWidth=320;MinHeight=420;
        if(!_windowSizing&&WindowState==WindowState.Normal){Width=_compactWidth;Height=_compactHeight;}
        Grid.SetColumn(EditorScroll,0);Grid.SetRow(EditorScroll,0);Grid.SetColumn(ImagePanel,1);Grid.SetRow(ImagePanel,1);
        ImagePanel.Width=0;ImagePanel.Height=0;ImagePanel.Margin=new Thickness(0);
        UpdatePanelGeometry();Dispatcher.BeginInvoke(new Action(UpdatePreviewOpacity),DispatcherPriority.Loaded);
    }
    private void Preview_Changed(object sender,SizeChangedEventArgs e){if(_focusedRegion)_focusFieldsPending=_previewFocusMode;DrawOverlay();}
    private static bool FindButton(DependencyObject element)
    {for(var item=element;item!=null;item=VisualTreeHelper.GetParent(item))if(item is System.Windows.Controls.Primitives.ButtonBase)return true;return false;}
    private UIElement? _headerCapture;
    internal bool IsDraggingWindow=>_draggingHeader;
    internal void BeginBlockDrag(bool preview,System.Windows.Point screenPoint)
    {
        _movingPreviewBlock=preview;_headerStart=screenPoint;
        if(_blocksLocked&&_previewPin?.IsVisible==true){
            _groupMainOrigin=DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(this).Handle).TopLeft;
            _groupPreviewOrigin=DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(_previewPin).Handle).TopLeft;
        }else if(_previewPin?.IsVisible==true){_blocksLocked=false;_previewDock=null;}
        var window=preview?_previewPin:this;
        if(window==null)return;
        _headerOrigin=DesktopWindows.NativeBounds(new System.Windows.Interop.WindowInteropHelper(window).Handle).TopLeft;
        _draggingHeader=true;
    }
    internal void UpdateBlockDrag(System.Windows.Point screenPoint)
    {
        if(!_draggingHeader)return;
        if(_blocksLocked&&_previewPin?.IsVisible==true){MoveLockedBlocks(screenPoint-_headerStart);return;}
        var target=_headerOrigin+(screenPoint-_headerStart);
        if(_movingPreviewBlock)MovePreviewBlockTo(target);else MoveCardTo(target);
    }
    internal void EndBlockDrag()
    {
        if(!_draggingHeader)return;
        _draggingHeader=false;
        var captured=_headerCapture;_headerCapture=null;
        if(captured?.IsMouseCaptured==true)captured.ReleaseMouseCapture();
        FinishBlockMove();
    }
    private void Header_Down(object sender,MouseButtonEventArgs e)
    {
        bool preview=ReferenceEquals(sender,ImageToolbar)||ReferenceEquals(sender,PreviewFooter);
        var window=preview?_previewPin:this;
        if(window==null||window.WindowState==WindowState.Maximized||
           (!preview&&WindowButtons.IsMouseOver)||e.OriginalSource is DependencyObject source&&FindButton(source))return;
        var surface=sender as UIElement??HeaderBar;
        if(!surface.CaptureMouse())return;
        _headerCapture=surface;
        BeginBlockDrag(preview,DesktopWindows.CursorPosition());e.Handled=true;
    }
    private void Header_Move(object sender,System.Windows.Input.MouseEventArgs e)
    {
        if(!_draggingHeader)return;
        if(e.LeftButton!=MouseButtonState.Pressed){EndBlockDrag();return;}
        UpdateBlockDrag(DesktopWindows.CursorPosition());e.Handled=true;
    }
    private void Header_Up(object sender,MouseButtonEventArgs e)
    {
        if(!_draggingHeader)return;
        UpdateBlockDrag(DesktopWindows.CursorPosition());EndBlockDrag();e.Handled=true;
    }
    private void Header_LostCapture(object sender,System.Windows.Input.MouseEventArgs e)
    {
        // Ignore bubbled capture changes from child buttons; only end our own drag.
        if(ReferenceEquals(e.OriginalSource,_headerCapture))EndBlockDrag();
    }
    public void ZoomPreview(double requested,System.Windows.Point cursor)
    {
        if(SourceImage.Source==null)return;
        _focusedRegion=false;ImageViewport.Clip=null;
        _singleClickZoom.Stop();
        double zoom=Math.Clamp(requested,1,12),ratio=zoom/ImageZoom.ScaleX;
        double x=cursor.X-PreviewArea.ActualWidth/2,y=cursor.Y-PreviewArea.ActualHeight/2;
        ImagePan.X=x-(x-ImagePan.X)*ratio;ImagePan.Y=y-(y-ImagePan.Y)*ratio;
        ImageZoom.ScaleX=ImageZoom.ScaleY=zoom;
        foreach(var mark in Overlay.Children.OfType<System.Windows.Shapes.Rectangle>())mark.StrokeThickness=1.8/zoom;
        if(zoom==1)ImagePan.X=ImagePan.Y=0;
        UpdatePreviewOpacity();
    }
    private void ResetContentTransform(){bool locate=_previewFocusMode;ResetPreview();_previewFocusMode=locate;UpdatePreviewMode();}
    public void ResetPreview(){_singleClickZoom.Stop();_previewFocusMode=false;UpdatePreviewMode();ImageZoom.ScaleX=ImageZoom.ScaleY=1;ImagePan.X=ImagePan.Y=0;_focusFieldsPending=false;_focusedRegion=false;ImageViewport.Clip=null;UpdatePreviewOpacity();}
    public void PanPreview(Vector delta){if(SourceImage.Source!=null){_focusedRegion=false;ImageViewport.Clip=null;ImagePan.X+=delta.X;ImagePan.Y+=delta.Y;UpdatePreviewOpacity();}}
    public void FocusFields()
    {
        if(SourceImage.Source is not BitmapSource source||PreviewArea.ActualWidth<=0||PreviewArea.ActualHeight<=0)return;
        var boxes=PreviewFields().Select(f=>DisplayBox(f.Bounds)).ToList();
        if(boxes.Count==0){ResetPreview();return;}
        var region=Box.Union(boxes);double scale=Math.Min(PreviewArea.ActualWidth/source.PixelWidth,PreviewArea.ActualHeight/source.PixelHeight);
        double pad=Math.Max(16,region.Height*.7);
        double left=Math.Max(0,region.X-pad),top=Math.Max(0,region.Y-pad);
        double right=Math.Min(source.PixelWidth,region.Right+pad),bottom=Math.Min(source.PixelHeight,region.Bottom+pad);
        double offsetX=(PreviewArea.ActualWidth-source.PixelWidth*scale)/2,offsetY=(PreviewArea.ActualHeight-source.PixelHeight*scale)/2;
        ImageViewport.Clip=new RectangleGeometry(new Rect(offsetX+left*scale,offsetY+top*scale,Math.Max(1,(right-left)*scale),Math.Max(1,(bottom-top)*scale)));
        _focusedRegion=true;_previewFocusMode=true;UpdatePreviewMode();
        double zoom=Math.Clamp(Math.Min(PreviewArea.ActualWidth*.9/((region.Width+2*pad)*scale),PreviewArea.ActualHeight*.85/((region.Height+2*pad)*scale)),1,12);
        ImageZoom.ScaleX=ImageZoom.ScaleY=zoom;
        foreach(var mark in Overlay.Children.OfType<System.Windows.Shapes.Rectangle>())mark.StrokeThickness=1.8/zoom;
        ImagePan.X=(source.PixelWidth/2d-region.CenterX)*scale*zoom;
        ImagePan.Y=(source.PixelHeight/2d-region.CenterY)*scale*zoom;
        UpdatePreviewOpacity();
    }
    private void FocusFields_Click(object sender,RoutedEventArgs e)=>FocusFields();
    private void FullImage_Click(object sender,RoutedEventArgs e)=>ResetPreview();
    private void Preview_Wheel(object sender,MouseWheelEventArgs e)
    {ZoomPreview(PreviewZoom*Math.Pow(1.2,e.Delta/120d),e.GetPosition(PreviewArea));e.Handled=true;}
    private void Preview_Down(object sender,MouseButtonEventArgs e)
    {
        if(e.ClickCount==2){_draggingPreview=false;PreviewArea.ReleaseMouseCapture();ResetPreview();e.Handled=true;return;}
        if(SourceImage.Source==null){e.Handled=true;return;}
        _draggingPreview=true;_previewMoved=false;_previewStart=e.GetPosition(PreviewArea);_panStartX=ImagePan.X;_panStartY=ImagePan.Y;
        PreviewArea.CaptureMouse();e.Handled=true;
    }
    private void Preview_Move(object sender,System.Windows.Input.MouseEventArgs e)
    {
        if(!_draggingPreview)return;
        var delta=e.GetPosition(PreviewArea)-_previewStart;
        if(delta.Length>3)_previewMoved=true;
        if(_previewMoved&&SourceImage.Source!=null){_focusedRegion=false;ImageViewport.Clip=null;ImagePan.X=_panStartX+delta.X;ImagePan.Y=_panStartY+delta.Y;UpdatePreviewOpacity();}
    }
    private void Preview_Up(object sender,MouseButtonEventArgs e)
    {
        if(!_draggingPreview)return;_draggingPreview=false;PreviewArea.ReleaseMouseCapture();
        if(!_previewMoved&&SourceImage.Source!=null){_singleClickZoom.Stop();_singleClickPoint=e.GetPosition(PreviewArea);_singleClickSession=_controller.SessionId;_singleClickZoom.Start();}
        e.Handled=true;
    }
    private void Preview_LostCapture(object sender,System.Windows.Input.MouseEventArgs e)=>_draggingPreview=false;
    private async void Card_KeyDown(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key==Key.C&&Keyboard.Modifiers==ModifierKeys.Control){
            if(Keyboard.FocusedElement is System.Windows.Controls.TextBox text&&text.SelectionLength>0){e.Handled=true;CopyFieldText(text.SelectedText);}
            else if(Keyboard.FocusedElement is System.Windows.Shapes.Rectangle&&CopySelectedPreview())e.Handled=true;
        }
        else if(e.Key==Key.Escape){e.Handled=true;if(_controller.Busy)await _controller.StopAsync();else HideToTray();}
        else if(e.Key==Key.Enter && Keyboard.FocusedElement is System.Windows.Controls.TextBox box && !box.IsReadOnly && box is not null)
        {e.Handled=true;await SubmitAsync();}
        else if(e.Key==Key.V && Keyboard.Modifiers==ModifierKeys.Control && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {e.Handled=true;ReadRequested?.Invoke();}
    }
    public event Action<string>? FileDropped;
    private void Card_Drop(object sender,System.Windows.DragEventArgs e)
    {if(e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[]{Length:>0} paths)FileDropped?.Invoke(paths[0]);}
    private void Card_Closing(object? sender,CancelEventArgs e){if(!_canClose){e.Cancel=true;HideToTray();}}
}

public sealed class OriginalImageWindow : Window
{
    public OriginalImageWindow(BitmapSource image)
    {
        Title="完整原图";Width=880;Height=650;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Content=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            Content=new System.Windows.Controls.Image{Source=image,Width=image.PixelWidth,Height=image.PixelHeight,Stretch=Stretch.None}};
        SourceInitialized+=(_,_)=>DesktopWindows.Place(this,DesktopWindows.WorkAreaAtPointer());
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
    }
}
