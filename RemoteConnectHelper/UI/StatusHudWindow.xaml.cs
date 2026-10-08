using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Hotkey;

namespace RemoteConnectHelper.UI;

/// <summary>
/// 速度优先（B 方案）的非阻塞状态小条：识别即连，不倒计时、不抢焦点。
/// 连接中给"取消连接"（点连接前=干净中止，点连接后=断开兜底）；
/// 成功一闪即关，失败/要密码才停下给"重试 / 手动改 / 关闭"。
/// </summary>
public partial class StatusHudWindow : Window
{
    public Action? OnCancel;
    public Action? OnRetry;
    public Action? OnManual;

    private ConnectionContext? _ctx;
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    private KeyHook? _escHook;
    private bool _connecting;

    public StatusHudWindow()
    {
        InitializeComponent();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 14;
        // 上抬到任务栏通知气泡（约占底部 0~100px）之上，避免开机启动气泡遮挡状态小条
        Top = wa.Bottom - Height - 108;
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); Close(); };
        // 连接中每秒重新置顶：远程软件主窗前置/也置顶时，保证“取消连接”按钮始终可点
        _topmostTimer.Tick += (_, _) =>
        {
            if (!_connecting) { _topmostTimer.Stop(); return; }
            Topmost = false;
            Topmost = true;
        };
        Closed += (_, _) => { DisarmEscape(); _topmostTimer.Stop(); };
    }

    /// <summary>连接中：全局钩 Esc＝取消连接（不钩回车，回车放行给前台远程软件）。</summary>
    private void ArmEscape()
    {
        if (_escHook != null) return;
        var hook = new KeyHook(hookEnter: false);
        hook.EscapePressed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_connecting && BtnCancel.IsEnabled)
                BtnCancel_Click(this, new RoutedEventArgs());
        }));
        try { hook.Install(); _escHook = hook; }
        catch (Exception ex) { Services.Logger.Error("状态小条 Esc 钩子装载失败", ex); }
    }

    /// <summary>离开“连接中”态立即卸载 Esc 钩子，避免吞掉后续远程会话里正常的 Esc。</summary>
    private void FinishConnecting()
    {
        _connecting = false;
        DisarmEscape();
        _topmostTimer.Stop();
    }

    private void DisarmEscape()
    {
        try { _escHook?.Dispose(); } catch { }
        _escHook = null;
    }

    /// <summary>绑定当前连接上下文（重试会换一个新的）。</summary>
    public void Bind(ConnectionContext ctx)
    {
        _ctx = ctx;
        _autoClose.Stop();
    }

    private static System.Windows.Media.Brush MakeBrush(string hex) =>
        (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(hex)!;

    /// <summary>进入"连接中"。</summary>
    public void SetConnecting(string appName, string code, string? password)
    {
        Dispatcher.Invoke(() =>
        {
            Dot.Fill = MakeBrush("#4C8DFF");
            TitleText.Text = $"正在连接 {appName}…";
            CodeText.Text = code;
            SubText.Text = string.IsNullOrEmpty(password)
                ? "免密连接 · 正在填码，点「取消连接」或按 Esc 中止"
                : $"密码 {password} · 正在填码，点「取消连接」或按 Esc 中止";
            ShowButtons(cancel: true);
            _connecting = true;
            ArmEscape();
            _topmostTimer.Start();
        });
    }

    /// <summary>已发起/成功：绿色，1.5s 后自动关。</summary>
    public void SetLaunched(string message, string code)
    {
        Dispatcher.Invoke(() =>
        {
            FinishConnecting();
            Dot.Fill = MakeBrush("#3AD17A");
            TitleText.Text = "已发起连接";
            CodeText.Text = code;
            SubText.Text = message;
            ShowButtons();
            _autoClose.Start();
        });
    }

    /// <summary>失败：红色停留，给重试/手动/关闭。</summary>
    public void SetFailed(string message, string code)
    {
        Dispatcher.Invoke(() =>
        {
            FinishConnecting();
            Dot.Fill = MakeBrush("#FF6B6B");
            TitleText.Text = "连接未成功";
            CodeText.Text = code;
            SubText.Text = message;
            ShowButtons(retry: true, manual: true, close: true);
        });
    }

    /// <summary>需要密码：橙色，给手动输入/关闭。</summary>
    public void SetNeedPassword(string message, string code)
    {
        Dispatcher.Invoke(() =>
        {
            FinishConnecting();
            Dot.Fill = MakeBrush("#FFB347");
            TitleText.Text = "需要密码";
            CodeText.Text = code;
            SubText.Text = message;
            ShowButtons(manual: true, close: true);
        });
    }

    /// <summary>用户取消：灰色，短暂提示后关。</summary>
    public void SetCancelled(string message, string code, bool disconnecting)
    {
        Dispatcher.Invoke(() =>
        {
            FinishConnecting();
            Dot.Fill = MakeBrush("#8A8A96");
            TitleText.Text = disconnecting ? "正在断开…" : "已取消";
            CodeText.Text = code;
            SubText.Text = message;
            ShowButtons(close: true);
            _autoClose.Interval = TimeSpan.FromMilliseconds(disconnecting ? 1800 : 1100);
            _autoClose.Start();
        });
    }

    private void ShowButtons(bool cancel = false, bool retry = false, bool manual = false, bool close = false)
    {
        BtnCancel.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        BtnRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        BtnManual.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        BtnClose.Visibility = close ? Visibility.Visible : Visibility.Collapsed;
        // 连接中只放一个取消按钮；重试/手动同时出现时取消按钮隐藏
        BtnCancel.IsEnabled = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        BtnCancel.IsEnabled = false;
        SubText.Text = "正在取消…";
        OnCancel?.Invoke();
    }

    private void BtnRetry_Click(object sender, RoutedEventArgs e)
    {
        BtnRetry.IsEnabled = false;
        OnRetry?.Invoke();
    }

    private void BtnManual_Click(object sender, RoutedEventArgs e)
    {
        OnManual?.Invoke();
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
