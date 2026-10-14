using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.Services;
using SD = System.Drawing;

namespace RemoteConnectHelper.Automation;

public enum ConnectStatus { Launched, NeedPassword, Failed, Cancelled }

public sealed class ConnectResult
{
    public ConnectStatus Status { get; init; }
    public string Message { get; init; } = "";
    public static ConnectResult Ok(string msg = "已发起连接") => new() { Status = ConnectStatus.Launched, Message = msg };
    public static ConnectResult NeedPwd(string msg) => new() { Status = ConnectStatus.NeedPassword, Message = msg };
    public static ConnectResult Fail(string msg) => new() { Status = ConnectStatus.Failed, Message = msg };
    public static ConnectResult Cancel(string msg = "已取消") => new() { Status = ConnectStatus.Cancelled, Message = msg };
}

public interface IRemoteConnector
{
    Task<ConnectResult> ConnectAsync(string deviceCode, string password, ConnectionContext? ctx = null);
}

/// <summary>UIA / SendKeys 需在带消息循环的 STA 线程跑。</summary>
internal static class StaRunner
{
    public static Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var th = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.IsBackground = true;
        th.Start();
        return tcs.Task;
    }
}

/// <summary>可中断等待：无 ctx 退化为 Thread.Sleep（旧行为），有 ctx 则取消即时返回。</summary>
internal static class ConnWait
{
    public static bool Sleep(ConnectionContext? ctx, int ms)
    {
        if (ctx == null) { Thread.Sleep(ms); return true; }
        return ctx.Sleep(ms);
    }
}

/// <summary>OCR 行/文字簇/数字段定位的通用辅助。</summary>
internal static class OcrGeo
{
    public sealed class Line
    {
        public double Y, X0, X1;
        public string Text = "";
        public List<OcrWord> Words = new();
    }

    public static List<Line> BuildLines(OcrDoc doc)
    {
        var lines = new List<Line>();
        foreach (var w in doc.Words.OrderBy(w => w.Y))
        {
            var hit = lines.FirstOrDefault(l => Math.Abs(l.Y - w.CenterY) <= 16);
            if (hit == null)
            {
                hit = new Line { Y = w.CenterY };
                lines.Add(hit);
            }
            hit.Words.Add(w);
            hit.Y = hit.Words.Average(x => x.CenterY);
        }
        foreach (var l in lines)
        {
            l.Words = l.Words.OrderBy(x => x.X).ToList();
            l.Text = string.Concat(l.Words.Select(x => x.Text));
            l.X0 = l.Words.Min(x => x.X);
            l.X1 = l.Words.Max(x => x.X + x.W);
        }
        return lines.OrderBy(l => l.Y).ToList();
    }

    public static Line? FindLineContains(IEnumerable<Line> lines, params string[] keys)
        => lines.FirstOrDefault(l => keys.Any(k => l.Text.Contains(k)));

    /// <summary>
    /// 在某条水平带（yC±tol）内，把词按 x 排序拼成串，定位 substring 所在的连续词，
    /// 返回这些词的包围盒中心。用于点空框里的中文占位文字（如"伙伴识别码""可为空"）。
    /// </summary>
    public static (double Cx, double Cy, double X0, double X1)? FindCluster(OcrDoc doc, double yC, double tol, string key, double xMin = 0)
    {
        var ws = doc.Words
            .Where(w => Math.Abs(w.CenterY - yC) <= tol && w.CenterX >= xMin)
            .OrderBy(w => w.X).ToList();
        if (ws.Count == 0) return null;
        var sb = new StringBuilder();
        var map = new List<OcrWord>();
        foreach (var w in ws)
            foreach (var ch in w.Text) { sb.Append(ch); map.Add(w); }
        int idx = sb.ToString().IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var hit = map.Skip(idx).Take(key.Length).Distinct().ToList();
        double x0 = hit.Min(w => w.X), x1 = hit.Max(w => w.Right);
        return (hit.Average(w => w.CenterX), hit.Average(w => w.CenterY), x0, x1);
    }

    private static readonly Regex NumSeg = new(@"^\d{3,4}$", RegexOptions.Compiled);

    /// <summary>在给定 y 区间内找连续数字段（框里的历史/占位码），返回其整体中心与行中心 y。</summary>
    public static (double Cx, double Cy, double X0, double X1)? NumberField(IEnumerable<Line> lines, double yMin, double yMax)
    {
        List<OcrWord> best = new();
        foreach (var l in lines)
        {
            if (l.Y < yMin || l.Y > yMax) continue;
            var nums = l.Words.Where(w => NumSeg.IsMatch((w.Text ?? "").Trim())).OrderBy(w => w.X).ToList();
            if (nums.Count < 2) continue;
            var group = new List<OcrWord> { nums[0] };
            for (int i = 1; i < nums.Count; i++)
            {
                var prev = nums[i - 1];
                double gap = nums[i].X - prev.Right;
                if (gap < prev.H * 1.4 + 26) group.Add(nums[i]);
            }
            if (group.Count >= 2 && group.Count > best.Count) best = group;
        }
        if (best.Count == 0) return null;
        double x0 = best.Min(w => w.X), x1 = best.Max(w => w.Right);
        return ((x0 + x1) / 2.0, best.Average(w => w.CenterY), x0, x1);
    }

    public static bool ContainsAny(OcrDoc doc, params string[] keys)
        => keys.Any(k => (doc.FlatText ?? "").Contains(k));
}

internal static class CoordOps
{
    /// <summary>
    /// 点输入框并等其获得焦点。Flutter 自绘框首次聚焦有延迟，等太短会丢键。
    /// 实测 300ms 偶发丢键、触发约 2s 慢速重填反而更慢；380ms 一次成功率高，端到端更快。
    /// 调用方在"连接按钮未变亮"时仍会以 slow=true（620ms）重填一次兜底。
    /// </summary>
    public static void ClickField(ScreenOps.Win win, double cx, double cy, ConnectionContext? ctx = null, bool slow = false)
    {
        ctx?.ThrowIfCancellationRequested();
        ScreenOps.ClickClient(win, cx, cy, slow ? 160 : 80);
        ConnWait.Sleep(ctx, slow ? 620 : 380);
        ctx?.ThrowIfCancellationRequested();
    }

    public static void Fill(ScreenOps.Win win, double cx, double cy, string text,
                            ConnectionContext? ctx = null, bool slow = false)
    {
        ClickField(win, cx, cy, ctx, slow);
        ScreenOps.Type(text);
        ConnWait.Sleep(ctx, slow ? 180 : 100);
    }
}

/* ============================ 向日葵（Flutter，深色新版） ============================ */
public sealed class SunloginConnector : IRemoteConnector
{
    private static readonly string[] Procs = { "AweSun" };
    private static readonly string[] Cls = { "FLUTTER_RUNNER_WIN32_WINDOW" };
    private static readonly string[] ClsExclude = { "FlutterMultiWindow" };
    private static readonly string[] Others = { "ToDesk" };

    private static readonly string[] Errors =
    {
        "不在线", "不存在", "验证码错误", "密码错误", "连接失败", "无法连接", "已离线",
        "设备离线", "识别码错误", "验证失败", "无人接听", "拒绝连接"
    };

    public Task<ConnectResult> ConnectAsync(string deviceCode, string password, ConnectionContext? ctx = null)
        => StaRunner.Run(() => Connect(deviceCode, password, ctx));

    private ConnectResult Connect(string deviceCode, string password, ConnectionContext? ctx)
    {
        var exe = AppPaths.FindSunlogin();
        var win = ScreenOps.Ensure(exe == null ? Array.Empty<string>() : new[] { exe },
                                   Procs, Cls, ClsExclude, Others, ctx);
        if (win == null)
        {
            Logger.Warn("向日葵 Ensure 未返回主窗口，连接中止（详见上方 启动/驻留/重启 日志）");
            return ConnectResult.Fail("没找到向日葵主窗口，请确认已安装（AweSun.exe）；若向日葵缩在托盘仍失败，请完全退出向日葵后重试");
        }
        if (!ScreenOps.EnsureForeground(win, ctx))
            return ConnectResult.Fail("没法把向日葵调到最前（可能被弹窗挡住），为避免连错已取消，请手动切到向日葵再试");

        var (readyBmp, readyDoc, ready) = ScreenOps.WaitForReady(win, new[] { "远程协助", "识别码", "连接" }, ctx);
        double codeCx = 0, codeCy = 0, pwdCx = 0, pwdCy = 0, rowY = 0;
        bool havePwdBox = false;
        using (readyBmp)
        {
            var doc = readyDoc;
            if (!ready || doc == null) return ConnectResult.Fail("向日葵主界面未加载，已取消自动连接（请手动打开软件后重试）");
            var lines = OcrGeo.BuildLines(doc);
            var title = OcrGeo.FindLineContains(lines, "远程协助他人", "协助他人");
            double titleY = title?.Y ?? win.H * 0.39;
            rowY = titleY + 63;                        // 输入框在标题下方约 63px
            const double tol = 34;

            // 设备码框：优先框内历史数字，否则点占位文字"伙伴识别码"
            var num = OcrGeo.NumberField(lines, titleY + 30, titleY + 100);
            var ph = OcrGeo.FindCluster(doc, rowY, tol, "伙伴识别码");
            if (num != null) { codeCx = num.Value.Cx; codeCy = num.Value.Cy; }
            else if (ph != null) { codeCx = ph.Value.Cx; codeCy = ph.Value.Cy; }
            else return ConnectResult.Fail("没定位到向日葵的伙伴识别码输入框（界面可能改版）");

            // 验证码框：同一行右侧占位"可为空"
            var pwdPh = OcrGeo.FindCluster(doc, rowY, tol, "可为空", codeCx + 60);
            if (pwdPh != null) { pwdCx = pwdPh.Value.Cx; pwdCy = pwdPh.Value.Cy; havePwdBox = true; }
            else { pwdCx = codeCx + 225; pwdCy = codeCy; } // 兜底：设备码框右侧

            Logger.Info($"向日葵 定位 设备码@({codeCx:F0},{codeCy:F0}) 验证码框={(havePwdBox ? $"({pwdCx:F0},{pwdCy:F0})" : "无")}");

            FillCredentials(win, codeCx, codeCy, pwdCx, pwdCy, deviceCode, password, ctx);
        }

        // 填完码后连接按钮才由禁用灰变亮蓝，重新截图定位；找不到亮蓝（疑似丢键）就慢速重填一次
        ConnWait.Sleep(ctx, 120);
        ctx?.ThrowIfCancellationRequested();

        SD.Bitmap bmp2 = ScreenOps.CaptureClient(win);
        try
        {
            var (btnX, btnY, refilled) = LocateConnectButton(win, bmp2, rowY, () =>
            {
                Logger.Warn("向日葵 未检测到亮蓝连接按钮，疑似填码丢失，慢速重填一次");
                FillCredentials(win, codeCx, codeCy, pwdCx, pwdCy, deviceCode, password, ctx, slow: true);
            });

            ctx?.ThrowIfCancellationRequested();   // 点连接前最后一道闸：此后取消按"断开"处理
            ctx?.MarkConnectClicked();
            ScreenOps.ClickClient(win, btnX, btnY, 220);
            Logger.Info($"向日葵 已点连接 @({btnX:F0},{btnY:F0}) refilled={refilled}");
        }
        finally { bmp2.Dispose(); }

        return PostConnect(ctx);
    }

    private static void FillCredentials(ScreenOps.Win win, double codeCx, double codeCy,
                                        double pwdCx, double pwdCy, string deviceCode, string password,
                                        ConnectionContext? ctx, bool slow = false)
    {
        CoordOps.Fill(win, codeCx, codeCy, deviceCode, ctx, slow);
        Logger.Info($"向日葵 填入识别码 {deviceCode}");
        // 空密码也必须清空验证码框；正常填写和慢速重填使用同一规则。
        CoordOps.Fill(win, pwdCx, pwdCy, password, ctx, slow);
        Logger.Info(string.IsNullOrEmpty(password) ? "向日葵 已清空验证码（免密）" : "向日葵 填入验证码");
    }

    /// <summary>找亮蓝连接按钮；正常路径直接复用定位阶段算好的输入行 y（不再 OCR，省约 300ms）；
    /// 找不到先 refill 重填，再截图并 OCR 重算 y；仍找不到用客户区比例兜底。</summary>
    private static (double X, double Y, bool Refilled) LocateConnectButton(
        ScreenOps.Win win, SD.Bitmap bmp, double guessY, Action refill)
    {
        var btn = ScreenOps.FindBlueButton(bmp, (int)(win.W * 0.55), (int)(guessY - 36), win.W, (int)(guessY + 40));
        if (btn != null) return (btn.Value.X, btn.Value.Y, false);

        refill();
        using var b2 = ScreenOps.CaptureClient(win);
        Thread.Sleep(120);
        double gy = guessY;
        var d2 = ScreenOps.OcrAsync(b2).GetAwaiter().GetResult();
        if (d2 != null)
        {
            var t = OcrGeo.FindLineContains(OcrGeo.BuildLines(d2), "远程协助他人", "协助他人");
            if (t != null) gy = t.Y + 63;
        }
        var btn2 = ScreenOps.FindBlueButton(b2, (int)(win.W * 0.55), (int)(gy - 36), win.W, (int)(gy + 40));
        if (btn2 != null) return (btn2.Value.X, btn2.Value.Y, true);
        return (win.W * 0.792, gy, true); // 兜底：客户区 79.2% 宽
    }

    private ConnectResult PostConnect(ConnectionContext? ctx)
    {
        // 轮询：错误词/验证码优先（首轮即 OCR）；会话窗需连续两次探到（防抖，避免残留窗秒判）；可取消。
        const int budget = 6000, step = 300, ocrEvery = 700;
        int elapsed = 0, sinceOcr = ocrEvery;
        bool sessionSeen = false;
        while (elapsed < budget)
        {
            ctx?.ThrowIfCancellationRequested();

            if (sinceOcr >= ocrEvery)
            {
                sinceOcr = 0;
                using var full = ScreenOps.CaptureScreen();
                var doc = ScreenOps.OcrAsync(full).GetAwaiter().GetResult();
                if (doc != null)
                {
                    var lines = OcrGeo.BuildLines(doc);
                    var errLine = lines.FirstOrDefault(l => Errors.Any(e => l.Text.Contains(e)));
                    if (errLine != null)
                    {
                        Logger.Warn("向日葵连接返回错误：" + errLine.Text);
                        return ConnectResult.Fail(errLine.Text.Length > 24 ? "向日葵返回：" + errLine.Text : errLine.Text);
                    }
                    if (OcrGeo.ContainsAny(doc, "请输入验证码", "输入验证码", "安全验证"))
                        return ConnectResult.NeedPwd("对方要求验证码，截图里没给，请在向日葵里手动输入");
                }
            }

            if (ScreenOps.FindWindow(Procs, new[] { "FlutterMultiWindow" }, Array.Empty<string>()) != null)
            {
                if (sessionSeen) return ConnectResult.Ok("正在连接对方（已打开向日葵会话窗）");
                sessionSeen = true;
            }
            else sessionSeen = false;

            if (!ConnWait.Sleep(ctx, step)) break;
            elapsed += step; sinceOcr += step;
        }

        // 预算耗尽仍无会话窗：末次全屏 OCR 判定，无错误则按"已发起"处理
        using var last = ScreenOps.CaptureScreen();
        var lastDoc = ScreenOps.OcrAsync(last).GetAwaiter().GetResult();
        if (lastDoc == null) return ConnectResult.Ok("已发起向日葵连接");
        var lastErr = OcrGeo.BuildLines(lastDoc).FirstOrDefault(l => Errors.Any(e => l.Text.Contains(e)));
        if (lastErr != null)
            return ConnectResult.Fail(lastErr.Text.Length > 24 ? "向日葵返回：" + lastErr.Text : lastErr.Text);
        if (OcrGeo.ContainsAny(lastDoc, "请输入验证码", "输入验证码", "安全验证"))
            return ConnectResult.NeedPwd("对方要求验证码，截图里没给，请在向日葵里手动输入");
        return ConnectResult.Ok("已发起向日葵连接");
    }
}

/* ============================ ToDesk（浅色，两步） ============================ */
public sealed class ToDeskConnector : IRemoteConnector
{
    private static readonly string[] Procs = { "ToDesk" };
    private static readonly string[] Cls = { "H-SMILE-FRAME" };
    private static readonly string[] ClsExclude = Array.Empty<string>();
    private static readonly string[] Others = { "AweSun" };

    private static readonly string[] Errors =
    {
        "不在线", "不存在", "连接失败", "无法连接", "已离线", "设备离线", "代码错误",
        "识别码错误", "密码错误", "频繁", "有误", "无法找到", "目标设备"
    };
    // “输入连接密码”弹窗独有字样（主控台自身的“安全密码连接”不算，避免还没弹窗就误触发）
    private static readonly string[] PwdDialogHints = { "输入连接密码", "立即连接", "免密连接" };
    // 密码没通过时弹窗内红字特征（比 Errors 更聚焦，用于弹窗内快速判密码不对、立刻弹改密卡）
    private static readonly string[] PwdErrHints =
        { "密码错误", "请重新输入", "重新输入", "密码不正确", "密码有误", "密码不对",
          "验证码错误", "验证码不正确", "验证失败", "不正确" };
    // 主控台特征词：进入会话/弹窗后这些会消失
    private static readonly string[] ConsoleAnchors = { "远程控制设", "设备代码", "允许控制" };

    public Task<ConnectResult> ConnectAsync(string deviceCode, string password, ConnectionContext? ctx = null)
        => StaRunner.Run(() => Connect(deviceCode, password, ctx));

    private ConnectResult Connect(string deviceCode, string password, ConnectionContext? ctx)
    {
        var exe = AppPaths.FindToDesk();
        var win = ScreenOps.Ensure(exe == null ? Array.Empty<string>() : new[] { exe },
                                   Procs, Cls, ClsExclude, Others, ctx);
        if (win == null)
        {
            Logger.Warn("ToDesk Ensure 未返回主窗口，连接中止（详见上方 启动/驻留/重启 日志）");
            return ConnectResult.Fail("没找到 ToDesk 主窗口，请确认已安装（ToDesk.exe）；若 ToDesk 缩在托盘仍失败，请完全退出后重试");
        }
        if (!ScreenOps.EnsureForeground(win, ctx))
            return ConnectResult.Fail("没法把 ToDesk 调到最前（可能被弹窗挡住），为避免连错已取消，请手动切到 ToDesk 再试");

        var (readyBmp, readyDoc, ready) = ScreenOps.WaitForReady(win, new[] { "远程控制", "设备代码", "连接" }, ctx);
        double codeCx, codeCy, tdRowY = 0;
        using (readyBmp)
        {
            var doc = readyDoc;
            if (!ready || doc == null) return ConnectResult.Fail("ToDesk 主界面未加载，已取消自动连接（请手动打开软件后重试）");
            var lines = OcrGeo.BuildLines(doc);
            var title = OcrGeo.FindLineContains(lines, "远程控制设", "控制设备");
            double titleY = title?.Y ?? win.H * 0.5;
            tdRowY = titleY + 55;
            // 设备码框在"远程控制设备"标题下方
            var num = OcrGeo.NumberField(lines, titleY + 18, titleY + 120);
            var ph = OcrGeo.FindCluster(doc, titleY + 55, 46, "设备代码", 0)
                     ?? OcrGeo.FindCluster(doc, titleY + 55, 46, "输入", win.W * 0.2);
            if (num != null) { codeCx = num.Value.Cx; codeCy = num.Value.Cy; }
            else if (ph != null) { codeCx = ph.Value.Cx; codeCy = ph.Value.Cy; }
            else { codeCx = win.W * 0.42; codeCy = titleY + 55; }

            Logger.Info($"ToDesk 定位 设备码@({codeCx:F0},{codeCy:F0})");
            CoordOps.Fill(win, codeCx, codeCy, deviceCode, ctx);
            Logger.Info($"ToDesk 填入设备码 {deviceCode}");
        }

        ConnWait.Sleep(ctx, 120);
        ctx?.ThrowIfCancellationRequested();

        SD.Bitmap bmp2 = ScreenOps.CaptureClient(win);
        try
        {
            var (btnX, btnY) = LocateConnectButton(win, bmp2, tdRowY, () =>
            {
                Logger.Warn("ToDesk 未检测到亮蓝连接按钮，疑似填码丢失，慢速重填一次");
                CoordOps.Fill(win, codeCx, codeCy, deviceCode, ctx, slow: true);
            });
            try
            {
                bmp2.Save(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RemoteConnectHelper", "td_before_connect.png"));
            }
            catch { }

            ctx?.ThrowIfCancellationRequested();
            ctx?.MarkConnectClicked();
            ScreenOps.ClickClient(win, btnX, btnY, 220);
            Logger.Info($"ToDesk 已点连接 @({btnX:F0},{btnY:F0})");
        }
        finally { bmp2.Dispose(); }

        return PostConnect(ctx, password);
    }

    private static (double X, double Y) LocateConnectButton(
        ScreenOps.Win win, SD.Bitmap bmp, double guessY, Action refill)
    {
        // 正常路径直接复用定位阶段算好的输入行 y，不再 OCR（省约 300ms）
        var btn = ScreenOps.FindBlueButton(bmp, (int)(win.W * 0.58), (int)(guessY - 40), win.W, (int)(guessY + 44));
        if (btn != null) return (btn.Value.X, btn.Value.Y);

        refill();
        using var b2 = ScreenOps.CaptureClient(win);
        Thread.Sleep(120);
        double gy = guessY;
        var d2 = ScreenOps.OcrAsync(b2).GetAwaiter().GetResult();
        if (d2 != null)
        {
            var t = OcrGeo.FindLineContains(OcrGeo.BuildLines(d2), "远程控制设", "控制设备");
            if (t != null) gy = t.Y + 55;
        }
        var btn2 = ScreenOps.FindBlueButton(b2, (int)(win.W * 0.58), (int)(gy - 40), win.W, (int)(gy + 44));
        return btn2 != null ? (btn2.Value.X, btn2.Value.Y) : (win.W * 0.82, gy);
    }

    private ConnectResult PostConnect(ConnectionContext? ctx, string password)
    {
        // 点连接后轮询全屏：错误即返回；出现“输入连接密码”弹窗时，有密码自动填并点“立即连接”、
        // 无密码点“免密连接”；主控台特征连续两次消失且无弹窗/错误→判已进入会话。可取消。
        const int step = 500;
        int elapsed = 0, budget = 6000, consoleGoneStreak = 0;
        bool pwdFilled = false, freeClicked = false;
        int? actedAt = null;   // 点下“立即连接/免密连接”的时刻，弹窗久留即判密码没通过
        while (elapsed < budget)
        {
            ctx?.ThrowIfCancellationRequested();
            using var full = ScreenOps.CaptureScreen();
            var doc = ScreenOps.OcrAsync(full).GetAwaiter().GetResult();
            if (doc != null)
            {
                var lines = OcrGeo.BuildLines(doc);
                var errLine = lines.FirstOrDefault(l => Errors.Any(e => l.Text.Contains(e)));
                if (errLine != null)
                {
                    // 顺手点掉错误框的"确定/知道了/关闭"
                    var ok = OcrGeo.FindLineContains(lines, "确定", "知道了", "关闭");
                    if (ok != null)
                    {
                        var w = ok.Words.FirstOrDefault(x => x.Text.Contains("确") || x.Text.Contains("知") || x.Text.Contains("关"));
                        if (w != null) { InputSimulator.Click((int)w.CenterX, (int)w.CenterY, 200); ConnWait.Sleep(ctx, 200); }
                    }
                    Logger.Warn("ToDesk 连接返回错误：" + errLine.Text);
                    bool isPwdErr = errLine.Text.Contains("密码") || errLine.Text.Contains("验证码")
                                    || errLine.Text.Contains("验证") || errLine.Text.Contains("解锁")
                                    || errLine.Text.Contains("凭据");
                    // 密码/验证码类错误 → 弹预填卡片让客服核对重连；设备离线等其它错误才直接失败
                    if (isPwdErr) return NeedPwdManual(password);
                    return ConnectResult.Fail(errLine.Text.Length > 24 ? "ToDesk 返回：" + errLine.Text : errLine.Text);
                }

                if (OcrGeo.ContainsAny(doc, PwdDialogHints))
                {
                    consoleGoneStreak = 0; // 弹窗在，会话尚未进入
                    var nowBtn = ClusterOnLine(doc, lines, "立即连接");
                    var freeBtn = ClusterOnLine(doc, lines, "免密连接");
                    var devLine = OcrGeo.FindLineContains(lines, "设备代码");

                    // 已提交过密码、弹窗却出现“密码错误/请重新输入”红字 → 立刻判密码不对，弹卡人工改
                    if (pwdFilled && lines.Any(l => PwdErrHints.Any(e => l.Text.Contains(e))))
                    {
                        Logger.Warn("ToDesk 密码弹窗提示密码错误/重新输入，转人工核对");
                        return NeedPwdManual(password);
                    }

                    if (!pwdFilled && !string.IsNullOrEmpty(password))
                    {
                        // 密码框中心：横向取两按钮中点（与输入框中心接近），纵向取“设备代码”行→按钮行的 0.40 分位
                        double cx;
                        if (freeBtn.HasValue && nowBtn.HasValue) cx = (freeBtn.Value.Cx + nowBtn.Value.Cx) / 2.0;
                        else if (nowBtn.HasValue) cx = nowBtn.Value.Cx - 95;
                        else if (devLine != null) cx = (devLine.X0 + devLine.X1) / 2.0 + 80;
                        else return NeedPwdManual(password);

                        double cy;
                        if (devLine != null && nowBtn.HasValue) cy = devLine.Y + (nowBtn.Value.Cy - devLine.Y) * 0.40;
                        else if (nowBtn.HasValue) cy = nowBtn.Value.Cy - 95;
                        else if (devLine != null) cy = devLine.Y + 62;
                        else return NeedPwdManual(password);

                        string nb = nowBtn.HasValue ? $"({nowBtn.Value.Cx:F0},{nowBtn.Value.Cy:F0})" : "(未定位)";
                        Logger.Info($"ToDesk 密码弹窗 填框@({cx:F0},{cy:F0}) 立即连接@{nb}");
                        InputSimulator.Click((int)cx, (int)cy, 120); // 点密码框聚焦
                        ConnWait.Sleep(ctx, 380);
                        ScreenOps.Type(password);
                        ConnWait.Sleep(ctx, 150);
                        if (nowBtn.HasValue) InputSimulator.Click((int)nowBtn.Value.Cx, (int)nowBtn.Value.Cy, 220);
                        pwdFilled = true;
                        actedAt = elapsed;
                        budget = Math.Max(budget, elapsed + 6000); // 填完再给 6s 等进会话/报错
                        Logger.Info("ToDesk 已填密码并点立即连接");
                    }
                    else if (!freeClicked && string.IsNullOrEmpty(password))
                    {
                        // 没识别到密码：对方若允许免密，点弹窗“免密连接”直连
                        if (freeBtn.HasValue)
                        {
                            InputSimulator.Click((int)freeBtn.Value.Cx, (int)freeBtn.Value.Cy, 220);
                            freeClicked = true;
                            actedAt = elapsed;
                            budget = Math.Max(budget, elapsed + 5000);
                            Logger.Info("ToDesk 未给密码，点弹窗“免密连接”");
                        }
                        else return NeedPwdManual("");
                    }
                    // 点过连接 2s 后弹窗仍在（没进会话、也没红字）= 密码没通过/需人工，直接弹预填改密卡，不干等
                    if (actedAt.HasValue && elapsed - actedAt.Value >= 2000)
                    {
                        Logger.Warn("ToDesk 点连接后密码弹窗久留，转人工核对密码");
                        return NeedPwdManual(password);
                    }
                }
                else
                {
                    bool consoleStillThere = ConsoleAnchors.Any(a => (doc.FlatText ?? "").Contains(a));
                    if (!consoleStillThere)
                    {
                        consoleGoneStreak++;
                        if (consoleGoneStreak >= 2) return ConnectResult.Ok("正在连接对方（已进入 ToDesk 会话）");
                    }
                    else consoleGoneStreak = 0;
                }
            }

            if (!ConnWait.Sleep(ctx, step)) break;
            elapsed += step;
        }

        // 预算耗尽：弹窗仍在=没进去，交人工；弹窗已没=按已发起处理
        using var last = ScreenOps.CaptureScreen();
        var lastDoc = ScreenOps.OcrAsync(last).GetAwaiter().GetResult();
        if (lastDoc != null && OcrGeo.ContainsAny(lastDoc, PwdDialogHints)) return NeedPwdManual(password);
        return ConnectResult.Ok("已发起 ToDesk 连接");
    }

    private static ConnectResult NeedPwdManual(string password)
        => string.IsNullOrEmpty(password)
            ? ConnectResult.NeedPwd("对方需要密码，请在弹出的卡片里填写后重连")
            : ConnectResult.NeedPwd("密码可能不对，已在卡片预填 " + password + "，核对后点连接重连");

    /// <summary>在包含 key 的那一行上取 key 文字簇中心（全屏物理坐标，用于点弹窗按钮）。</summary>
    private static (double Cx, double Cy)? ClusterOnLine(OcrDoc doc, List<OcrGeo.Line> lines, string key)
    {
        var line = lines.FirstOrDefault(l => l.Text.Contains(key));
        if (line == null) return null;
        var c = OcrGeo.FindCluster(doc, line.Y, 26, key);
        return c == null ? null : (c.Value.Cx, c.Value.Cy);
    }
}
