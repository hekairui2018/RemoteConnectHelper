using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;

namespace RemoteConnectHelper.Automation;

/// <summary>鼠标点击 + SendKeys 输入。两个远程软件都不吃 UIA 赋值，只能物理模拟。</summary>
public static class InputSimulator
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x02;

    /// <summary>
    /// 把窗口拉到前台。不使用 AttachThreadInput——它会附加到当前前台线程，
    /// 若该线程（如白屏无响应的远控 UI）挂起，SetForegroundWindow 会永久阻塞。
    /// 改用 ShowWindow + SetForegroundWindow，失败时用一次 ALT 键绕过前台锁。
    /// </summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, 1); // SW_SHOWNORMAL
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd)
        {
            keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        Thread.Sleep(300);
    }

    /// <summary>在屏幕坐标处左键单击。</summary>
    public static void Click(int x, int y, int settleMs = 120)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(settleMs);
    }

    public static void ClickRect(System.Windows.Rect r, int settleMs = 120)
        => Click((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), settleMs);

    /// <summary>清空当前焦点输入框（Ctrl+A 全选 → Delete 一次删除；不再连发退格，省时间）。</summary>
    public static void ClearFocusedField()
    {
        SendKeys.SendWait("^a");
        Thread.Sleep(35);
        SendKeys.SendWait("{DELETE}");
        Thread.Sleep(35);
    }

    /// <summary>向当前前台窗口发送一次 Esc（取消连接中/关弹窗的兜底）。</summary>
    public static void SendEscape()
    {
        SendKeys.SendWait("{ESC}");
        Thread.Sleep(120);
    }

    /// <summary>向当前焦点输入框打字（转义 SendKeys 特殊字符）。</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendKeys.SendWait(Escape(text));
        Thread.Sleep(120);
    }

    private static string Escape(string s)
    {
        var special = new[] { '+', '^', '%', '~', '(', ')', '{', '}', '[', ']' };
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
        {
            if (special.Contains(c)) sb.Append('{').Append(c).Append('}');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static Rect? GetWindowRect(IntPtr hwnd)
    {
        if (GetWindowRect(hwnd, out var rc))
            return new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
        return null;
    }
}
