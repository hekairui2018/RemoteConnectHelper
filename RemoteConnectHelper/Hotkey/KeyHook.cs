using System.Runtime.InteropServices;

namespace RemoteConnectHelper.Hotkey;

/// <summary>
/// 低级键盘钩子，仅在确认卡片存活时装载。卡片不抢焦点（ShowActivated=false），
/// 用它在全局抓 Enter（立即连）/ Esc（取消），并吞掉这两个键，
/// 避免前台是聊天框时回车误发消息。
/// </summary>
public sealed class KeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr _hook = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;
    private bool _fired;

    public event Action? EnterPressed;
    public event Action? EscapePressed;

    public KeyHook()
    {
        _proc = Callback;
    }

    public void Install()
    {
        using var cur = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(System.Diagnostics.Process.GetCurrentProcess().MainModule!.ModuleName!), 0);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) && !_fired)
        {
            int vk = Marshal.ReadInt32(lParam);
            if (vk == VK_RETURN)
            {
                _fired = true;
                EnterPressed?.Invoke();
                return (IntPtr)1; // 吞掉，防止前台聊天框回车发消息
            }
            if (vk == VK_ESCAPE)
            {
                _fired = true;
                EscapePressed?.Invoke();
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
