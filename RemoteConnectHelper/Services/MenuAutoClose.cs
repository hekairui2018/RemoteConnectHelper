using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RemoteConnectHelper.Services;

/// <summary>
/// 托盘/悬浮球右键菜单的自动关闭兜底。本程序是 WPF 宿主、主窗隐藏、悬浮球为
/// WS_EX_NOACTIVATE 不激活窗，WinForms ContextMenuStrip 默认 AutoClose 依赖的“窗口激活切换”
/// 在点悬浮球/桌面等不激活区域时不发生，菜单点外部不消失。这里统一做两件事，托盘菜单与
/// 悬浮球右键复用同一个 ContextMenuStrip，一处挂载两处生效：
/// 1) 点外部（任何非菜单窗口/桌面/任务栏/悬浮球）：低级鼠标钩子捕获，立即关闭；
/// 2) 鼠标只是停在菜单外（含刚右键弹出但鼠标不挪进菜单）：停留满 2 秒自动关闭；
///    鼠标在菜单内则一直保留，移出后计时、移回复位。Esc/点选菜单项仍走原生 AutoClose。
/// </summary>
public static class MenuAutoClose
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    /// <summary>鼠标停在菜单外多久后自动关闭。</summary>
    private static readonly TimeSpan HoverCloseDelay = TimeSpan.FromSeconds(2);
    /// <summary>悬停判定轮询间隔。</summary>
    private const int PollMs = 200;
    /// <summary>判定“鼠标在菜单内”时向外多算的边距，避免贴边误关。</summary>
    private const int EdgePad = 8;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public static void Attach(ContextMenuStrip menu)
    {
        IntPtr hook = IntPtr.Zero;
        IntPtr hMod = GetModuleHandle(null); // 低级鼠标钩子不注入 DLL，传当前模块句柄即可
        LowLevelMouseProc? proc = null;
        DateTime? outsideSince = null;

        var timer = new System.Windows.Forms.Timer { Interval = PollMs };
        timer.Tick += (_, _) =>
        {
            if (!menu.Visible) { timer.Stop(); return; }
            try
            {
                var rect = menu.RectangleToScreen(menu.ClientRectangle);
                rect.Inflate(EdgePad, EdgePad);
                var pos = Cursor.Position;
                if (rect.Contains(pos))
                {
                    outsideSince = null; // 鼠标在菜单内：保持打开
                }
                else
                {
                    outsideSince ??= DateTime.UtcNow; // 鼠标在菜单外：开始/继续计时
                    if (DateTime.UtcNow - outsideSince.Value >= HoverCloseDelay)
                        menu.Close(ToolStripDropDownCloseReason.AppFocusChange);
                }
            }
            catch { /* 菜单尚未完成布局等，本拍跳过，下拍再判 */ }
        };

        proc = (nCode, wParam, lParam) =>
        {
            if (nCode >= 0 && hook != IntPtr.Zero &&
                (wParam == (IntPtr)WM_LBUTTONDOWN ||
                 wParam == (IntPtr)WM_RBUTTONDOWN ||
                 wParam == (IntPtr)WM_MBUTTONDOWN))
            {
                try
                {
                    var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    IntPtr under = WindowFromPoint(data.pt);
                    var sb = new StringBuilder(256);
                    GetClassName(under, sb, sb.Capacity);
                    string cls = sb.ToString();

                    // 点在菜单窗口本身（类名含 ToolStrip，含边框）内不干预，交给 Click 与 AutoClose；
                    // 点到任何其他窗口、桌面、任务栏或不激活的悬浮球，立即关闭。
                    if (under == IntPtr.Zero ||
                        cls.IndexOf("ToolStrip", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        menu.BeginInvoke(new Action(() =>
                        {
                            try { if (menu.Visible) menu.Close(ToolStripDropDownCloseReason.AppFocusChange); }
                            catch { /* 菜单已释放等，忽略 */ }
                        }));
                    }
                }
                catch { /* 钩子回调内不得抛异常 */ }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        };

        menu.Opened += (_, _) =>
        {
            outsideSince = null;
            if (hook == IntPtr.Zero)
                hook = SetWindowsHookEx(WH_MOUSE_LL, proc, hMod, 0);
            timer.Start();
        };

        menu.Closed += (_, _) =>
        {
            timer.Stop();
            outsideSince = null;
            if (hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
        };
    }
}
