using System.Runtime.InteropServices;

namespace RemoteConnectHelper.Hotkey;

/// <summary>
/// 低级键盘钩子。确认卡存活时全局抓 Enter（立即连）/ Esc（停止/关闭）并吞键，避免前台聊天框回车误发消息；
/// 连接阶段只钩 Esc（＝停止连接）、回车放行给前台远程软件（hookEnter:false）。
/// 窗体不抢焦点（ShowActivated=false），故用全局钩子而不是键盘事件。
/// </summary>
public sealed class KeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int LLKHF_INJECTED = 0x10;
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
    public bool HookEnter { get; set; }
    private readonly HashSet<int> _pressedKeys = new();

    public event Action? EnterPressed;
    public event Action? EscapePressed;

    /// <param name="hookEnter">是否拦截回车：确认卡传 true（吞回车防误发消息）；
    /// 连接阶段传 false，只要 Esc 停止连接，回车必须放行给前台远程软件。</param>
    public KeyHook(bool hookEnter = true)
    {
        HookEnter = hookEnter;
        _proc = Callback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        using var cur = System.Diagnostics.Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(cur!.ModuleName!), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 自动化自身发送的按键必须放行，尤其是断开兜底发送的 Esc。
        if (nCode >= 0 && (Marshal.ReadInt32(lParam, 8) & LLKHF_INJECTED) == 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            bool down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            bool up = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
            bool capture = vk == VK_ESCAPE || (vk == VK_RETURN && HookEnter) || _pressedKeys.Contains(vk);
            if (capture && down)
            {
                // 每次按下只触发一次；长按的重复事件仍吞掉，松开后可再次使用。
                if (_pressedKeys.Add(vk))
                {
                    if (vk == VK_RETURN) EnterPressed?.Invoke();
                    else EscapePressed?.Invoke();
                }
                return (IntPtr)1;
            }
            if (capture && up && _pressedKeys.Remove(vk))
            {
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
        _pressedKeys.Clear();
    }
}
