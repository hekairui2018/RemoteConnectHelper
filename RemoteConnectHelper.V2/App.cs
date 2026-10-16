using System.Windows;
using RemoteConnectHelper.Hotkey;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.UI;
using Forms = System.Windows.Forms;

namespace RemoteConnectHelper.V2;

public sealed class App : System.Windows.Application
{
    private ShellWindow? _shell;
    private FloatingBall? _ball;
    private AssistantController? _controller;
    private HotkeyManager? _hotkey;
    private RemoteConnectHelper.UI.ClipboardListenerWindow? _clipboard;
    private Forms.NotifyIcon? _tray;
    private bool _exiting;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0) return Diagnostics.CommandLine.Run(args);
        using var mutex = new Mutex(true, "Local\\RemoteConnectHelperV2-SingleInstance", out bool owns);
        if (!owns) { System.Windows.MessageBox.Show("二代助手已在运行，请从托盘或悬浮球打开。", "二代远程助手"); return 0; }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log.Write("UI exception " + e.Exception.GetType().Name); e.Handled = true; app._controller?.ShowMessage("操作未完成，请重试；详情已写入本地日志。"); };
        app.Run();
        return 0;
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preferences = Preferences.Load(); _controller = new();
        _shell = new(_controller, preferences); MainWindow = _shell;
        _ball = new();
        _ball.ReadRequested += () => _ = ReadAsync(); _ball.OpenRequested += ShowShell; _ball.ExitRequested += () => _ = ExitAsync();
        _shell.BallRequested += () => { if (_ball.IsVisible) _ball.Hide(); else _ball.Show(); };
        _shell.HiddenByUser += () => _ball.Show();
        _hotkey = new(); _hotkey.Pressed += () => _ = ReadAsync();
        _shell.SetHotkeyAvailable(_hotkey.Register(0x0002 | 0x0001, 0x52));
        _clipboard = new();
        _clipboard.ClipboardSettled += () => { if (_shell.WatchClipboard && !_controller.Busy) _ = ReadAsync(); };
        _clipboard.Start();
        _tray = new Forms.NotifyIcon { Text = "二代远程助手", Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示助手", null, (_, _) => Dispatcher.BeginInvoke(new Action(ShowShell)));
        menu.Items.Add("读取截图", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = ReadAsync())));
        menu.Items.Add("退出二代助手", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = ExitAsync())));
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowShell));
        _shell.Show(); if (preferences.ShowFloatingBall) _ball.Show();
        Log.Write("二代启动 " + System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion);
    }
    private void ShowShell() { if (_shell == null || _exiting) return; _shell.Show(); if (_shell.WindowState == WindowState.Minimized) _shell.WindowState = WindowState.Normal; _shell.Activate(); }
    private async Task ReadAsync() { if (_shell == null || _exiting) return; ShowShell(); await _shell.ReadClipboardAsync(); }
    private async Task ExitAsync()
    {
        if (_exiting) return; _exiting = true;
        try
        {
            _hotkey?.Dispose(); _clipboard?.Close();
            if (_controller != null) await _controller.DisposeAsync();
            if (_shell != null) { _shell.CanClose = true; _shell.Close(); }
            _ball?.Close(); if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }
        finally { Shutdown(); }
    }
}
