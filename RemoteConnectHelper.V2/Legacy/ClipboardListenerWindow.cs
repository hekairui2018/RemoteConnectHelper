using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RemoteConnectHelper.UI;

/// <summary>
/// 隐藏消息窗：监听系统剪贴板更新（Win+Shift+S 截图、右键复制图片都会触发），
/// 去抖后抛 <see cref="ClipboardSettled"/>，由 App 做"认出远程码才自动连"的预检。
/// 普通截图/复制文字不会被放过去（预检在 App 侧）。
/// </summary>
public class ClipboardListenerWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;
    // 1300ms：刻意延后到客服截图后“立刻 Ctrl+V 粘贴”的高峰之后再读剪贴板，
    // 配合 Win32 毫秒级快取，最大限度避免与粘贴动作抢剪贴板锁。
    private const int DebounceMs = 250;

    private readonly DispatcherTimer _debounce;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>剪贴板静默约 700ms 后触发一次（连续复制只认最后一张）。</summary>
    public event Action? ClipboardSettled;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public ClipboardListenerWindow()
    {
        // 0 尺寸、透明、屏外、不抢焦点、不进任务栏：纯为拿一个能收 WM_CLIPboardUPDATE 的窗口句柄
        Width = 0;
        Height = 0;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Left = -10000;
        Top = -10000;
        IsHitTestVisible = false;
        Focusable = false;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            try { ClipboardSettled?.Invoke(); } catch { /* 预检异常不影响监听 */ }
        };
    }

    public void Start() => Show();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(_hwnd) is { } src)
            src.AddHook(WndProc);
        try { AddClipboardFormatListener(_hwnd); }
        catch (Exception ex) { Services.Logger.Error("注册剪贴板监听失败", ex); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // 去抖：截图/复制可能连续写多次剪贴板，每次都重置计时，停稳后只处理一次
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _debounce.Stop();
            if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
        }
        catch { /* ignore */ }
        base.OnClosed(e);
    }
}
