using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.Services;
using SD = System.Drawing;

namespace RemoteConnectHelper.Automation;

/// <summary>
/// 不依赖 UI Automation 的屏幕自动化：纯 Win32 选窗/前置 + 客户区截图 OCR/颜色定位 + 物理点击。
/// 实测向日葵(Flutter)/ToDesk 当前版本不向 UIA 暴露控件树，只能走这一套。
/// 所有等待点都接受 <see cref="ConnectionContext"/>：可被"取消"即时中断，并把固定死等改成就绪轮询（速度优先）。
/// </summary>
public static class ScreenOps
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref PT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    private const uint WM_CLOSE = 0x0010;

    [StructLayout(LayoutKind.Sequential)] private struct RC { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }

    public sealed class Win
    {
        public IntPtr Hwnd;
        public int X, Y, W, H; // 客户区屏幕矩形
        public override string ToString() => $"{Hwnd} ({X},{Y},{W}x{H})";
    }

    // ---------- 取消/等待辅助 ----------
    private static void CheckCancel(ConnectionContext? ctx) => ctx?.ThrowIfCancellationRequested();

    /// <summary>可中断等待；无 ctx 时退化为 Thread.Sleep。返回 false=被取消。</summary>
    private static bool Wait(ConnectionContext? ctx, int ms)
    {
        if (ctx == null) { Thread.Sleep(ms); return true; }
        return ctx.Sleep(ms);
    }

    /// <summary>按进程名+类名关键字找可见顶层窗（不用 MainWindowHandle，它会在多窗间漂移）。</summary>
    public static Win? FindWindow(string[] processNames, string[] classContains, string[] classExclude)
    {
        foreach (var pname in processNames)
        {
            foreach (var p in Process.GetProcessesByName(pname))
            {
                var w = FindInProcess((uint)p.Id, classContains, classExclude);
                if (w != null) return w;
            }
        }
        return null;
    }

    private static Win? FindInProcess(uint pid, string[] classContains, string[] classExclude)
    {
        var cands = new List<(IntPtr H, int W, int Hh, bool Vis, bool Ico)>();
        EnumWindows((h, _) =>
        {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp != pid) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            var cls = sb.ToString();
            if (classExclude.Any(e => cls.Contains(e))) return true;
            if (classContains.Length != 0 && !classContains.Any(c => cls.Contains(c))) return true;

            GetClientRect(h, out var rc0);
            cands.Add((h, rc0.R - rc0.L, rc0.B - rc0.T, IsWindowVisible(h), IsIconic(h)));
            return true;
        }, IntPtr.Zero);

        if (cands.Count == 0) return null;
        bool Land((int W, int Hh) c) => c.W >= c.Hh * 1.4 && c.W >= 500 && c.Hh >= 320;

        (IntPtr H, int W, int Hh, bool Vis, bool Ico) Pick(List<(IntPtr, int, int, bool, bool)> list, bool landOnly)
        {
            var q = landOnly ? list.Where(c => Land((c.Item2, c.Item3))) : list;
            return q.OrderByDescending(c => c.Item2 * c.Item3).FirstOrDefault();
        }

        var vis = cands.Where(c => c.Vis && !c.Ico).ToList();
        var chosen = Pick(vis, true);
        if (chosen.H == IntPtr.Zero) chosen = vis.Where(c => c.W * c.Hh >= 40000).OrderByDescending(c => c.W * c.Hh).FirstOrDefault();
        if (chosen.H == IntPtr.Zero) chosen = Pick(cands, true);
        if (chosen.H == IntPtr.Zero) chosen = cands.Where(c => c.W * c.Hh >= 40000).OrderByDescending(c => c.W * c.Hh).FirstOrDefault();
        return chosen.H == IntPtr.Zero ? null : ToWin(chosen.H);
    }

    private static Win? ToWin(IntPtr h)
    {
        if (!GetClientRect(h, out var rc)) return null;
        var pt = new PT();
        ClientToScreen(h, ref pt);
        return new Win { Hwnd = h, X = pt.X, Y = pt.Y, W = rc.R - rc.L, H = rc.B - rc.T };
    }

    /// <summary>最小化某组进程的所有可见顶层窗（连接前把"另一个"远程软件收起来，防止点错）。</summary>
    public static void MinimizeProcesses(string[] processNames)
    {
        foreach (var pname in processNames)
        {
            foreach (var p in Process.GetProcessesByName(pname))
            {
                EnumWindows((h, _) =>
                {
                    uint wp; GetWindowThreadProcessId(h, out wp);
                    if (wp == p.Id && IsWindowVisible(h)) ShowWindow(h, 6); // SW_MINIMIZE
                    return true;
                }, IntPtr.Zero);
            }
        }
    }

    /// <summary>启动 exe 并等待匹配窗口出现（250ms 快轮询，可取消）。</summary>
    public static Win? Launch(string exePath, string[] processNames, string[] classContains, string[] classExclude,
                              ConnectionContext? ctx = null, int timeoutMs = 12000)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath),
            });
        }
        catch (Exception ex) { Logger.Error($"启动失败 {exePath}", ex); return null; }

        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            CheckCancel(ctx);
            if (!Wait(ctx, 250)) break;
            var w = FindWindow(processNames, classContains, classExclude);
            if (w != null) return w;
        }
        return FindWindow(processNames, classContains, classExclude);
    }

    /// <summary>前置并强校验前台句柄；失败返回 false（调用方不得盲点）。首次等待更短以提速。</summary>
    public static bool EnsureForeground(Win win, ConnectionContext? ctx = null, int retries = 4)
    {
        for (int i = 0; i < retries; i++)
        {
            CheckCancel(ctx);
            ShowWindow(win.Hwnd, 1); // SW_SHOWNORMAL：隐藏到托盘或最小化都能恢复显示
            InputSimulator.BringToFront(win.Hwnd);
            if (!Wait(ctx, i == 0 ? 260 : 650)) break;
            if (GetForegroundWindow() == win.Hwnd)
            {
                // 刷新客户区矩形（窗口可能被移动/缩放）
                var fresh = ToWin(win.Hwnd);
                if (fresh != null) { win.X = fresh.X; win.Y = fresh.Y; win.W = fresh.W; win.H = fresh.H; }
                return true;
            }
        }
        return GetForegroundWindow() == win.Hwnd;
    }

    /// <summary>截取窗口客户区（前置成功后调用），返回 System.Drawing.Bitmap。</summary>
    public static SD.Bitmap CaptureClient(Win win)
    {
        var bmp = new SD.Bitmap(win.W, win.H, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
            g.CopyFromScreen(win.X, win.Y, 0, 0, new SD.Size(win.W, win.H), SD.CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>截主屏全屏（点连接后反馈可能在会话窗/弹窗，不能只看主窗客户区）。</summary>
    public static SD.Bitmap CaptureScreen()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new SD.Rectangle(0, 0, 1920, 1080);
        var bmp = new SD.Bitmap(b.Width, b.Height, SD.Imaging.PixelFormat.Format32bppArgb);
        using (var g = SD.Graphics.FromImage(bmp))
            g.CopyFromScreen(b.X, b.Y, 0, 0, new SD.Size(b.Width, b.Height), SD.CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static BitmapSource ToBitmapSource(SD.Bitmap bmp)
    {
        var h = bmp.GetHbitmap();
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    /// <summary>客户区坐标 -> 屏幕坐标并点击。</summary>
    public static void ClickClient(Win win, double cx, double cy, int settle = 160)
        => InputSimulator.Click(win.X + (int)cx, win.Y + (int)cy, settle);

    public static void Type(string text)
    {
        InputSimulator.ClearFocusedField();
        InputSimulator.TypeText(text);
    }

    /// <summary>
    /// 在指定区域找品牌蓝实心按钮，返回其中心（客户区坐标）。用于定位"连接"按钮。
    /// 条件：蓝高红低、足够大的实心蓝块，取面积最大者。
    /// </summary>
    public static (double X, double Y, double W, double H)? FindBlueButton(SD.Bitmap bmp, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(bmp.Width, x1); y1 = Math.Min(bmp.Height, y1);
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, SD.Imaging.ImageLockMode.ReadOnly, SD.Imaging.PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf = new byte[stride * bmp.Height];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(data);

        bool[,] mask = new bool[x1 - x0, y1 - y0];
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * stride + x * 4;
                int b = buf[i], g = buf[i + 1], r = buf[i + 2];
                if (b >= 175 && r <= 140 && (b - r) >= 80 && g >= 85 && g <= 200)
                    mask[x - x0, y - y0] = true;
            }

        // 连通域（4 邻域），取最大
        int ww = x1 - x0, hh = y1 - y0;
        bool[,] seen = new bool[ww, hh];
        (double X, double Y, double W, double H)? best = null;
        int bestArea = 0;
        var stack = new Stack<(int, int)>();
        for (int sy = 0; sy < hh; sy++)
            for (int sx = 0; sx < ww; sx++)
            {
                if (!mask[sx, sy] || seen[sx, sy]) continue;
                int minX = sx, maxX = sx, minY = sy, maxY = sy, area = 0;
                stack.Push((sx, sy)); seen[sx, sy] = true;
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    area++;
                    if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;
                    void Try(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= ww || ny >= hh || seen[nx, ny] || !mask[nx, ny]) return;
                        seen[nx, ny] = true; stack.Push((nx, ny));
                    }
                    Try(cx + 1, cy); Try(cx - 1, cy); Try(cx, cy + 1); Try(cx, cy - 1);
                }
                int bw = maxX - minX + 1, bh = maxY - minY + 1;
                // 按钮：宽>70 高>28，实心度>45%
                if (bw > 70 && bh > 28 && area > bw * bh * 0.45 && area > bestArea)
                {
                    bestArea = area;
                    best = (x0 + (minX + maxX) / 2.0, y0 + (minY + maxY) / 2.0, bw, bh);
                }
            }
        return best;
    }

    /// <summary>OCR 客户区截图。</summary>
    public static Task<OcrDoc?> OcrAsync(SD.Bitmap bmp)
        => OcrService.RecognizeAsync(ToBitmapSource(bmp));

    /// <summary>
    /// 恢复/前置窗口后循环截图 OCR，直到界面出现锚点文字（界面渲染完成）或超时。
    /// 进入立即探测一次（界面本就就绪时零等待），之后 300ms 快轮询；可取消。
    /// ToDesk 从托盘恢复会白屏数秒，必须等渲染完再定位，否则会盲点兜底坐标。
    /// 返回的 bmp 由调用方 Dispose。
    /// </summary>
    public static (SD.Bitmap Bmp, OcrDoc? Doc, bool Ready) WaitForReady(Win win, string[] anchors,
                                                                          ConnectionContext? ctx = null, int timeoutMs = 10000)
    {
        SD.Bitmap? bmp = null;
        OcrDoc? doc = null;
        int waited = 0;
        const int interval = 300;
        try
        {
            while (waited < timeoutMs)
            {
                CheckCancel(ctx);
                bmp?.Dispose();
                bmp = CaptureClient(win);
                doc = OcrAsync(bmp).GetAwaiter().GetResult();
                if (doc != null && anchors.Any(a => (doc.FlatText ?? "").Contains(a)))
                    return (bmp, doc, true);
                bmp.Dispose();
                bmp = null;
                if (!Wait(ctx, interval)) break;
                waited += interval;
            }
            CheckCancel(ctx);
            bmp = CaptureClient(win);
            doc = OcrAsync(bmp).GetAwaiter().GetResult();
            bool readyNow = doc != null && anchors.Any(a => (doc.FlatText ?? "").Contains(a));
            return (bmp, doc, readyNow);
        }
        catch (OperationCanceledException)
        {
            bmp?.Dispose();
            throw;
        }
        catch
        {
            bmp ??= CaptureClient(win);
            return (bmp, doc, false);
        }
    }

    /// <summary>已运行则复用，否则启动；otherProcesses 会被最小化以防点错。</summary>
    public static Win? Ensure(string[] exePaths, string[] processNames, string[] classContains, string[] classExclude,
                              string[] otherProcesses, ConnectionContext? ctx = null)
    {
        var win = FindWindow(processNames, classContains, classExclude);
        if (win == null)
        {
            foreach (var exe in exePaths)
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    win = Launch(exe, processNames, classContains, classExclude, ctx);
                    if (win != null) break;
                }
        }
        // 已驻留托盘但主窗被隐藏（不可见且非最小化）：再启动一次 exe 唤起单实例主窗，
        // 比单纯 ShowWindow 可靠（ToDesk 隐藏窗强拉会白屏不渲染）。
        if (win != null && !IsWindowVisible(win.Hwnd) && !IsIconic(win.Hwnd))
        {
            foreach (var exe in exePaths)
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    try
                    {
                        // 丢线程池启动并限时 3s：单实例软件二次启动（尤其白屏无响应时）
                        // ShellExecute 可能阻塞，绝不能卡住主流程；UseShellExecute=false 走 CreateProcess 立即返回。
                        var started = Task.Run(() =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = exe,
                                    UseShellExecute = false,
                                    WorkingDirectory = Path.GetDirectoryName(exe),
                                });
                            }
                            catch
                            {
                                try { Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true }); }
                                catch { }
                            }
                        });
                        if (!started.Wait(3000)) Logger.Warn($"唤起 {exe} 启动超时，继续后续流程");
                    }
                    catch { }
                    // 唤起后改快轮询：主窗一可见就继续（不再固定死等 1.8s）
                    int woken = 0;
                    while (woken < 2600)
                    {
                        CheckCancel(ctx);
                        if (!Wait(ctx, 200)) break;
                        woken += 200;
                        var w2 = FindWindow(processNames, classContains, classExclude);
                        if (w2 != null && IsWindowVisible(w2.Hwnd) && !IsIconic(w2.Hwnd)) { win = w2; break; }
                    }
                    var w3 = FindWindow(processNames, classContains, classExclude);
                    if (w3 != null) win = w3;
                    break;
                }
        }
        if (win == null) return null;
        try { MinimizeProcesses(otherProcesses); } catch { }
        return win;
    }

    // ---------- 断开兜底（点下连接后再"取消"） ----------

    private delegate bool TopWindowProc(IntPtr h, string cls, string title, bool visible, bool iconic, int w, int hh);

    /// <summary>枚举某组进程的全部顶层窗，把类名/标题/可见性/客户区尺寸交给回调。</summary>
    private static void EnumProcessTopWindows(string[] processNames, TopWindowProc cb)
    {
        foreach (var pname in processNames)
        {
            foreach (var p in Process.GetProcessesByName(pname))
            {
                uint pid = (uint)p.Id;
                EnumWindows((h, _) =>
                {
                    uint wp; GetWindowThreadProcessId(h, out wp);
                    if (wp != pid) return true;
                    var cs = new StringBuilder(256); GetClassName(h, cs, 256);
                    var ts = new StringBuilder(256); GetWindowText(h, ts, 256);
                    GetClientRect(h, out var rc);
                    bool cont = cb(h, cs.ToString(), ts.ToString(), IsWindowVisible(h), IsIconic(h),
                                   rc.R - rc.L, rc.B - rc.T);
                    return cont;
                }, IntPtr.Zero);
            }
        }
    }

    /// <summary>
    /// 已点下连接后用户取消 = 尽力断开：向日葵直接关会话副窗(FlutterMultiWindow)；
    /// ToDesk 关闭非主窗的会话窗（保留标题为 ToDesk 的横长主控台）并向当前界面发 Esc。
    /// </summary>
    public static void CloseSession(ConnectionContext ctx)
    {
        try
        {
            if (ctx.Kind == RemoteAppKind.Sunlogin)
            {
                EnumProcessTopWindows(new[] { "AweSun" }, (h, cls, title, vis, ico, w, hh) =>
                {
                    if (vis && cls.Contains("FlutterMultiWindow"))
                        PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return true;
                });
                Logger.Info("断开：已向向日葵会话窗(FlutterMultiWindow)发送 WM_CLOSE");
            }
            else if (ctx.Kind == RemoteAppKind.ToDesk)
            {
                EnumProcessTopWindows(new[] { "ToDesk" }, (h, cls, title, vis, ico, w, hh) =>
                {
                    long area = (long)w * hh;
                    bool isMainConsole = cls.Contains("H-SMILE") && title.Trim() == "ToDesk"
                                         && w >= hh * 1.4 && w >= 500 && hh >= 320;
                    if (vis && !ico && cls.Contains("H-SMILE") && !isMainConsole && area >= 40000)
                        PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return true;
                });
                InputSimulator.SendEscape();
                Logger.Info("断开：已关闭 ToDesk 会话副窗并发送 Esc");
            }
        }
        catch (Exception ex) { Logger.Error("CloseSession 失败", ex); }
    }
}
