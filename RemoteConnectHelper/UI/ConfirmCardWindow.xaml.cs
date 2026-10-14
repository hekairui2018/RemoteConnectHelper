using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Hotkey;
using RemoteConnectHelper.Recognition;

namespace RemoteConnectHelper.UI;

/// <summary>
/// 唯一的连接结果窗口（悬浮球旁气泡卡）：预填设备码/密码/软件（可改），底部【停止】【连接】两按钮。
/// 流程：点悬浮球→弹卡；品牌铁证时自动发起连接，没把握时等客服选软件/手填后点「连接」。
/// 成功 → 气泡显示“已发起连接”后自动消失；失败/需要密码 → 气泡停留，客服在卡上改码/改密码/换软件，
/// 再点「连接」重试；点「停止」或按 Esc = 取消（连接前）/ 断开（已点下连接后）。
/// 等待人工选择时 8 秒无人理会自动关闭；连接中与失败后不再自动关。
/// </summary>
public partial class ConfirmCardWindow : Window
{
    private readonly RecognizedResult _result;
    private readonly Func<RemoteAppKind, string, string, ConnectionContext, Task<ConnectResult>> _onConnect;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _autoClose;
    private readonly KeyHook _keyHook = new();

    private const double AutoCloseSeconds = 8;
    private DateTime _autoDeadline;
    private bool _held;          // 等待确认态已被交互保持
    private bool _connecting;    // 连接中
    private bool _failed;        // 失败/需要密码停留态（不再自动关）
    private bool _closed;
    private bool _initializing = true;
    private readonly Rect? _anchor;
    private readonly double _baseHeight;
    private Window? _sourceWindow;
    private readonly bool _focusPwd;
    private readonly bool _autoConnect;
    private RemoteAppKind _selectedKind;
    private ConnectionContext? _ctx;
    private readonly TaskCompletionSource<bool> _closedTcs = new();

    public Task<bool> ClosedTask => _closedTcs.Task;

    private static readonly Brush SelBg = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush SelFg = Brushes.White;
    private static readonly Brush IdleBg = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));
    private static readonly Brush IdleFg = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xD4));

    public ConfirmCardWindow(RecognizedResult result, BitmapSource? thumb,
                             Func<RemoteAppKind, string, string, ConnectionContext, Task<ConnectResult>> onConnect,
                             Rect? anchor = null, string? statusOverride = null, bool autoConnect = false)
    {
        InitializeComponent();
        _result = result;
        _onConnect = onConnect;
        _focusPwd = !string.IsNullOrEmpty(statusOverride);
        _autoConnect = autoConnect;
        _anchor = anchor;

        // 默认软件：明确 ToDesk 选 ToDesk，其余（向日葵/未知）默认向日葵
        _selectedKind = result.Kind == RemoteAppKind.ToDesk ? RemoteAppKind.ToDesk : RemoteAppKind.Sunlogin;
        SetSelected(_selectedKind);

        // 设备码（深色 TextBox，白字可见）；认到多个长码时在下方给候选小按钮切换
        CodeBox.Text = result.DeviceCode;
        BuildCandidates(result.CandidateCodes, result.DeviceCode);

        // 密码框常驻：识别到就预填，没有就留空（水印提示留空＝免密），客服随时可补
        PwdBox.Text = result.Password;
        UpdatePlaceholder();
        BuildPwdCandidates(result.CandidatePasswords, result.Password);
        if (CandidatePanel.Visibility == Visibility.Visible) Height += 30;
        if (PwdCandidatePanel.Visibility == Visibility.Visible) Height += 30;
        _baseHeight = Height;
        PreparePreview(thumb);

        // 状态提示
        if (!string.IsNullOrEmpty(statusOverride))
            ShowStatus(statusOverride, "#FFB347");
        else if (!string.IsNullOrEmpty(result.Error))
            ShowStatus(result.Error, "#FFB347");
        else if (_autoConnect)
            ShowStatus($"已识别{result.KindName}，正在自动连接…", "#4C8DFF");
        else if (result.ImageEnhanced)
            ShowStatus("已做图像增强，请对照原图核对设备码和密码后点「连接」", "#FFB347");
        else if (result.PasswordNeedsReview)
            ShowStatus(result.HasPassword
                ? "密码没识别清楚，请对照截图修改后点「连接」"
                : "截图中的密码不可读，请核对或补填；确认允许免密才留空连接", "#FFB347");
        else if (!result.BrandConfident)
            ShowStatus("没看清是哪个软件：点下面对应按钮选定，再点「连接」（码/密码可改）", "#FFB347");
        else
            ShowStatus("已识别，点「连接」或按回车发起", "#4C8DFF");

        CountdownBar.Maximum = AutoCloseSeconds;
        CountdownBar.Value = AutoCloseSeconds;
        UpdateHint(AutoCloseSeconds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); Close(); };

        // 任一交互都保持气泡，不再自动关闭
        CardBorder.MouseEnter += (_, _) => Hold();
        IsKeyboardFocusWithinChanged += (_, _) => { if (IsKeyboardFocusWithin) Hold(); };
        PwdBox.GotFocus += (_, _) => Hold();

        PlaceByAnchor(anchor);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _initializing = false;
        if (!_autoConnect && (result.PasswordNeedsReview || result.ImageEnhanced))
        {
            Hold();
            if (SourcePreview.Source != null) SetPreviewVisible(true);
        }

        Services.Logger.Info($"确认卡显示 默认={_selectedKind switch { RemoteAppKind.ToDesk => "ToDesk", _ => "向日葵" }} " +
                             $"码={result.DeviceCode} 密码={(result.HasPassword ? result.Password : "<免密>")} " +
                             $"依据=[{result.BrandReason}] 自动连接={autoConnect}");
    }

    private void PreparePreview(BitmapSource? thumb)
    {
        PreviewBtn.Visibility = Visibility.Collapsed;
        if (thumb == null) return;
        try
        {
            var image = Services.OcrService.RotateForOcr(thumb, _result.ImageRotation);
            // 保留整张截图。只围绕设备码裁固定高度会丢掉下方或右侧的密码。
            SourcePreview.Source = image;
            PreviewBtn.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { Services.Logger.Warn("截图预览失败：" + ex.Message); }
    }

    private void SetPreviewVisible(bool visible)
    {
        PreviewBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewBtn.Content = visible ? "收起截图" : "查看截图";
        Height = _baseHeight + (visible ? 130 : 0);
        PlaceByAnchor(_anchor);
    }

    private void PreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        Hold();
        SetPreviewVisible(PreviewBorder.Visibility != Visibility.Visible);
    }

    private void SourcePreview_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_connecting || _closed || SourcePreview.Source is not BitmapSource source) return;
        Hold();
        if (_sourceWindow != null) { _sourceWindow.Activate(); return; }
        var image = new System.Windows.Controls.Image { Source = source, Stretch = Stretch.None,
            Width = source.PixelWidth, Height = source.PixelHeight };
        var viewer = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Black };
        var area = SystemParameters.WorkArea;
        var window = new Window { Title = "完整截图（可滚动查看）", Owner = this, Content = viewer,
            Width = Math.Min(Math.Max(source.PixelWidth + 40, 440), area.Width - 40),
            Height = Math.Min(source.PixelHeight + 65, area.Height - 60),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Topmost = true };
        _sourceWindow = window;
        _keyHook.HookEnter = false;
        window.Closed += (_, _) => { _sourceWindow = null; _keyHook.HookEnter = !_connecting && !_closed; };
        window.Show();
    }

    private void InputBox_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_connecting || _closed || sender is not System.Windows.Controls.TextBox field || !field.IsEnabled) return;
        Hold();
        // 不激活显示的卡片可能只有 WPF 逻辑焦点，键盘仍送往之前的窗口。
        // 用户明确点击编辑时再激活卡片并给键盘焦点，初次显示仍不抢前台。
        Activate();
        System.Windows.Input.Keyboard.Focus(field);
    }

    /// <summary>切换两个软件大按钮的选中高亮（只选择，不发起连接；发起由「连接」按钮/回车负责）。</summary>
    private void SetSelected(RemoteAppKind kind)
    {
        _selectedKind = kind;
        bool sun = kind == RemoteAppKind.Sunlogin;
        SunBtn.Background = sun ? SelBg : IdleBg;
        SunBtn.Foreground = sun ? SelFg : IdleFg;
        TdBtn.Background = sun ? IdleBg : SelBg;
        TdBtn.Foreground = sun ? IdleFg : SelFg;
    }

    private void SunBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        SetSelected(RemoteAppKind.Sunlogin);
        Hold();
        ShowStatus("已选向日葵，点「连接」发起（码/密码可改）", "#4C8DFF");
    }

    private void TdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        SetSelected(RemoteAppKind.ToDesk);
        Hold();
        ShowStatus("已选 ToDesk，点「连接」发起（码/密码可改）", "#4C8DFF");
    }

    private void PwdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Hold();
        UpdatePlaceholder();
    }

    private void CodeBox_GotFocus(object sender, RoutedEventArgs e)
    {
        Hold();
        CodeBox.SelectAll();
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e) => Hold();

    /// <summary>认到多个长码时，在设备码框下方列出“其它候选”小按钮，点一下即替换。</summary>
    private void BuildCandidates(List<string> candidates, string chosen)
    {
        var others = candidates.Distinct().Where(c => c != chosen).ToList();
        if (others.Count == 0) return;
        CandidatePanel.Visibility = Visibility.Visible;
        CandidatePanel.Children.Add(new TextBlock
        {
            Text = "其它候选：", Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        });
        foreach (var c in others)
        {
            string cap = c;
            var b = new System.Windows.Controls.Button
            {
                Content = cap, Height = 24, Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(0, 0, 6, 0), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Background = IdleBg,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xC6, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x4A))
            };
            b.Click += (_, _) => { CodeBox.Text = cap; CodeBox.Focus(); Hold(); };
            CandidatePanel.Children.Add(b);
        }
    }

    /// <summary>密码有多个 OCR 读法时，在密码框下方列“其它密码”小按钮，点一下即替换（暖色与设备码候选区分）。</summary>
    private void BuildPwdCandidates(List<string> candidates, string chosen)
    {
        var others = candidates.Distinct().Where(c => !string.IsNullOrEmpty(c) && c != chosen).ToList();
        if (others.Count == 0) return;
        PwdCandidatePanel.Visibility = Visibility.Visible;
        PwdCandidatePanel.Children.Add(new TextBlock
        {
            Text = "其它密码：", Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        });
        foreach (var c in others)
        {
            string cap = c;
            var b = new System.Windows.Controls.Button
            {
                Content = cap, Height = 24, Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(0, 0, 6, 0), FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Background = IdleBg,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0x8A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x4A))
            };
            b.Click += (_, _) => { PwdBox.Text = cap; PwdBox.Focus(); Hold(); UpdatePlaceholder(); };
            PwdCandidatePanel.Children.Add(b);
        }
    }

    private void UpdatePlaceholder()
        => PwdPlaceholder.Visibility = string.IsNullOrEmpty(PwdBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;

    private enum TailDir { Down, Up, Left, Right }

    /// <summary>以悬浮球当前位置为锚点摆放气泡（球常驻右下 → 气泡在球左上方、尾巴指向球），并自动避开屏幕边缘。</summary>
    private void PlaceByAnchor(Rect? anchor)
    {
        double w = Width, h = Height;

        if (anchor is not Rect b)
        {
            var wa0 = SystemParameters.WorkArea;
            Left = wa0.Right - w - 14;
            Top = wa0.Bottom - h - 14;
            Tail.Visibility = Visibility.Collapsed;
            CardBorder.Margin = new Thickness(0);
            return;
        }

        var scr = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)(b.Left + b.Width / 2), (int)(b.Top + b.Height / 2)));
        var wa = scr.WorkingArea;
        double gap = 14;
        double waL = wa.Left, waT = wa.Top, waR = wa.Right, waB = wa.Bottom;
        double bcx = b.Left + b.Width / 2, bcy = b.Top + b.Height / 2;
        double above = b.Top - waT, below = waB - b.Bottom;
        double leftSpace = b.Left - waL, rightSpace = waR - b.Right;

        double L, T;
        TailDir dir;
        if (above >= h + gap)
        {
            L = ClampD(bcx - (w - 46), waL + 8, waR - w - 8);
            T = b.Top - gap - h;
            dir = TailDir.Down;
        }
        else if (below >= h + gap)
        {
            L = ClampD(bcx - (w - 46), waL + 8, waR - w - 8);
            T = b.Bottom + gap;
            dir = TailDir.Up;
        }
        else if (leftSpace >= w + gap)
        {
            L = b.Left - gap - w;
            T = ClampD(bcy - h / 2, waT + 8, waB - h - 8);
            dir = TailDir.Right;
        }
        else
        {
            L = b.Right + gap;
            T = ClampD(bcy - h / 2, waT + 8, waB - h - 8);
            dir = TailDir.Left;
        }
        Left = L; Top = T;
        ApplyTail(dir, L, T, bcx, bcy, w, h);
    }

    /// <summary>按方位摆放小尾巴（三角尖尽量对准球心）。</summary>
    private void ApplyTail(TailDir d, double L, double T, double bcx, double bcy, double w, double h)
    {
        Tail.Visibility = Visibility.Visible;
        double along;
        switch (d)
        {
            case TailDir.Down:
                CardBorder.Margin = new Thickness(0, 0, 0, 10);
                Tail.HorizontalAlignment = HorizontalAlignment.Right;
                Tail.VerticalAlignment = VerticalAlignment.Bottom;
                TailRotate.Angle = 0;
                along = ClampD((L + w) - bcx - 9, 12, w - 30);
                Tail.Margin = new Thickness(0, 0, along, 0);
                break;
            case TailDir.Up:
                CardBorder.Margin = new Thickness(0, 10, 0, 0);
                Tail.HorizontalAlignment = HorizontalAlignment.Right;
                Tail.VerticalAlignment = VerticalAlignment.Top;
                TailRotate.Angle = 180;
                along = ClampD((L + w) - bcx - 9, 12, w - 30);
                Tail.Margin = new Thickness(0, 0, along, 0);
                break;
            case TailDir.Right:
                CardBorder.Margin = new Thickness(0, 0, 10, 0);
                Tail.HorizontalAlignment = HorizontalAlignment.Right;
                Tail.VerticalAlignment = VerticalAlignment.Top;
                TailRotate.Angle = 90;
                along = ClampD(bcy - T - 9, 12, h - 30);
                Tail.Margin = new Thickness(0, along, 0, 0);
                break;
            default: // Left
                CardBorder.Margin = new Thickness(10, 0, 0, 0);
                Tail.HorizontalAlignment = HorizontalAlignment.Left;
                Tail.VerticalAlignment = VerticalAlignment.Top;
                TailRotate.Angle = -90;
                along = ClampD(bcy - T - 9, 12, h - 30);
                Tail.Margin = new Thickness(0, along, 0, 0);
                break;
        }
    }

    private static double ClampD(double v, double min, double max)
        => Math.Min(Math.Max(v, min), max);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _keyHook.EnterPressed += () => Dispatcher.BeginInvoke(new Action(() => Connect()));
        _keyHook.EscapePressed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_sourceWindow != null) _sourceWindow.Close();
            else Stop();
        }));
        try { _keyHook.Install(); }
        catch (Exception ex) { Services.Logger.Error("键盘钩子装载失败", ex); }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Services.Logger.Info($"确认气泡显示 Left={Left:F0} Top={Top:F0} {Width:F0}x{Height:F0}");
        _autoDeadline = DateTime.Now.AddSeconds(AutoCloseSeconds);
        _timer.Start();
        // 密码未通过/需要密码而自动弹出时，光标直接落密码框并全选，方便立刻改
        if (_focusPwd) { PwdBox.Focus(); PwdBox.SelectAll(); }
        // 品牌铁证自动直连：加载完立即发起
        if (_autoConnect) Dispatcher.BeginInvoke(new Action(() => Connect()));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_held || _connecting || _failed) { _timer.Stop(); return; }
        var remain = (_autoDeadline - DateTime.Now).TotalSeconds;
        if (remain <= 0)
        {
            _timer.Stop();
            Cancel("8秒超时");
            return;
        }
        CountdownBar.Value = remain;
        UpdateHint(remain);
    }

    /// <summary>客服开始看/操作气泡：停止自动关闭并保持。</summary>
    private void Hold()
    {
        if (_initializing || _held) return;
        _held = true;
        _timer?.Stop();
        if (CountdownBar != null) CountdownBar.Visibility = Visibility.Collapsed;
        UpdateHint(null);
    }

    private string KindShortName(RemoteAppKind k) => k == RemoteAppKind.ToDesk ? "ToDesk" : "向日葵";

    private void UpdateHint(double? remain)
    {
        string baseText = _connecting
            ? "正在连接… 点「停止」或按 Esc 中止"
            : _failed
                ? "改好后点「连接」重试 · Esc / 停止关闭"
                : "回车连接 · Esc / 停止关闭";
        HintText.Text = remain.HasValue && !_connecting && !_failed
            ? $"{remain.Value:0} 秒后自动关闭 · " + baseText
            : baseText;
    }

    /// <summary>点「连接」：按卡片上的软件/码/密码发起，结果在卡片内流转（成功自动关，失败停留可改）。</summary>
    private async void Connect()
    {
        if (_connecting || _closed) return;
        _timer.Stop();
        Hold();

        var code = (CodeBox.Text ?? "").Trim().Replace(" ", "");
        var pwd = (PwdBox.Text ?? "").Trim();

        bool validLength = _selectedKind == RemoteAppKind.ToDesk
            ? code.Length == 9
            : code.Length is 9 or 10;
        if (!validLength || !code.All(c => c is >= '0' and <= '9'))
        {
            ShowStatus(_selectedKind == RemoteAppKind.ToDesk
                ? "ToDesk 设备码应为 9 位数字，请在卡片上修改"
                : "向日葵识别码应为 9~10 位数字，请在卡片上修改", "#FF6B6B");
            CodeBox.Focus();
            return;
        }

        _connecting = true;
        _keyHook.HookEnter = false;
        _failed = false;
        SetInputsEnabled(false);
        ConnectBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        StopBtn.Content = "停止";
        ShowStatus($"正在打开{KindShortName(_selectedKind)}并连接…", "#4C8DFF");
        UpdateHint(null);
        Services.Logger.Info($"卡片发起连接：{KindShortName(_selectedKind)} 码={code} 密码={(string.IsNullOrEmpty(pwd) ? "<免密>" : pwd)}");

        var ctx = new ConnectionContext(_selectedKind);
        _ctx = ctx;
        try
        {
            var res = await _onConnect(_selectedKind, code, pwd, ctx);
            if (!_closed) ApplyResult(res);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) ApplyResult(ConnectResult.Cancel());
        }
        catch (Exception ex)
        {
            Services.Logger.Error("卡片发起连接异常", ex);
            if (!_closed)
            {
                ShowStatus("连接异常：" + ex.Message, "#FF6B6B");
                EnterFailedState();
            }
        }
        finally
        {
            _connecting = false;
            if (_closed) _closedTcs.TrySetResult(false);
        }
    }

    /// <summary>按连接终态流转：成功短暂停留后自动消失；失败/要密码停留可改；取消短暂提示后关闭。</summary>
    private void ApplyResult(ConnectResult res)
    {
        _connecting = false;
        _keyHook.HookEnter = true;
        switch (res.Status)
        {
            case ConnectStatus.Launched:
                ShowStatus(res.Message ?? "已发起连接", "#3AD17A");
                SetInputsEnabled(false);
                ConnectBtn.IsEnabled = false;
                StopBtn.IsEnabled = false;
                HintText.Text = "连接已发起，气泡即将消失";
                _autoClose.Interval = TimeSpan.FromMilliseconds(1500);
                _autoClose.Start();
                break;
            case ConnectStatus.Cancelled:
                ShowStatus(res.Message ?? "已取消", "#8A8A96");
                SetInputsEnabled(false);
                ConnectBtn.IsEnabled = false;
                StopBtn.IsEnabled = false;
                _autoClose.Interval = TimeSpan.FromMilliseconds(1100);
                _autoClose.Start();
                break;
            case ConnectStatus.NeedPassword:
                ShowStatus(res.Message ?? "需要密码", "#FFB347");
                EnterFailedState();
                PwdBox.Focus();
                PwdBox.SelectAll();
                break;
            default:
                ShowStatus(res.Message ?? "连接未成功", "#FF6B6B");
                EnterFailedState();
                break;
        }
    }

    /// <summary>失败/需要密码停留态：不再自动关闭，输入与「连接」恢复可用，客服改完重试。</summary>
    private void EnterFailedState()
    {
        _connecting = false;
        _keyHook.HookEnter = true;
        _failed = true;
        _timer.Stop();
        CountdownBar.Visibility = Visibility.Collapsed;
        SetInputsEnabled(true);
        ConnectBtn.IsEnabled = true;
        StopBtn.IsEnabled = true;
        StopBtn.Content = "停止";
        UpdateHint(null);
    }

    /// <summary>点「停止」/Esc：连接中=取消（已点连接则由 App 做断开兜底）；等待或失败态=直接关闭卡片。</summary>
    private void Stop()
    {
        if (_closed) return;
        if (_connecting)
        {
            StopBtn.IsEnabled = false;
            ShowStatus("正在取消…", "#8A8A96");
            Services.Logger.Info("用户在卡片点停止（连接中）");
            _ctx?.CancelByUser();
            return;
        }
        Cancel("停止");
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e) => Stop();

    private void ConnectBtn_Click(object sender, RoutedEventArgs e) => Connect();

    private void Cancel(string reason)
    {
        if (_closed) return;
        if (_connecting) _ctx?.CancelByUser();
        _closed = true;
        _timer.Stop();
        _autoClose.Stop();
        Services.Logger.Info($"卡片取消（{reason}） 码={(CodeBox.Text ?? "").Trim().Replace(" ", "")}");
        // 卡片可立即隐藏，但连接收尾完成前仍占住主管线，避免新旧自动化同时输入。
        if (!_connecting) _closedTcs.TrySetResult(false);
        Close();
    }

    private void CloseBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Cancel("手动关闭");

    /// <summary>外部强制关闭（如悬浮球触发"关旧卡重新识别"）：连接中先取消连接，再走统一关闭。</summary>
    public void ForceClose(string reason = "重新识别")
    {
        Cancel(reason);
    }

    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Hold();

    private void SetInputsEnabled(bool enabled)
    {
        SunBtn.IsEnabled = enabled;
        TdBtn.IsEnabled = enabled;
        CodeBox.IsEnabled = enabled;
        PwdBox.IsEnabled = enabled;
        CandidatePanel.IsEnabled = enabled;
        PwdCandidatePanel.IsEnabled = enabled;
        PreviewBtn.IsEnabled = enabled;
        SourcePreview.IsHitTestVisible = enabled;
    }

    private void ShowStatus(string msg, string color)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new BrushConverter().ConvertFromString(color) as Brush;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _sourceWindow?.Close();
        if (_connecting) _ctx?.CancelByUser();
        _timer.Stop();
        _autoClose.Stop();
        try { _keyHook.Dispose(); } catch { }
        _closed = true;
        if (!_connecting) _closedTcs.TrySetResult(false);
    }
}
