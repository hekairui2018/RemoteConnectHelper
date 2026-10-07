using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Hotkey;
using RemoteConnectHelper.Recognition;

namespace RemoteConnectHelper.UI;

/// <summary>
/// 从悬浮球旁冒出的“气泡”确认卡：低置信/未识别/需补密码时弹出。
/// 弹出后 8 秒无人理会自动关闭（=取消，不连接）；鼠标移入、获得焦点、展开下拉、
/// 改码/改密码等任一交互即保持不消失，交给客服回车连接或 Esc/× 关闭。
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
    private readonly TaskCompletionSource<bool> _closedTcs = new();

    public Task<bool> ClosedTask => _closedTcs.Task;

    public ConfirmCardWindow(RecognizedResult result, BitmapSource? thumb,
                             Func<RemoteAppKind, string, string, Task<ConnectResult>> connect,
                             Rect? anchor = null)
    {
        InitializeComponent();
        _result = result;
        _connect = connect;

        AppBox.Items.Add("向日葵");
        AppBox.Items.Add("ToDesk");
        AppBox.SelectedIndex = result.Kind switch
        {
            RemoteAppKind.Sunlogin => 0,
            RemoteAppKind.ToDesk => 1,
            _ => -1,
        };

        // 设备码候选
        if (result.CandidateCodes.Count > 1)
        {
            foreach (var c in result.CandidateCodes) CodeBox.Items.Add(c);
        }
        CodeBox.Text = result.DeviceCode;

        // 密码 / 免密
        if (result.HasPassword)
        {
            PwdBox.Text = result.Password;
            PwdBox.Visibility = Visibility.Visible;
            PwdFree.Visibility = Visibility.Collapsed;
        }
        else
        {
            PwdBox.Visibility = Visibility.Collapsed;
            PwdFree.Visibility = Visibility.Collapsed;
            PwdFree.Text = "免密连接";
            PwdFree.Visibility = Visibility.Visible;
        }
        // 用户一旦在密码框输入就视为有密码
        PwdBox.TextChanged += (_, _) =>
        {
            Hold();
            if (!string.IsNullOrWhiteSpace(PwdBox.Text))
            {
                PwdBox.Visibility = Visibility.Visible;
                PwdFree.Visibility = Visibility.Collapsed;
            }
        };

        if (thumb != null) Thumb.Source = thumb;

        // 状态提示
        if (!string.IsNullOrEmpty(result.Error))
            ShowStatus(result.Error, "#FFB347");
        else if (!result.BrandConfident)
            ShowStatus("没看清是哪个软件，已默认选向日葵，核对软件/码后按回车连接", "#FFB347");
        else
            ShowStatus("已识别，核对无误后按回车连接", "#4C8DFF");

        CountdownBar.Maximum = AutoCloseSeconds;
        CountdownBar.Value = AutoCloseSeconds;
        HintText.Text = $"{AutoCloseSeconds:0} 秒后自动关闭 · 回车连接 · Esc / × 关闭";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;

        // 任一交互都保持气泡，不再自动关闭
        CardBorder.MouseEnter += (_, _) => Hold();
        IsKeyboardFocusWithinChanged += (_, _) => { if (IsKeyboardFocusWithin) Hold(); };
        AppBox.DropDownOpened += (_, _) => Hold();
        CodeBox.DropDownOpened += (_, _) => Hold();
        AppBox.SelectionChanged += (_, _) => Hold();

        PlaceByAnchor(anchor);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

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
        _keyHook.EnterPressed += () => Dispatcher.BeginInvoke(new Action(Accept));
        _keyHook.EscapePressed += () => Dispatcher.BeginInvoke(new Action(Cancel));
        try { _keyHook.Install(); }
        catch (Exception ex) { Services.Logger.Error("键盘钩子装载失败", ex); }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Services.Logger.Info($"确认气泡显示 Left={Left:F0} Top={Top:F0} {Width:F0}x{Height:F0}（8 秒无交互自动关闭）");
        _autoDeadline = DateTime.Now.AddSeconds(AutoCloseSeconds);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_held || _busy) { _timer.Stop(); return; }
        var remain = (_autoDeadline - DateTime.Now).TotalSeconds;
        if (remain <= 0)
        {
            _timer.Stop();
            Services.Logger.Info("确认气泡 8 秒无交互，自动关闭（不连接）");
            Cancel();
            return;
        }
        CountdownBar.Value = remain;
        HintText.Text = $"{remain:0} 秒后自动关闭 · 回车连接 · Esc / × 关闭";
    }

    /// <summary>客服开始看/操作气泡：停止自动关闭并保持，等回车或手动关。</summary>
    private void Hold()
    {
        if (_held) return;
        _held = true;
        _timer.Stop();
        CountdownBar.Visibility = Visibility.Collapsed;
        HintText.Text = "回车连接 · Esc 或点 × 关闭";
    }

    private RemoteAppKind SelectedKind() => AppBox.SelectedIndex switch
    {
        0 => RemoteAppKind.Sunlogin,
        1 => RemoteAppKind.ToDesk,
        _ => RemoteAppKind.Unknown,
    };

    private async void Accept()
    {
        if (_busy) return;
        _timer.Stop();
        Hold();

        var kind = SelectedKind();
        var code = (CodeBox.Text ?? "").Trim().Replace(" ", "");
        var pwd = (PwdBox.Text ?? "").Trim();

        if (kind == RemoteAppKind.Unknown)
        {
            ShowStatus("请先选择是向日葵还是 ToDesk", "#FFB347");
            return;
        }
        if (code.Length < 9)
        {
            ShowStatus("设备码不对（应 9~10 位数字），请在卡片上修改", "#FF6B6B");
            return;
        }

        _busy = true;
        ShowStatus("正在打开软件并连接…", "#4C8DFF");
        HintText.Text = "正在连接，请稍候";
        SetInputsEnabled(false);

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
                PwdBox.Visibility = Visibility.Visible;
                PwdFree.Visibility = Visibility.Collapsed;
                HintText.Text = "补好密码后按回车重试 · Esc 取消";
                SetInputsEnabled(true);
                _busy = false;
                return;
            }
            // Failed
            ShowStatus(r.Message, "#FF6B6B");
            HintText.Text = "改好后按回车重试 · Esc 取消";
            SetInputsEnabled(true);
            _busy = false;
        }
        catch (Exception ex)
        {
            ShowStatus("连接异常：" + ex.Message, "#FF6B6B");
            SetInputsEnabled(true);
            _busy = false;
        }
    }

    private void Cancel()
    {
        _timer.Stop();
        _closedTcs.TrySetResult(false);
        Close();
    }

    private void CloseBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Cancel();

    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Hold();

    private void SetInputsEnabled(bool enabled)
    {
        AppBox.IsEnabled = enabled;
        CodeBox.IsEnabled = enabled;
        PwdBox.IsEnabled = enabled;
    }

    private void ShowStatus(string msg, string color)
    {
        StatusText.Text = msg;
        StatusText.Foreground = new System.Windows.Media.BrushConverter().ConvertFromString(color)
                                as System.Windows.Media.Brush;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        try { _keyHook.Dispose(); } catch { }
        _closedTcs.TrySetResult(false);
    }
}
