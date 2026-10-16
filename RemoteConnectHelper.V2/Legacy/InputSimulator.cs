using System.Runtime.InteropServices;
using System.Windows;

namespace RemoteConnectHelper.Automation;

/// <summary>鼠标点击 + Win32 键盘输入。文本按 Unicode 发送，避免输入法改写设备码或密码。</summary>
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
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public INPUTUNION Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x02;
    private const uint KEYEVENTF_UNICODE = 0x04;

    private static INPUT Key(ushort vk, uint flags = 0, ushort character = 0) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Data = new INPUTUNION { Keyboard = new KEYBDINPUT { VirtualKey = vk, ScanCode = character, Flags = flags } }
    };

    private static void Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "模拟键盘输入失败，请确认远程软件可接收输入");
    }

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
        Send(new[] { Key(0x11), Key(0x41), Key(0x41, KEYEVENTF_KEYUP), Key(0x11, KEYEVENTF_KEYUP) });
        Thread.Sleep(35);
        Send(new[] { Key(0x2E), Key(0x2E, KEYEVENTF_KEYUP) });
        Thread.Sleep(35);
    }

    /// <summary>向当前前台窗口发送一次 Esc（取消连接中/关弹窗的兜底）。</summary>
    public static void SendEscape()
    {
        Send(new[] { Key(0x1B), Key(0x1B, KEYEVENTF_KEYUP) });
        Thread.Sleep(120);
    }

    /// <summary>原样输入 UTF-16 字符，不走键盘布局或输入法，也无需对密码特殊字符转义。</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new INPUT[checked(text.Length * 2)];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Key(0, KEYEVENTF_UNICODE, text[i]);
            inputs[i * 2 + 1] = Key(0, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, text[i]);
        }
        Send(inputs);
        Thread.Sleep(120);
    }

    public static Rect? GetWindowRect(IntPtr hwnd)
    {
        if (GetWindowRect(hwnd, out var rc))
            return new Rect(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top);
        return null;
    }
}
