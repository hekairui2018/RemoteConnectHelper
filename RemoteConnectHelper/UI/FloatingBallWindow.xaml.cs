using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
// 工程同时启用 WPF 与 WinForms，显式消歧（Point / MouseEventArgs 两套命名空间都有）
using RemoteConnectHelper.Services;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace RemoteConnectHelper.UI;

/// <summary>
/// 北斗七星悬浮球：常驻屏幕边缘的“识别连接”按钮。
/// 单击 = 识别剪贴板并连接（等同 Ctrl+Alt+R）；按住拖动换位并记忆；右键出菜单。
/// 双击桌面图标只负责启动后台/把球唤到最前，不触发识别（见 App 的 IPC 处理）。
/// 完全静止，不做呼吸/眨眼等动画。
/// </summary>
public partial class FloatingBallWindow : Window
{
    public Action? OnRecognize;
    /// <summary>右键球：请求宿主（App）弹出与托盘共用的 WinForms 菜单（点外部/Esc/选完即关）。</summary>
    public Action? OnRightClick;

    /// <summary>拖动结束，回传新的 Left/Top（DIP）供持久化。</summary>
    public event Action<double, double>? PositionChanged;

    private const double BallSize = 84;
    private const double DragThreshold = 5;

    private bool _down;
    private bool _moved;
    private Point _startScreen;
    private double _startLeft;
    private double _startTop;

    public FloatingBallWindow()
    {
        InitializeComponent();
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>悬浮球永不抢焦点：点它识别连接时不把客服当前窗口/输入打断，也不进 Alt+Tab/任务栏。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        long ex = GetWindowLongPtr(helper.Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(helper.Handle, GWL_EXSTYLE,
            new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// <summary>球当前的屏幕矩形（DIP），供确认气泡锚定定位。</summary>
    public Rect BoundsRect => new Rect(Left, Top, BallSize, BallSize);

    /// <summary>按记忆位置摆放；没有记忆或已在屏外（分辨率变化）时用默认右下角（上抬给灵眸球让位）。</summary>
    public void ApplyPosition(double? left, double? top)
    {
        if (left.HasValue && top.HasValue && IsInWorkArea(left.Value, top.Value))
        {
            Left = left.Value;
            Top = top.Value;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - BallSize - 18;
            Top = wa.Bottom - BallSize - 150;
        }
    }

    /// <summary>把球显示并提到最前（不抢焦点、不中断当前操作）。</summary>
    public void ShowBall()
    {
        if (!IsInWorkArea(Left, Top)) ApplyPosition(null, null);
        if (!IsVisible) Show();
        Topmost = false;
        Topmost = true;
        Ball.Opacity = 1;
    }

    private static bool IsInWorkArea(double l, double t)
    {
        var wa = SystemParameters.WorkArea;
        return l >= wa.Left - BallSize / 2.0
            && t >= wa.Top - BallSize / 2.0
            && l <= wa.Right - 24
            && t <= wa.Bottom - 24;
    }

    private static double ClampX(double x)
    {
        var wa = SystemParameters.WorkArea;
        return Math.Min(Math.Max(x, wa.Left), wa.Right - BallSize);
    }

    private static double ClampY(double y)
    {
        var wa = SystemParameters.WorkArea;
        return Math.Min(Math.Max(y, wa.Top), wa.Bottom - BallSize);
    }

    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _down = true;
        _moved = false;
        _startScreen = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        Ball.CaptureMouse();
        Ball.Opacity = 0.78;
    }

    private void Ball_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_down || e.LeftButton != MouseButtonState.Pressed) return;

        var p = PointToScreen(e.GetPosition(this));
        double sx = 1, sy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            sx = src.CompositionTarget.TransformToDevice.M11;
            sy = src.CompositionTarget.TransformToDevice.M22;
        }
        double dx = (p.X - _startScreen.X) / sx;
        double dy = (p.Y - _startScreen.Y) / sy;

        if (!_moved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold) return;
        if (!_moved) Logger.Info("悬浮球开始拖动（按住移动中，若此后无“拖动结束”即粘住）");
        _moved = true;
        Left = ClampX(_startLeft + dx);
        Top = ClampY(_startTop + dy);
    }

    private void Ball_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_down) return;
        _down = false;
        Ball.Opacity = 1;
        if (Ball.IsMouseCaptured) Ball.ReleaseMouseCapture();

        if (!_moved)
        {
            // 单击（无拖动）= 识别并连接
            OnRecognize?.Invoke();
        }
        else
        {
            Left = ClampX(Left);
            Top = ClampY(Top);
            Logger.Info($"悬浮球拖动结束 Left={Left:F0} Top={Top:F0}");
            PositionChanged?.Invoke(Left, Top);
        }
    }

    // 右键不在此弹 WPF ContextMenu：悬浮球是 WS_EX_NOACTIVATE 不激活窗，WPF 菜单收不到"点外部关闭"会卡住；
    // 改为通知 App 弹出与托盘共用的 WinForms ContextMenuStrip（ToolStripDropDown 点外部/Esc/选完自动关）。
    private void Grid_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnRightClick?.Invoke();
    }
}
