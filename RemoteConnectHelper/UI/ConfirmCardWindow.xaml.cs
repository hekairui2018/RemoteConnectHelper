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
/// 从悬浮球旁冒出的“气泡”确认/手填卡：认不准软件、未识别、需补密码时弹出。
/// 软件二选一用两个大按钮——点【向日葵】/【ToDesk】就直接连哪家（码/密码已预填，可改）；
/// 回车＝连当前高亮（认不准时默认向日葵）；密码框常驻，留空即免密直连。
/// 弹出后 8 秒无人理会自动关闭（=取消，不连接）；鼠标移入、聚焦、改码/改密码即保持不消失。
/// </summary>
public partial class ConfirmCardWindow : Window
{
    private readonly RecognizedResult _result;
    private readonly Func<RemoteAppKind, string, string, Task<ConnectResult>> _connect;
    private readonly DispatcherTimer _timer;
    private readonly KeyHook _keyHook = new();

    private const double AutoCloseSeconds = 8;
    private DateTime _autoDeadline;
    private bool _held;
    private bool _busy;
    private bool _closed;
    private readonly bool _focusPwd;
    private RemoteAppKind _selectedKind;
    private readonly TaskCompletionSource<bool> _closedTcs = new();

    public Task<bool> ClosedTask => _closedTcs.Task;

    private static readonly Brush SelBg = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush SelFg = Brushes.White;
    private static readonly Brush IdleBg = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));
    private static readonly Brush IdleFg = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xD4));

    public ConfirmCardWindow(RecognizedResult result, BitmapSource? thumb,
                             Func<RemoteAppKind, string, string, Task<ConnectResult>> connect,
                             Rect? anchor = null, string? statusOverride = null)
    {
        InitializeComponent();
        _result = result;
        _connect = connect;
        _focusPwd = !string.IsNullOrEmpty(statusOverride);

        // 默认软件：明确 ToDesk 选 ToDesk，其余（向日葵/未知）默认向日葵，回车即可连
        _selectedKind = result.Kind == RemoteAppKind.ToDesk ? RemoteAppKind.ToDesk : RemoteAppKind.Sunlogin;
        SetSelected(_selectedKind);

        // 设备码（深色 TextBox，白字可见）；认到多个长码时在下方给候选小按钮切换
        CodeBox.Text = result.DeviceCode;
        BuildCandidates(result.CandidateCodes, result.DeviceCode);

        // 密码框常驻：识别到就预填，没有就留空（水印提示留空＝免密），客服随时可补
        PwdBox.Text = result.Password;
        UpdatePlaceholder();
        // 截图缩略图已移除：白底纯码缩略会被误看成第二个设备码输入框。thumb 参数保留不展示。

        // 状态提示
        if (!string.IsNullOrEmpty(statusOverride))
            ShowStatus(statusOverride, "#FFB347");
        else if (!string.IsNullOrEmpty(result.Error))
            ShowStatus(result.Error, "#FFB347");
        else if (!result.BrandConfident)
            ShowStatus("没看清是哪个软件：点下面对应按钮直接连（码/密码可改）", "#FFB347");
        else
            ShowStatus("已识别，点软件按钮或按回车连接", "#4C8DFF");

        CountdownBar.Maximum = AutoCloseSeconds;
        CountdownBar.Value = AutoCloseSeconds;
        UpdateHint(AutoCloseSeconds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;

        // 任一交互都保持气泡，不再自动关闭
        CardBorder.MouseEnter += (_, _) => Hold();
        IsKeyboardFocusWithinChanged += (_, _) => { if (IsKeyboardFocusWithin) Hold(); };
        PwdBox.GotFocus += (_, _) => Hold();

        PlaceByAnchor(anchor);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        Services.Logger.Info($"确认卡显示 默认={_selectedKind switch { RemoteAppKind.ToDesk => "ToDesk", _ => "向日葵" }} " +
                             $"码={result.DeviceCode} 密码={(result.HasPassword ? result.Password : "<免密>")} " +
                             $"依据=[{result.BrandReason}]");
    }

    /// <summary>切换两个软件大按钮的选中高亮。</summary>
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
        SetSelected(RemoteAppKind.Sunlogin);
        Accept(RemoteAppKind.Sunlogin);
    }

    private void TdBtn_Click(object sender, RoutedEventArgs e)
    {
        SetSelected(RemoteAppKind.ToDesk);
        Accept(RemoteAppKind.ToDesk);
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
        _keyHook.EnterPressed += () => Dispatcher.BeginInvoke(new Action(() => Accept(_selectedKind)));
        _keyHook.EscapePressed += () => Dispatcher.BeginInvoke(new Action(() => Cancel("Esc")));
        try { _keyHook.Install(); }
        catch (Exception ex) { Services.Logger.Error("键盘钩子装载失败", ex); }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Services.Logger.Info($"确认气泡显示 Left={Left:F0} Top={Top:F0} {Width:F0}x{Height:F0}（8 秒无交互自动关闭）");
        _autoDeadline = DateTime.Now.AddSeconds(AutoCloseSeconds);
        _timer.Start();
        // 密码未通过/需要密码而自动弹出时，光标直接落密码框并全选，方便立刻改
        if (_focusPwd) { PwdBox.Focus(); PwdBox.SelectAll(); }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_held || _busy) { _timer.Stop(); return; }
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

    /// <summary>客服开始看/操作气泡：停止自动关闭并保持，等点软件按钮、回车或手动关。</summary>
    private void Hold()
    {
        if (_held) return;
        _held = true;
        // 构造期给 CodeBox/PwdBox 赋初值也会触发 TextChanged→Hold()，此刻 _timer 可能尚未创建
        _timer?.Stop();
        if (CountdownBar != null) CountdownBar.Visibility = Visibility.Collapsed;
        UpdateHint(null);
    }

    private string KindShortName(RemoteAppKind k) => k == RemoteAppKind.ToDesk ? "ToDesk" : "向日葵";

    private void UpdateHint(double? remain)
    {
        string enter = $"回车连{KindShortName(_selectedKind)}";
        string tail = " · 点软件名直连 · " + enter + " · Esc / × 关闭";
        HintText.Text = remain.HasValue
            ? $"{remain.Value:0} 秒后自动关闭{tail}"
            : tail.TrimStart(' ', '·');
    }

    private async void Accept(RemoteAppKind kind)
    {
        if (_busy) return;
        _timer.Stop();
        Hold();
        SetSelected(kind);

        var code = (CodeBox.Text ?? "").Trim().Replace(" ", "");
        var pwd = (PwdBox.Text ?? "").Trim();

        if (code.Length < 9)
        {
            ShowStatus("设备码不对（应 9~10 位数字），请在卡片上修改", "#FF6B6B");
            CodeBox.Focus();
            return;
        }

        _busy = true;
        ShowStatus($"正在打开{KindShortName(kind)}并连接…", "#4C8DFF");
        HintText.Text = "正在连接，请稍候";
        SetInputsEnabled(false);
        Services.Logger.Info($"卡片确认连接：{KindShortName(kind)} 码={code} 密码={(string.IsNullOrEmpty(pwd) ? "<免密>" : pwd)}");

        try
        {
            var r = await _connect(kind, code, pwd);
            if (r.Status == ConnectStatus.Launched)
            {
                _closedTcs.TrySetResult(true);
                Close();
                return;
            }
            if (r.Status == ConnectStatus.NeedPassword)
            {
                ShowStatus(r.Message, "#FFB347");
                HintText.Text = "补好密码后点软件按钮 / 回车重试 · Esc 取消";
                SetInputsEnabled(true);
                PwdBox.Focus();
                _busy = false;
                return;
            }
            // Failed
            ShowStatus(r.Message, "#FF6B6B");
            HintText.Text = "改好后点软件按钮 / 回车重试 · Esc 取消";
            SetInputsEnabled(true);
            _busy = false;
        }
        catch (Exception ex)
        {
            Services.Logger.Error("卡片连接异常", ex);
            ShowStatus("连接异常：" + ex.Message, "#FF6B6B");
            SetInputsEnabled(true);
            _busy = false;
        }
    }

    private void Cancel(string reason)
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        Services.Logger.Info($"卡片取消（{reason}），未连接 码={(CodeBox.Text ?? "").Trim().Replace(" ", "")}");
        _closedTcs.TrySetResult(false);
        Close();
    }

    private void CloseBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Cancel("手动关闭");

    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Hold();

    private void SetInputsEnabled(bool enabled)
    {
        SunBtn.IsEnabled = enabled;
        TdBtn.IsEnabled = enabled;
        CodeBox.IsEnabled = enabled;
        PwdBox.IsEnabled = enabled;
    }

    private void ShowStatus(string msg, string color)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new BrushConverter().ConvertFromString(color) as Brush;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        try { _keyHook.Dispose(); } catch { }
        _closed = true;
        _closedTcs.TrySetResult(false);
    }
}
