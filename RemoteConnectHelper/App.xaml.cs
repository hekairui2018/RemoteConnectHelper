using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Hotkey;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.Services;
using RemoteConnectHelper.UI;
using WinForms = System.Windows.Forms;

namespace RemoteConnectHelper;

public partial class App : System.Windows.Application
{
    private const string AppName = "远程连接助手";
    private const string MutexName = "RemoteConnectHelper_SingleInstance_Mutex";

    private Mutex? _mutex;
    private HotkeyManager? _hotkey;
    private WinForms.NotifyIcon? _tray;
    private AppConfig _config = new();
    private bool _running;
    private readonly RecognizerEngine _engine = new();

    // 第二个实例（双击桌面图标/快捷方式）通过这个命名事件通知已在运行的主实例"识别剪贴板"
    private const string RecognizeEventName = "RemoteConnectHelper_Recognize_Event";
    private EventWaitHandle? _recognizeEvent;
    private WelcomeWindow? _welcome;
    private ClipboardListenerWindow? _clipListener;
    private FloatingBallWindow? _ball;
    private WinForms.ContextMenuStrip? _menu;
    private string? _lastPreHash;
    // 截图后后台“预识别”的最近一张远程码结果（含来源图与哈希）。点悬浮球/热键时若剪贴板仍是这张图，
    // 直接复用、跳过现场 OCR，做到“点一下就秒连”；每张截图都会先在这里记住，认出码才缓存。
    private PreRecognition? _pre;

    /// <summary>一次截图的预识别结果：来源图哈希、冻结位图、识别结果（高置信直连/低置信弹气泡）。</summary>
    private sealed class PreRecognition
    {
        public string Hash { get; init; } = "";
        public BitmapSource? Shot { get; init; }
        public RecognizedResult? Result { get; init; }
    }

    private static System.Drawing.Icon? _appIcon;
    private static System.Windows.Media.ImageSource? _iconImage;
    public static System.Windows.Media.ImageSource? AppIconImage => _iconImage;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 自检：--selftest <图片路径>，跑 OCR+识别打印结果后退出（不驻留托盘）
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            RunSelfTest(e.Args[1]);
            Shutdown();
            return;
        }

        // 并发回归：--locktest <图片路径>，同图并发 N 路 OCR，验证串行锁下无重入异常、结果一致
        if (e.Args.Length >= 2 && e.Args[0] == "--locktest")
        {
            RunLockTest(e.Args[1]);
            Shutdown();
            return;
        }

        // 一键真机回归：--once <图片路径>，识别后直接自动连接（不经卡片），结果写日志/once_result.txt
        if (e.Args.Length >= 2 && e.Args[0] == "--once")
        {
            base.OnStartup(e);
            RunOnce(e.Args[1]);
            return;
        }

        // 界面真机验证：--hud <图片路径>，把图放进剪贴板后走与热键完全相同的流水线
        // （弹状态小条、可取消/重试），常驻托盘。仅用于真机验证界面，不写 once_result。
        if (e.Args.Length >= 2 && e.Args[0] == "--hud")
        {
            base.OnStartup(e);
            _config = AppConfig.Load();
            SetupTray();
            SetupHotkey();
            Logger.Info($"远程连接助手已启动（--hud）版本={BuildInfo.Version} 构建={BuildInfo.BuildTime} " +
                        $"OS={Environment.OSVersion.Version} {OcrService.DescribeEngine()}");
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(Path.GetFullPath(e.Args[1]));
                bmp.EndInit();
                bmp.Freeze();
                System.Windows.Clipboard.SetImage(bmp);
            }
            catch (Exception ex) { Logger.Error("--hud 载入图片到剪贴板失败", ex); }
            Dispatcher.BeginInvoke(new Action(RunPipelineSafe),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        // 单实例
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已在运行：双击桌面图标/快捷方式 = 通知主实例立即识别剪贴板（而不是静默退出、毫无反馈）
            SignalRunningInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _config = AppConfig.Load();

        SetupTray();
        SetupHotkey();
        StartRecognizeListener();
        SetupClipboardListener();
        SetupBall();

        Logger.Info($"远程连接助手已启动 版本={BuildInfo.Version} 构建={BuildInfo.BuildTime} " +
                    $"OS={Environment.OSVersion.Version} {OcrService.DescribeEngine()}");
        // 首次启动弹可关闭的使用说明窗（讲清"怎么把截图给它"）；之后不自动弹，托盘右键「使用说明」可随时打开。
        MaybeShowWelcome();
    }

    private void SetupTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        _menu = menu; // 悬浮球右键复用同一个菜单（点外部/Esc/选完自动关）
        MenuAutoClose.Attach(menu); // WPF 无激活主窗/不激活悬浮球下，兜底“点菜单外任意位置即关”

        var miRecognize = new WinForms.ToolStripMenuItem("立即连接");
        miRecognize.Click += (_, _) => RunPipelineSafe();
        menu.Items.Add(miRecognize);

        var miHelp = new WinForms.ToolStripMenuItem("使用说明（怎么把截图给它）");
        miHelp.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowWelcome(false)));
        menu.Items.Add(miHelp);

        var miExport = new WinForms.ToolStripMenuItem("导出当天日志");
        miExport.Click += (_, _) => Dispatcher.BeginInvoke(new Action(ExportTodayLog));
        menu.Items.Add(miExport);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var miAutoClip = new WinForms.ToolStripMenuItem("截图后自动连接（认出远程码即连）")
        {
            CheckOnClick = true,
            Checked = _config.AutoConnectOnClipboard,
        };
        miAutoClip.CheckedChanged += (_, _) =>
        {
            _config.AutoConnectOnClipboard = miAutoClip.Checked;
            _config.Save();
            Logger.Info("截图后自动连接：" + (miAutoClip.Checked ? "开" : "关"));
        };
        menu.Items.Add(miAutoClip);

        var miAutoStart = new WinForms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled(),
        };
        miAutoStart.CheckedChanged += (_, _) => SetAutoStart(miAutoStart.Checked);
        menu.Items.Add(miAutoStart);

        // 每次展开菜单时刷新勾选状态（使用说明窗里也可能改过开机自启）
        menu.Opening += (_, _) =>
        {
            miAutoStart.Checked = IsAutoStartEnabled();
            miAutoClip.Checked = _config.AutoConnectOnClipboard;
        };

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var miAbout = new WinForms.ToolStripMenuItem("关于");
        miAbout.Click += (_, _) =>
            System.Windows.MessageBox.Show(
                $"远程连接助手 V{BuildInfo.Version}\n构建时间：{BuildInfo.BuildTime}\n\n" +
                "截图客户远程码后点北斗悬浮球（或按 Ctrl+Alt+R），\n自动识别向日葵/ToDesk 并连接；\n认不准软件时点卡片上的软件按钮，密码留空即免密直连。\n\n" +
                "右键悬浮球/托盘可「导出当天日志」排查问题。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        menu.Items.Add(miAbout);

        var miExit = new WinForms.ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitApp();
        menu.Items.Add(miExit);

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "远程连接助手（Ctrl+Alt+R）",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RunPipelineSafe();
    }

    private void SetupHotkey()
    {
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += () =>
        {
            Logger.Info("热键 Ctrl+Alt+R 触发");
            Dispatcher.BeginInvoke(new Action(RunPipelineSafe));
        };
        if (!_hotkey.Register(_config.Modifiers, _config.Key))
        {
            Logger.Warn("热键注册失败（被占用）");
            _tray?.ShowBalloonTip(4000, AppName,
                "Ctrl+Alt+R 已被占用，请右键托盘图标用「立即连接」",
                WinForms.ToolTipIcon.Warning);
        }
        else
        {
            Logger.Info("全局热键已注册 Ctrl+Alt+R");
        }
    }

    /// <summary>后台线程监听第二实例（双击桌面图标）发来的"识别剪贴板"命名事件。</summary>
    private void StartRecognizeListener()
    {
        try
        {
            _recognizeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, RecognizeEventName, out _);
            var th = new Thread(() =>
            {
                try
                {
                    while (_recognizeEvent != null)
                    {
                        _recognizeEvent.WaitOne();
                        if (_recognizeEvent == null) break;
                        // 双击桌面图标 = 只把后台程序的悬浮球唤到最前，不直接识别；
                        // “识别并连接”统一由点悬浮球 / Ctrl+Alt+R / 托盘菜单触发。
                        Logger.Info("收到桌面图标双击信号，唤出悬浮球");
                        Dispatcher.BeginInvoke(new Action(ShowBall));
                    }
                }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Logger.Warn("IPC 监听结束：" + ex.Message); }
            })
            { IsBackground = true, Name = "RchIpcListener" };
            th.Start();
        }
        catch (Exception ex) { Logger.Error("启动 IPC 监听失败", ex); }
    }

    /// <summary>第二实例：通知已在运行的主实例识别一次剪贴板。</summary>
    private static void SignalRunningInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(RecognizeEventName);
            ev.Set();
            Logger.Info("已有实例在运行，已发信号唤出悬浮球");
        }
        catch (Exception ex) { Logger.Warn("通知已有实例失败：" + ex.Message); }
    }

    private static string AppDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "RemoteConnectHelper");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    /// <summary>首次启动（没有 welcome.shown 标记）自动弹一次使用说明。</summary>
    private void MaybeShowWelcome()
    {
        try
        {
            if (File.Exists(Path.Combine(AppDir, "welcome.shown"))) return;
            Dispatcher.BeginInvoke(new Action(() => ShowWelcome(true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) { Logger.Warn("首次使用说明判定失败：" + ex.Message); }
    }

    /// <summary>打开使用说明窗；关闭时应用开机自启选择，并按勾选写"不再提示"标记。</summary>
    private void ShowWelcome(bool firstTime)
    {
        if (_welcome != null)
        {
            try { _welcome.Activate(); } catch { }
            return;
        }
        var w = new WelcomeWindow(IsAutoStartEnabled(), firstTime);
        _welcome = w;
        w.Closed += (_, _) =>
        {
            _welcome = null;
            try
            {
                SetAutoStart(w.WantAutoStart);
                if (w.DontShowAgain)
                    File.WriteAllText(Path.Combine(AppDir, "welcome.shown"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Logger.Info($"使用说明窗关闭：开机自启={w.WantAutoStart}，不再提示={w.DontShowAgain}");
            }
            catch (Exception ex) { Logger.Warn("保存使用说明选项失败：" + ex.Message); }
        };
        w.Show();
    }

    /// <summary>右键菜单“导出当天日志”：复制当天日志到桌面；桌面不可写改弹“另存为”；当天无日志则提示。</summary>
    private void ExportTodayLog()
    {
        try
        {
            if (!Logger.TodayLogExists)
            {
                System.Windows.MessageBox.Show("今天还没有产生日志。", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? dst = null;
            try { dst = Logger.ExportTodayToDesktop(); }
            catch (Exception ex) { Logger.Warn("导出日志到桌面失败，改弹另存为：" + ex.Message); }

            if (string.IsNullOrEmpty(dst))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"远程连接助手日志_{DateTime.Now:yyyyMMdd}.log",
                    Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    File.Copy(Logger.TodayLogPath, dlg.FileName, true);
                    dst = dlg.FileName;
                }
            }

            if (!string.IsNullOrEmpty(dst))
            {
                Logger.Info($"导出当天日志到：{dst}");
                var r = System.Windows.MessageBox.Show(
                    "当天日志已导出到：\n" + dst + "\n\n点“是”打开所在文件夹。",
                    AppName, MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r == MessageBoxResult.Yes)
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dst}\""); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("导出当天日志失败", ex);
            System.Windows.MessageBox.Show("导出日志失败：" + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>挂一个隐藏窗监听剪贴板：截图停稳后每张都预识别，认出远程码先记住（默认等点球/热键再连，
    /// 开启“截图后自动连接”时认出即连）。</summary>
    private void SetupClipboardListener()
    {
        _clipListener = new ClipboardListenerWindow();
        _clipListener.ClipboardSettled += OnClipboardSettled;
        _clipListener.Start();
    }

    /// <summary>创建并显示北斗悬浮球：单击识别连接、拖动换位并记忆、右键出菜单。</summary>
    private void SetupBall()
    {
        _ball = new FloatingBallWindow();
        _ball.ApplyPosition(_config.BallLeft, _config.BallTop);
        _ball.OnRecognize = () =>
        {
            Logger.Info("悬浮球点击，触发识别");
            Dispatcher.BeginInvoke(new Action(RunPipelineSafe));
        };
        _ball.OnRightClick = ShowBallMenu;
        _ball.PositionChanged += (l, t) =>
        {
            _config.BallLeft = l;
            _config.BallTop = t;
            _config.Save();
        };
        _ball.Show();
        Logger.Info("北斗悬浮球已显示");
    }

    /// <summary>悬浮球右键：弹与托盘共用的 WinForms 菜单。WPF 自带 ContextMenu 在不激活悬浮窗
    /// (WS_EX_NOACTIVATE) 上收不到“点外部关闭”会卡住；ToolStripDropDown 自带消息循环，点外部/Esc/选完即关。</summary>
    private void ShowBallMenu()
    {
        if (_menu == null) return;
        try { _menu.Show(WinForms.Cursor.Position); }
        catch (Exception ex) { Logger.Warn("弹出悬浮球右键菜单失败：" + ex.Message); }
    }

    /// <summary>把悬浮球显示并提到最前（已在运行时双击桌面图标 = 唤出球，不识别）。</summary>
    private void ShowBall()
    {
        _ball?.ShowBall();
    }

    private async void OnClipboardSettled()
    {
        try
        {
            if (_running) return;          // 正在识别/连接，不打断（点球时会现场 OCR 兜底）
            if (_welcome != null) return;  // 使用说明窗开着时先不预识别

            BitmapSource? shot = null;
            await Dispatcher.InvokeAsync(() => shot = ClipboardImage.TryGet());
            if (shot == null) return;      // 剪贴板是文字/空，不理

            string hash = QuickHash(shot);
            if (hash == _lastPreHash) return; // 同一张截图不重复预识别
            _lastPreHash = hash;

            // 每张截图都完整识别一遍（界面码 / 聊天远程码 / 默认向日葵都覆盖），认出码就先记住，等点击再连。
            var (doc, r) = await RecognizeWithOrientationAsync(shot);
            if (doc == null || r == null) return;
            string code = (r.DeviceCode ?? "").Trim().Replace(" ", "");
            if (code.Length < 9)
            {
                Logger.Info("剪贴板新图未认出远程码，不缓存");
                return;
            }

            // 记住最近一张远程码（品牌没把握也缓存，点击时按品牌置信分流：铁证直连、拿不准弹气泡人工核对软件）
            _pre = new PreRecognition { Hash = hash, Shot = shot, Result = r };
            bool canAuto = r.Kind != RemoteAppKind.Unknown && string.IsNullOrEmpty(r.Error) && r.BrandConfident;
            Logger.Info($"剪贴板截图已预识别并记住：{r.KindName} {code} " +
                        $"密码={(r.HasPassword ? r.Password : "<免密>")} " +
                        $"原图={doc.ImgWidth:F0}x{doc.ImgHeight:F0} 放大={doc.ScaleApplied:0.##} " +
                        $"依据=[{r.BrandReason}] 分流={(canAuto ? "直连" : "弹卡选软件")}，等待点悬浮球/热键");

            // 仅当用户显式开启“截图后自动连接”时才零按键直连；默认只记住、不自动连。
            if (_config.AutoConnectOnClipboard && canAuto)
            {
                Logger.Info("已开启截图后自动连接，直接触发连接");
                RunPipelineSafe();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("剪贴板预识别异常：" + ex.Message);
        }
    }

    /// <summary>OCR + 方向兜底：先识别正向，只要认出设备码立即返回（日常正向截图零额外开销）；
    /// 正向没认出码时，依次把图旋转 180/90/270 再识别，首个认出设备码的方向整份采用（品牌/密码同方向），
    /// 三个方向都没有码则返回正向结果走原分流（弹手填卡）。手机拍屏转了 90/180/270 度也能认。</summary>
    private async Task<(OcrDoc? doc, RecognizedResult? result)> RecognizeWithOrientationAsync(BitmapSource shot)
    {
        var doc = await Task.Run(() => OcrService.RecognizeAsync(shot)).ConfigureAwait(false);
        if (doc == null) return (null, null);
        var result = _engine.Run(doc);
        bool upright = OcrOrientation.HasUprightSignal(doc);
        // 快路径：认出码且画面文字正立可读（命中品牌/界面词）→ 直接采用。正向截图只做这一次 OCR，零额外开销。
        if (!string.IsNullOrWhiteSpace(result.DeviceCode) && upright) return (doc, result);

        // 正向无码，或有码但文字不像正立（倒置/侧置时数字会被硬凑成形似的 9 位误码，如 353717031→130111353）：
        // 逐方向找“认出码 + 文字正立”，首个满足即采用。
        foreach (var ang in new[] { 180, 90, 270 })
        {
            BitmapSource rotated;
            try { rotated = OcrService.RotateForOcr(shot, ang); }
            catch (Exception ex) { Logger.Warn($"旋转 {ang}° 位图失败：" + ex.Message); continue; }

            var d2 = await Task.Run(() => OcrService.RecognizeAsync(rotated)).ConfigureAwait(false);
            if (d2 == null) continue;
            var r2 = _engine.Run(d2);
            if (!string.IsNullOrWhiteSpace(r2.DeviceCode) && OcrOrientation.HasUprightSignal(d2))
            {
                Logger.Info($"OCR 正向未获“正立设备码”（正向界面词={upright}），旋转 {ang}° 后命中：{r2.KindName} 码={r2.DeviceCode}");
                return (d2, r2);
            }
        }
        // 各方向都没有“码+正立词”：多为纯数字/纯字母聊天截图（本就无中文锚词），尊重原图正向结果；
        // 纯数字正立图旋转也不会凭空出现中文词，故这里回退不会把正立改错方向。
        if (!string.IsNullOrWhiteSpace(result.DeviceCode) && !upright)
            Logger.Info("未检测到正立界面词，按原图方向采用结果（纯数字聊天或文字过少）；若码异常请在卡片核对");
        return (doc, result);
    }

    /// <summary>16x16 灰度均值哈希，用于"同一张截图不重复预识别"以及点击时核对剪贴板是否仍是那张图。</summary>
    private static string QuickHash(BitmapSource bmp)
    {
        try
        {
            if (!bmp.IsFrozen && bmp.CanFreeze) bmp.Freeze();
            var scaled = new TransformedBitmap(bmp,
                new System.Windows.Media.ScaleTransform(16.0 / bmp.PixelWidth, 16.0 / bmp.PixelHeight));
            var gray = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Gray8, null, 0);
            var px = new byte[256];
            gray.CopyPixels(px, 16, 0);
            long sum = 0;
            foreach (var p in px) sum += p;
            byte avg = (byte)(sum / px.Length);
            var sb = new System.Text.StringBuilder(256);
            foreach (var p in px) sb.Append(p >= avg ? '1' : '0');
            return sb.ToString();
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>从嵌入资源加载多尺寸 app.ico 给托盘用；顺带生成冻结的 BitmapSource 供 WPF 窗（使用说明）标题栏/任务栏。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        if (_appIcon != null) return _appIcon;
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var st = asm.GetManifestResourceStream("RemoteConnectHelper.Assets.app.ico");
            if (st != null)
            {
                _appIcon = new System.Drawing.Icon(st);
                try
                {
                    _iconImage = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        _appIcon.Handle, Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    _iconImage.Freeze();
                }
                catch { }
                return _appIcon;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>收掉托盘气泡（如开机启动提示）：切换图标可见性会让系统立即关闭当前气泡，
    /// 图标本身与右键菜单绑定在同一 NotifyIcon 对象上，不会丢失。</summary>
    private void DismissBalloon()
    {
        try
        {
            if (_tray is { Visible: true })
            {
                _tray.Visible = false;
                _tray.Visible = true;
            }
        }
        catch { /* 气泡不影响主流程，忽略 */ }
    }

    private async void RunPipelineSafe()
    {
        if (_running) return;
        _running = true;
        try
        {
            await RunPipelineAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("流水线异常", ex);
            _tray?.ShowBalloonTip(3000, AppName, "出错了：" + ex.Message, WinForms.ToolTipIcon.Error);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task RunPipelineAsync()
    {
        // 1. 先读当前剪贴板图（Win32 毫秒快取，不长期持锁）
        BitmapSource? cur = null;
        await Dispatcher.InvokeAsync(() => cur = ClipboardImage.TryGet());

        RecognizedResult result;
        BitmapSource shot;
        var cache = _pre; // 抓快照，避免预识别回调在本方法执行期间替换缓存造成空引用

        if (cur != null && cache is { Result: not null, Shot: not null } && QuickHash(cur) == cache.Hash)
        {
            // 2a. 剪贴板仍是刚截、已在后台预识别好的那张图：直接复用结果，跳过现场 OCR，点一下即连
            shot = cache.Shot;
            result = cache.Result;
            Logger.Info("命中截图预识别缓存，跳过 OCR 直接连接");
        }
        else if (cur == null && cache is { Result: not null, Shot: not null })
        {
            // 2b. 截图后剪贴板又被文字等覆盖：复用最近一次截图预识别到的远程码
            shot = cache.Shot;
            result = cache.Result;
            Logger.Info("剪贴板当前不是图片，复用最近一次截图的预识别结果");
        }
        else
        {
            // 2c. 缓存未命中（换了新图、或没预识别过）：现场取图 OCR，绝不用上一张旧码连新图
            shot = cur!;
            if (shot == null)
            {
                _tray?.ShowBalloonTip(2500, AppName,
                    "剪贴板里没有截图，请先按 Win+Shift+S 框选远程码",
                    WinForms.ToolTipIcon.Warning);
                return;
            }
            Logger.Info("未命中预识别缓存，现场 OCR");
            var (doc, ori) = await RecognizeWithOrientationAsync(shot);
            if (doc == null || ori == null)
            {
                _tray?.ShowBalloonTip(2500, AppName, "OCR 识别失败", WinForms.ToolTipIcon.Warning);
                return;
            }
            result = ori;
            Logger.Info($"OCR 文本：{doc.FullText.Replace("\r", " ").Replace("\n", " ").Trim()}");
            Logger.Info($"现场OCR 原图={doc.ImgWidth:F0}x{doc.ImgHeight:F0} 放大倍数={doc.ScaleApplied:0.##}");

            // 现场 OCR 认出码也回写预识别缓存：远程软件启动可能冲掉剪贴板图，回写后第二次点
            // （即使剪贴板已无图）也能走“复用最近一张”，不再误报“剪贴板里没有截图”。
            string liveCode = (result.DeviceCode ?? "").Trim().Replace(" ", "");
            if (liveCode.Length >= 9)
            {
                string liveHash = QuickHash(shot);
                _pre = new PreRecognition { Hash = liveHash, Shot = shot, Result = result };
                _lastPreHash = liveHash;
                Logger.Info("现场OCR结果已回写缓存，后续点球可复用同一张");
            }
        }

        Logger.Info($"识别结果：{result.KindName} 码={result.DeviceCode} " +
                    $"密码={(result.HasPassword ? result.Password : "<免密>")} " +
                    $"候选=[{string.Join(",", result.CandidateCodes)}] 依据=[{result.BrandReason}] 错误={result.Error ?? "无"}");

        // 识别成功、即将弹状态小条/手动卡片：先收掉托盘启动气泡，避免两个提示并排让客服困惑
        DismissBalloon();

        // 4. 速度优先：品牌有铁证（品牌名/识别码·验证码 vs 设备代码·安全密码）立即连接 + 非阻塞状态小条；
        //    品牌没把握（只剩两家共有中性词、靠码长默认）时，码虽认到也弹气泡让客服瞄一眼软件，防连错。
        string autoCode = (result.DeviceCode ?? "").Trim().Replace(" ", "");
        bool canAuto = result.Kind != RemoteAppKind.Unknown
                       && string.IsNullOrEmpty(result.Error)
                       && result.BrandConfident
                       && autoCode.Length >= 9;
        if (!canAuto)
        {
            string why = !string.IsNullOrEmpty(result.Error) ? "未认出设备码→手填卡"
                : !result.BrandConfident ? "软件类型无铁证→软件选择卡（不替用户赌）"
                : "设备码不足9位→手填卡";
            Logger.Info($"分流→确认卡（{why}）");
            var manual = new ConfirmCardWindow(result, shot, MakeCardConfirm(result, shot), _ball?.BoundsRect);
            manual.Show();
            await manual.ClosedTask;
            return;
        }
        Logger.Info("分流→自动直连（品牌铁证 + 设备码≥9位）");
        await RunConnectWithHudAsync(result, shot);
    }

    /// <summary>带状态小条 + 取消上下文（Esc/取消按钮/35s 看门狗/点连接后断开兜底）的一次连接；
    /// 品牌铁证自动直连与确认卡点软件按钮后统一走这里，保证两条路径都能随时停止。</summary>
    private async Task RunConnectWithHudAsync(RecognizedResult r, BitmapSource? shot)
    {
        var hud = new StatusHudWindow();
        ConnectionContext? ctx = null;
        hud.OnCancel = () =>
        {
            Logger.Info("用户在状态小条点取消/Esc");
            ctx?.CancelByUser();
        };
        hud.OnRetry = () => { Logger.Info("用户在状态小条点重试"); _ = RunAutoAsync(hud, r); };
        hud.OnManual = () =>
        {
            Logger.Info("用户选手动改/输密码");
            Dispatcher.Invoke(() => PopManualCard(hud, r, shot, "在卡片上核对设备码/密码后点软件按钮连接"));
        };
        hud.Show();
        await RunAutoAsync(hud, r);

        // 一次自动连接（重试也走这里，每次换新的取消上下文）
        async Task RunAutoAsync(StatusHudWindow h, RecognizedResult r)
        {
            var c = new ConnectionContext(r.Kind);
            ctx = c;
            h.Bind(c);
            h.SetConnecting(r.KindName, r.DeviceCode, r.HasPassword ? r.Password : null);
            try
            {
                IRemoteConnector connector = r.Kind == RemoteAppKind.Sunlogin
                    ? new SunloginConnector()
                    : new ToDeskConnector();
                var t = connector.ConnectAsync(r.DeviceCode, r.HasPassword ? r.Password : "", c);
                _ = t.ContinueWith(x =>
                    {
                        if (x.IsFaulted && x.Exception?.InnerException is OperationCanceledException)
                            Logger.Info("连接已被用户取消");
                        else if (x.IsFaulted)
                            Logger.Error("后台连接任务异常", x.Exception?.InnerException);
                    },
                    TaskScheduler.Default);

                ConnectResult res;
                if (await Task.WhenAny(t, Task.Delay(35000)) == t)
                {
                    try { res = await t; }
                    catch (OperationCanceledException)
                    {
                        res = c.ConnectClicked
                            ? ConnectResult.Cancel("已尝试断开，请确认远程会话已关闭")
                            : ConnectResult.Cancel("已取消，未发起连接");
                    }
                }
                else
                {
                    c.Cancel();
                    res = ConnectResult.Fail("连接超时（远程软件无响应），已取消自动连接，请手动连接");
                }

                // 终态归一放到 UI 线程执行，与"取消按钮点击（BtnCancel_Click 也在 UI 线程）"串行，
                // 消除点击事件尚在 Dispatcher 队列、后台线程已抢先按 Fail 定终态的竞态。
                ConnectResult final = res;
                bool disconnecting = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (c.UserCancelled &&
                        final.Status != ConnectStatus.Launched && final.Status != ConnectStatus.NeedPassword)
                        final = c.ConnectClicked
                            ? ConnectResult.Cancel("已尝试断开，请确认远程会话已关闭")
                            : ConnectResult.Cancel("已取消，未发起连接");
                    disconnecting = c.ConnectClicked && final.Status == ConnectStatus.Cancelled;
                });

                if (disconnecting)
                {
                    // 已点下连接才取消 = 断开兜底：在 STA 线程关会话窗/发 Esc
                    try { await StaRunner.Run(() => { ScreenOps.CloseSession(c); return true; }); }
                    catch (Exception ex) { Logger.Error("断开会话失败", ex); }
                }

                if (final.Status == ConnectStatus.Launched)
                {
                    // 成功由状态小条绿色态表达并在 1.5s 后自动关，不再弹气泡，避免两个提示并排
                    h.SetLaunched(final.Message, r.DeviceCode);
                }
                else if (final.Status == ConnectStatus.Cancelled)
                {
                    h.SetCancelled(final.Message, r.DeviceCode, disconnecting);
                }
                else if (final.Status == ConnectStatus.NeedPassword || IsPwdIssue(final.Message))
                {
                    // 需要密码 / 密码没通过：直接自动弹出预填卡片（码+密码已填好，光标在密码框），
                    // 客服核对改完点连接即可，不必再去状态小条点“手动改/输密码”
                    Dispatcher.Invoke(() => PopManualCard(h, r, shot, final.Message));
                }
                else
                {
                    h.SetFailed(final.Message, r.DeviceCode);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("自动连接异常", ex);
                // 与取消点击同线程读取，避免竞态把用户取消误判成失败
                bool uc = false, clicked = false;
                Dispatcher.Invoke(() => { uc = c.UserCancelled; clicked = c.ConnectClicked; });
                if (uc)
                    h.SetCancelled(clicked ? "已尝试断开，请确认远程会话已关闭" : "已取消，未发起连接",
                        r.DeviceCode, clicked);
                else
                    h.SetFailed("出错了：" + ex.Message, r.DeviceCode);
            }
        }
    }

    /// <summary>密码没通过/需要密码时，自动在悬浮球旁弹出预填确认卡（码+密码可改），并收起状态小条。</summary>
    private void PopManualCard(StatusHudWindow h, RecognizedResult r, BitmapSource? shot, string status)
    {
        try
        {
            var card = new ConfirmCardWindow(r, shot, MakeCardConfirm(r, shot), _ball?.BoundsRect, status);
            card.Show();
            Logger.Info("需要密码/密码未通过，自动弹出预填确认卡供核对");
        }
        catch (Exception ex)
        {
            Logger.Error("自动弹确认卡失败，回退到状态小条", ex);
            h.SetNeedPassword(status, r.DeviceCode);
            return;
        }
        h.Close();
    }

    /// <summary>失败消息是否属于密码/验证码类（这类要弹预填卡，离线/超时等不弹）。</summary>
    private static bool IsPwdIssue(string? msg)
        => !string.IsNullOrEmpty(msg) &&
           (msg.Contains("密码") || msg.Contains("验证码") || msg.Contains("验证")
            || msg.Contains("解锁") || msg.Contains("凭据"));

    /// <summary>确认卡点软件按钮后的回调：用人工选定的软件/设备码/密码组一个结果，关掉卡片后走可取消的状态小条连接。
    /// 必须在 UI 线程调用（内部要 new/Show 状态小条）；连接在独立任务里继续，失败/要密码由状态小条重弹卡片。</summary>
    private Func<RemoteAppKind, string, string, Task> MakeCardConfirm(RecognizedResult baseResult, BitmapSource? shot)
    {
        return (kind, code, pwd) =>
        {
            var r = new RecognizedResult
            {
                Kind = kind,
                DeviceCode = code,
                Password = pwd,
                BrandConfident = true,
                BrandReason = "确认卡人工选择",
                CandidateCodes = baseResult.CandidateCodes,
                CandidatePasswords = baseResult.CandidatePasswords
            };
            Logger.Info($"确认卡人工选定→启动可取消连接：{r.KindName} 码={code} 密码={(string.IsNullOrEmpty(pwd) ? "<免密>" : pwd)}");
            RunConnectWithHudAsync(r, shot).ContinueWith(
                t => Logger.Error("卡片确认后连接任务异常", t.Exception?.InnerException),
                TaskContinuationOptions.OnlyOnFaulted);
            return Task.CompletedTask;
        };
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
                key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue(AppName) != null)
                key.DeleteValue(AppName, false);
        }
        catch (Exception ex) { Logger.Error("设置开机自启失败", ex); }
    }

    private async void RunOnce(string imgPath)
    {
        try
        {
            Logger.Info($"[once] 开始 {imgPath}");
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(imgPath));
            bmp.EndInit();
            bmp.Freeze();

            var doc = await Task.Run(() => OcrService.RecognizeAsync(bmp));
            if (doc == null) { Logger.Warn("[once] OCR 返回 null"); Shutdown(); return; }
            var r = _engine.Run(doc);
            Logger.Info($"[once] 识别 {r.KindName} 码={r.DeviceCode} " +
                        $"密码={(r.HasPassword ? r.Password : "<免密>")} 错误={r.Error ?? "无"}");
            if (r.Kind == RemoteAppKind.Unknown || string.IsNullOrEmpty(r.DeviceCode))
            {
                Logger.Warn("[once] 未识别到可连接目标");
                WriteOnceResult("Failed", r.Error ?? "未识别到可连接目标", r.KindName, r.DeviceCode, "");
                Shutdown();
                return;
            }

            IRemoteConnector conn = r.Kind == RemoteAppKind.Sunlogin
                ? new SunloginConnector()
                : new ToDeskConnector();
            var onceCtx = new ConnectionContext(r.Kind);
            // 总超时看门狗：远程软件白屏/无响应时，前置或输入的 native 调用可能挂住后台线程，
            // 绝不让 --once 永久卡死；超时即取消并判失败退出（worker 为后台线程，进程可正常结束）。
            var connectTask = conn.ConnectAsync(r.DeviceCode, r.Password, onceCtx);
            ConnectResult res;
            if (await Task.WhenAny(connectTask, Task.Delay(35000)) == connectTask)
                res = await connectTask;
            else
            {
                Logger.Error("[once] 连接总超时（远程软件可能无响应/白屏），取消并判失败退出");
                onceCtx.Cancel();
                res = ConnectResult.Fail("连接超时（远程软件无响应），已取消自动连接，请手动连接");
            }
            Logger.Info($"[once] 结果 {res.Status}：{res.Message}");
            WriteOnceResult(res.Status.ToString(), res.Message, r.KindName, r.DeviceCode,
                r.HasPassword ? r.Password : "");
        }
        catch (Exception ex)
        {
            Logger.Error("[once] 异常", ex);
            WriteOnceResult("Failed", ex.Message, "", "", "");
        }
        finally
        {
            await Task.Delay(400);
            Shutdown();
        }
    }

    /// <summary>把 --once 的结局落盘（成功/失败/异常都写），便于外部回归脚本判定；任何 IO 错误吞掉。</summary>
    private static void WriteOnceResult(string status, string? message, string kind, string code, string pwd)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "RemoteConnectHelper");
            Directory.CreateDirectory(dir);
            string cleanMsg = (message ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            File.WriteAllText(Path.Combine(dir, "once_result.txt"),
                $"{status}\t{cleanMsg}\r\n{kind}\t{code}\t{pwd}");
        }
        catch { }
    }

    private void RunSelfTest(string imgPath)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (!File.Exists(imgPath))
            {
                Console.WriteLine($"[selftest] 图片不存在：{imgPath}");
                return;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(imgPath));
            bmp.EndInit();
            bmp.Freeze();

            // 整包包进线程池任务再阻塞：selftest 跑在 WPF UI 线程，若直接 .GetAwaiter().GetResult()，
            // 内部 await 会捕获 UI 同步上下文、延续要回到被阻塞的 UI 线程 → 死锁。Task.Run 内无同步上下文。
            var (doc, r) = Task.Run(async () => await RecognizeWithOrientationAsync(bmp).ConfigureAwait(false))
                              .GetAwaiter().GetResult();
            if (doc == null || r == null)
            {
                Console.WriteLine("[selftest] OCR 返回 null");
                return;
            }

            Console.WriteLine($"[selftest] 图片 {Path.GetFileName(imgPath)} ({doc.ImgWidth}x{doc.ImgHeight})");
            Console.WriteLine("---- OCR 词（带坐标）----");
            foreach (var w in doc.Words)
                Console.WriteLine($"  ({w.X,6:F0},{w.Y,4:F0}) {w.Text}");

            Console.WriteLine("---- 识别结果 ----");
            Console.WriteLine($"  软件   : {r.KindName}");
            Console.WriteLine($"  品牌把握: {(r.BrandConfident ? "铁证(直连)" : "待核对(弹气泡)")}");
            Console.WriteLine($"  设备码 : {r.DeviceCode}");
            Console.WriteLine($"  密码   : {(r.HasPassword ? r.Password : "<免密>")}");
            Console.WriteLine($"  候选码 : [{string.Join(", ", r.CandidateCodes)}]");
            Console.WriteLine($"  候选密码: [{string.Join(", ", r.CandidatePasswords)}]");
            Console.WriteLine($"  错误   : {r.Error ?? "无"}");

            // 未认出设备码时，把四个方向各自的 OCR 结果打出来，定位是方向闸门还是画质问题
            if (string.IsNullOrWhiteSpace(r.DeviceCode))
            {
                Console.WriteLine("[selftest] 未认出设备码，四方向诊断：");
                foreach (var (name, ang) in new[] { ("0°", 0), ("180°", 180), ("90°", 90), ("270°", 270) })
                {
                    var b = ang == 0 ? bmp : OcrService.RotateForOcr(bmp, ang);
                    var dd = Task.Run(() => OcrService.RecognizeAsync(b)).GetAwaiter().GetResult();
                    if (dd == null) { Console.WriteLine($"  {name}: OCR null"); continue; }
                    var rr = _engine.Run(dd);
                    var rawHits = CodeExtractor.FindDeviceCodes(dd, 9, 10);
                    Console.WriteLine($"  {name}: 词数={dd.Words.Count} 正立词={OcrOrientation.HasUprightSignal(dd)} " +
                                      $"码={rr.DeviceCode} 软件={rr.KindName} 密码={(rr.HasPassword ? rr.Password : "<免密>")}");
                    Console.WriteLine($"      原始码命中: {(rawHits.Count == 0 ? "<无>" : string.Join(" | ", rawHits.Select(h => $"{h.Code}@({h.X:F0},{h.Y:F0},w{h.W:F0},h{h.H:F0})")))}");
                    foreach (var w in dd.Words.Where(x => x.Text.Any(char.IsDigit)))
                        Console.WriteLine($"      ({w.X:F0},{w.Y:F0}) {w.Text} [w{w.W:F0} h{w.H:F0}]");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[selftest] 异常：" + ex);
        }
    }

    /// <summary>并发 OCR 回归：同一张图并发 N 路识别。串行锁生效时应全部成功、结果一致；
    /// 无锁时 Windows OCR 会抛 “Another RecognizeAsync operation is already running”，被 catch 成 null。</summary>
    private void RunLockTest(string imgPath)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (!File.Exists(imgPath))
            {
                Console.WriteLine($"[locktest] 图片不存在：{imgPath}");
                return;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(imgPath));
            bmp.EndInit();
            bmp.Freeze();

            const int N = 8;
            var tasks = Enumerable.Range(0, N)
                .Select(_ => Task.Run(async () => await OcrService.RecognizeAsync(bmp)))
                .ToArray();
            var docs = Task.WhenAll(tasks).GetAwaiter().GetResult();

            int nulls = docs.Count(d => d == null);
            int distinct = docs.Where(d => d != null).Select(d => d!.FlatText).Distinct().Count();
            Console.WriteLine($"[locktest] N={N} nulls={nulls} distinctResults={distinct}");
            var first = docs.FirstOrDefault(d => d != null);
            if (first != null)
            {
                var r = _engine.Run(first);
                Console.WriteLine($"[locktest] -> {r.KindName} 码={r.DeviceCode} " +
                                  $"密码={(r.HasPassword ? r.Password : "<免密>")} 错误={r.Error ?? "无"}");
            }
            Console.WriteLine(nulls == 0 && distinct == 1 ? "LOCKTEST OK" : "LOCKTEST FAIL");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[locktest] 异常：" + ex);
        }
    }

    private void ExitApp()
    {
        try { _ball?.Close(); } catch { }
        try { _clipListener?.Close(); } catch { }
        try { _recognizeEvent?.Dispose(); } catch { }
        _recognizeEvent = null;
        try { _hotkey?.Dispose(); } catch { }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _appIcon?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }
}
