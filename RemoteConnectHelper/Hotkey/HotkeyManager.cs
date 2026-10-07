using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RemoteConnectHelper.Hotkey;

/// <summary>全局热键：用 message-only 窗口接收 WM_HOTKEY。</summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private HwndSource? _src;
    private IntPtr _hwnd;
    private readonly int _id = 9001;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>注册热键。成功返回 true；失败（被占用）返回 false。</summary>
    public bool Register(uint modifiers, uint vk)
    {
        EnsureWindow();
        Unregister();

        bool ok = RegisterHotKey(_hwnd, _id, modifiers | MOD_NOREPEAT, vk);
        _registered = ok;
        return ok;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, _id);
            _registered = false;
        }
    }

    private void EnsureWindow()
    {
        if (_src != null) return;
        var p = new HwndSourceParameters("RCHotkeyMessageWindow")
        {
            WindowStyle = 0,   // WS_OVERLAPPED，不创建 WS_VISIBLE，窗口不可见
            Width = 0,
            Height = 0,
        };
        _src = new HwndSource(p);
        SetParent(_src.Handle, HWND_MESSAGE); // 挂到 message-only，不收任务栏/窗口列表影响
        _src.AddHook(WndProc);
        _hwnd = _src.Handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            try { Pressed?.Invoke(); } catch { /* 吞掉，热键线程不能崩 */ }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _src?.RemoveHook(WndProc);
        _src?.Dispose();
        _src = null;
    }
}
