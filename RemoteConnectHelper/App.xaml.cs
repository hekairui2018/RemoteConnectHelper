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
    // 当前打开的确认卡（手填/软件选择/自动直连共用）。卡开着时点悬浮球 = 关旧卡、重新识别最新截图。
    private ConfirmCardWindow? _currentCard;
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

        // 存图链路自测：--shotsnap <图片路径>，对五类“不成功”各存一张 JPG 并打包当天 zip，打印路径后退出
        if (e.Args.Length >= 2 && e.Args[0] == "--shotsnap")
        {
            RunSnapshotTest(e.Args[1]);
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
        // （弹确认气泡卡、可停止/重试），常驻托盘。仅用于真机验证界面，不写 once_result。
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

        var miOpenLogDir = new WinForms.ToolStripMenuItem("打开日志文件夹");
        miOpenLogDir.Click += (_, _) => Dispatcher.BeginInvoke(new Action(OpenLogFolder));
        menu.Items.Add(miOpenLogDir);

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
                "右键悬浮球/托盘可「导出当天日志」（打包发回复盘）\n或「打开日志文件夹」（现场直接看 logs 日志、shots 截图）。\n\n" +
                "日志/截图目录：" + Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteConnectHelper"),
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

    /// <summary>右键菜单“打开日志文件夹”：在资源管理器打开 %AppData%\RemoteConnectHelper，
    /// 里面 logs\ 为按天日志、shots\ 为不成功时留存的截图、config.json 为设置。现场排查不用再翻隐藏目录。</summary>
    private void OpenLogFolder()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteConnectHelper");
            Directory.CreateDirectory(root);
            Logger.Info("打开日志/截图文件夹：" + root);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + root + "\"");
        }
        catch (Exception ex)
        {
            Logger.Error("打开日志文件夹失败", ex);
            System.Windows.MessageBox.Show("打开日志文件夹失败：" + ex.Message, AppName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>右键菜单“导出当天日志”：始终只在桌面产出一个 zip（内含当天 .log；当天有异常截图再带 shots/）；
    /// 桌面不可写改弹“另存为”；当天无日志则提示。不再额外往桌面放裸 .log，保证桌面只有一个文件。</summary>
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

            string dateStr = DateTime.Now.ToString("yyyyMMdd");
            int shotCount = SnapshotStore.TodayFiles().Count;

            string? zip = null;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                zip = SnapshotStore.ExportZip(Logger.TodayLogPath, dateStr,
                    Path.Combine(desktop, $"远程连接助手日志_{dateStr}.zip"));
            }
            catch (Exception ex) { Logger.Warn("导出日志zip到桌面失败，改弹另存为：" + ex.Message); }

            if (string.IsNullOrEmpty(zip))
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = $"远程连接助手日志_{dateStr}.zip",
                    Filter = "压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    try { zip = SnapshotStore.ExportZip(Logger.TodayLogPath, dateStr, dlg.FileName); }
                    catch (Exception ex) { Logger.Warn("另存日志zip失败：" + ex.Message); }
                }
            }

            if (!string.IsNullOrEmpty(zip))
            {
                Logger.Info($"导出当天日志包到：{zip}（异常截图 {shotCount} 张）");
                string tip = "当天日志已导出到：\n" + zip +
                    (shotCount > 0
                        ? $"\n\n压缩包内含当天日志和 {shotCount} 张异常截图（shots 文件夹）。"
                        : "\n\n压缩包内含当天日志。") +
                    "\n\n点“是”打开所在文件夹。";
                var r = System.Windows.MessageBox.Show(tip, AppName, MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r == MessageBoxResult.Yes)
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zip}\""); } catch { }
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
            Dispatcher.BeginInvoke(new Action(TriggerRecognizeOrReplaceCard));
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
            bool canAuto = CanAutoConnect(r);
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
        if (string.IsNullOrWhiteSpace(result.DeviceCode) && upright)
        {
            var enhanced = await RecognizeEnhancedAsync(shot).ConfigureAwait(false);
            if (enhanced.result != null) return enhanced;
        }

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
                r2.ImageRotation = ang;
                return (d2, r2);
            }
            if (string.IsNullOrWhiteSpace(r2.DeviceCode) && OcrOrientation.HasUprightSignal(d2))
            {
                var enhanced = await RecognizeEnhancedAsync(rotated).ConfigureAwait(false);
                if (enhanced.result != null)
                {
                    enhanced.result.ImageRotation = ang;
                    return enhanced;
                }
            }
        }
        // 各方向都没有“码+正立词”：多为纯数字/纯字母聊天截图（本就无中文锚词），尊重原图正向结果；
        // 纯数字正立图旋转也不会凭空出现中文词，故这里回退不会把正立改错方向。
        if (!string.IsNullOrWhiteSpace(result.DeviceCode) && !upright)
            Logger.Info("未检测到正立界面词，按原图方向采用结果（纯数字聊天或文字过少）；若码异常请在卡片核对");
        return (doc, result);
    }

    private async Task<(OcrDoc? doc, RecognizedResult? result)> RecognizeEnhancedAsync(BitmapSource source)
    {
        try
        {
            var image = OcrService.EnhanceForOcr(source);
            var doc = await Task.Run(() => OcrService.RecognizeAsync(image)).ConfigureAwait(false);
            if (doc == null) return (null, null);
            var result = _engine.Run(doc);
            if (string.IsNullOrWhiteSpace(result.DeviceCode)) return (null, null);
            result.ImageEnhanced = true;
            Logger.Info($"高对比副本识别到设备码 {result.DeviceCode}，按软件置信及设备码有效性决定直连");
            return (doc, result);
        }
        catch (Exception ex)
        {
            Logger.Warn("高对比识别兜底失败：" + ex.Message);
            return (null, null);
        }
    }

    /// <summary>按尺寸和完整像素计算 SHA256。只有同一张图才能复用识别结果，不能用缩略图相似度判定设备码。</summary>
    private static string QuickHash(BitmapSource bmp)
    {
        try
        {
            if (!bmp.IsFrozen && bmp.CanFreeze) bmp.Freeze();
            var pixels = bmp.Format == System.Windows.Media.PixelFormats.Bgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            hash.AppendData(BitConverter.GetBytes(pixels.PixelWidth));
            hash.AppendData(BitConverter.GetBytes(pixels.PixelHeight));
            int stride = checked(pixels.PixelWidth * 4);
            const int rowsPerBlock = 64;
            var buffer = new byte[checked(stride * Math.Min(rowsPerBlock, pixels.PixelHeight))];
            for (int y = 0; y < pixels.PixelHeight; y += rowsPerBlock)
            {
                int rows = Math.Min(rowsPerBlock, pixels.PixelHeight - y);
                pixels.CopyPixels(new Int32Rect(0, y, pixels.PixelWidth, rows), buffer, stride, 0);
                hash.AppendData(buffer, 0, stride * rows);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
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

    /// <summary>点悬浮球/热键入口：确认卡还开着时，先关旧卡再重新识别最新截图（关完等旧流程释放锁，再走正常流水线）。</summary>
    private async void TriggerRecognizeOrReplaceCard()
    {
        if (_currentCard is { IsVisible: true })
        {
            Logger.Info("确认卡开着→关闭旧卡，重新识别最新截图");
            var card = _currentCard;
            _currentCard = null;
            card.ForceClose("重新识别");
            while (_running) await Task.Delay(50);
        }
        RunPipelineSafe();
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
                SnapshotStore.Save("nocode", null, shot); // 没认出图：留存原图便于复盘
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

        // 识别成功、即将弹确认气泡卡：先收掉托盘启动气泡，避免两个提示并排让客服困惑
        DismissBalloon();

        // 4. 唯一结果窗口＝确认气泡卡：品牌铁证 → 弹卡即自动发起连接；没把握 → 弹卡等客服选软件/手填后点「连接」。
        //    成功卡片自动消失，失败/要密码卡片停留可改可重试（绝不替用户赌品牌）。
        string autoCode = (result.DeviceCode ?? "").Trim().Replace(" ", "");
        bool canAuto = CanAutoConnect(result);
        if (!canAuto)
        {
            string why = !string.IsNullOrEmpty(result.Error) ? "未认出设备码→手填卡"
                : !result.BrandConfident ? "软件类型无铁证→软件选择卡（不替用户赌）"
                : "设备码无效→手填卡";
            Logger.Info($"分流→确认卡（{why}，等人工点连接）");
            // 没能自动直连（未认出码/软件无铁证要人工选/码不足）：不成功，留存原图
            SnapshotStore.Save(!string.IsNullOrEmpty(result.Error) || autoCode.Length < 9 ? "nocode" : "pickbrand",
                               result.DeviceCode, shot);
            var manual = new ConfirmCardWindow(result, shot, MakeCardConnect(shot), _ball?.BoundsRect);
            _currentCard = manual;
            manual.Show();
            await manual.ClosedTask;
            if (_currentCard == manual) _currentCard = null;
            return;
        }
        Logger.Info("分流→自动直连（软件已确定 + 设备码有效）：弹卡并自动发起连接");
        var autoCard = new ConfirmCardWindow(result, shot, MakeCardConnect(shot), _ball?.BoundsRect,
                                             autoConnect: true);
        _currentCard = autoCard;
        autoCard.Show();
        await autoCard.ClosedTask;
        if (_currentCard == autoCard) _currentCard = null;
    }

    /// <summary>软件确定且设备码有效即直连；增强识别、密码待核对本身不再要求先点确认。</summary>
    private static bool CanAutoConnect(RecognizedResult result)
    {
        if (!result.BrandConfident || !string.IsNullOrEmpty(result.Error)) return false;
        string code = (result.DeviceCode ?? "").Trim().Replace(" ", "");
        bool validLength = result.Kind switch
        {
            RemoteAppKind.Sunlogin => code.Length is 9 or 10,
            RemoteAppKind.ToDesk => code.Length == 9,
            _ => false
        };
        return validLength && code.All(c => c is >= '0' and <= '9');
    }

    /// <summary>确认卡「连接」回调：按选定软件/码/密码发起一次可取消连接（35s 看门狗、取消竞态、断开兜底、失败留存），
    /// 终态返回卡片自行流转显示（成功自动关、失败/要密码停留可改可重试）。必须在 UI 线程调用。</summary>
    private Func<RemoteAppKind, string, string, ConnectionContext, Task<ConnectResult>> MakeCardConnect(BitmapSource? shot)
    {
        return (kind, code, pwd, ctx) => CardConnectAsync(kind, code, pwd, ctx, shot);
    }

    /// <summary>卡片发起的一次连接（重试也走这里，每次换新的取消上下文）。
    /// 留存规则：取消不留存；成功仅“已发起未探到会话”留存；失败/要密码留存。</summary>
    private async Task<ConnectResult> CardConnectAsync(RemoteAppKind kind, string code, string pwd,
                                                       ConnectionContext c, BitmapSource? shot)
    {
        string kindName = kind == RemoteAppKind.Sunlogin ? "向日葵" : "ToDesk";
        Logger.Info($"卡片连接发起：{kindName} 码={code} 密码={(string.IsNullOrEmpty(pwd) ? "<免密>" : pwd)}");
        try
        {
            IRemoteConnector connector = kind == RemoteAppKind.Sunlogin
                ? new SunloginConnector()
                : new ToDeskConnector();
            var t = connector.ConnectAsync(code, pwd, c);
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

            // 终态归一放到 UI 线程执行，与"停止按钮点击（也在 UI 线程）"串行，消除点击事件尚在 Dispatcher 队列、
            // 后台线程已抢先按 Fail 定终态的竞态。
            ConnectResult final = res;
            bool disconnecting = false;
            await Dispatcher.InvokeAsync(() =>
            {
                if (c.UserCancelled)
                    final = c.ConnectClicked
                        ? ConnectResult.Cancel("已尝试断开，请确认远程会话已关闭")
                        : ConnectResult.Cancel("已取消，未发起连接");
                disconnecting = c.ConnectClicked && c.Token.IsCancellationRequested;
            });

            if (disconnecting)
            {
                // 已点下连接才取消 = 断开兜底：在 STA 线程关会话窗/发 Esc
                try { await StaRunner.Run(() => { ScreenOps.CloseSession(c); return true; }); }
                catch (Exception ex) { Logger.Error("断开会话失败", ex); }
            }

            // 留存：取消不留；成功仅“已发起”没探到会话窗=未确认留存；失败/要密码留存
            if (final.Status == ConnectStatus.Launched)
            {
                if (final.Message?.Contains("已发起") == true)
                    SnapshotStore.Save("unconfirmed", code, shot);
            }
            else if (final.Status == ConnectStatus.NeedPassword || IsPwdIssue(final.Message))
            {
                SnapshotStore.Save("needpwd", code, shot);
            }
            else if (final.Status == ConnectStatus.Failed)
            {
                SnapshotStore.Save("failed", code, shot);
            }
            return final;
        }
        catch (Exception ex)
        {
            Logger.Error("自动连接异常", ex);
            // 与停止点击同线程读取，避免竞态把用户取消误判成失败
            bool uc = false, clicked = false;
            Dispatcher.Invoke(() => { uc = c.UserCancelled; clicked = c.ConnectClicked; });
            if (uc)
                return c.ConnectClicked
                    ? ConnectResult.Cancel("已尝试断开，请确认远程会话已关闭")
                    : ConnectResult.Cancel("已取消，未发起连接");
            SnapshotStore.Save("failed", code, shot);
            return ConnectResult.Fail("出错了：" + ex.Message);
        }
    }

    /// <summary>失败消息是否属于密码/验证码类（这类在卡片上停留并聚焦密码框；离线/超时等不聚焦）。</summary>
    private static bool IsPwdIssue(string? msg)
        => !string.IsNullOrEmpty(msg) &&
           (msg.Contains("密码") || msg.Contains("验证码") || msg.Contains("验证")
            || msg.Contains("解锁") || msg.Contains("凭据"));

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

    /// <summary>异常截图留存链路自测：载入一张图，对五类不成功各存一张 JPG，再打包当天 zip，打印产物路径。</summary>
    private void RunSnapshotTest(string imgPath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(imgPath));
            bmp.EndInit();
            bmp.Freeze();
            Console.WriteLine($"载入 {Path.GetFileName(imgPath)}  {bmp.PixelWidth}x{bmp.PixelHeight} fmt={bmp.Format}");
            foreach (var k in new[] { "nocode", "pickbrand", "needpwd", "failed", "unconfirmed" })
                Console.WriteLine($"  Save({k}) -> {SnapshotStore.Save(k, "905098332", bmp)}");
            var shots = SnapshotStore.TodayFiles();
            Console.WriteLine($"TodayFiles={shots.Count}");
            foreach (var f in shots) Console.WriteLine("  shot: " + new FileInfo(f).Name + $" ({new FileInfo(f).Length / 1024}KB)");
            string d2 = DateTime.Now.ToString("yyyyMMdd");
            string zpath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"远程连接助手日志_{d2}.zip");
            string? zip = SnapshotStore.ExportZip(Logger.TodayLogPath, d2, zpath);
            Console.WriteLine($"Bundle zip -> {zip ?? "(null)"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("SNAPSHOT_TEST_FAIL: " + ex);
        }
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
            Console.WriteLine($"  品牌把握: {(CanAutoConnect(r) ? "已确定(直连)" : "待核对(弹气泡)")}");
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
        Logger.Info("收到退出指令，开始收尾退出…");
        try { _ball?.Close(); } catch (Exception ex) { Logger.Warn("退出时关闭悬浮球异常：" + ex.Message); }
        try { _clipListener?.Close(); } catch { }
        try { _recognizeEvent?.Dispose(); } catch { }
        _recognizeEvent = null;
        try { _hotkey?.Dispose(); } catch { }
        try
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }
        catch (Exception ex) { Logger.Warn("退出时释放托盘异常：" + ex.Message); }
        try { _menu?.Dispose(); } catch { }
        try { _appIcon?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { Shutdown(); } catch (Exception ex) { Logger.Warn("Shutdown 异常：" + ex.Message); }
        // 硬兜底：任何前台线程/隐藏窗/残留钩子导致 Dispatcher 没退干净时，强制结束进程，保证“退出”一定有效。
        Logger.Info("退出收尾完成，强制结束进程");
        Environment.Exit(0);
    }
}
