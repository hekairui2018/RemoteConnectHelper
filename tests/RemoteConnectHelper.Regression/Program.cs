using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Hotkey;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.UI;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _failed, _passed, _skipped;
    private sealed class SkipTestException(string message) : Exception(message);
    // Dispatcher 测试会触发 Application.Startup；禁止进入托盘/单实例/剪贴板等真实启动流程。
    private sealed class TestApplication : RemoteConnectHelper.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--input-fixture") return InputFixture(args[1]);
        if (args.Length == 2 && args[0] == "--ocr-variants") return OcrVariants(args[1]);
        if (args.Length >= 2 && args[0] == "--probe") return ProbePassword(args[1], args.Length > 2 ? int.Parse(args[2]) : 0);
        var app = new TestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        if (args.Length == 2 && args[0] == "--native-probe")
        {
            try { NativeProbe(args[1]); return 0; }
            finally { app.Shutdown(); }
        }
        Run("different device-code screenshots never reuse the same hash", ImageIdentity);
        Run("keyboard supports Enter, Esc, retries and suppresses held-key repeats", Keyboard);
        Run("autofill keeps countdown; user edit holds card; timeout closes card", Countdown);
        Run("close cancels connection and retains pipeline lock until cleanup", CloseDuringConnect);
        Run("native close and replacement also cancel pending connection", OtherClosePaths);
        Run("exceptions and password failures permit another attempt", Retry);
        Run("device-code validation rejects letters and wrong lengths", ValidateCodes);
        Run("cancelled input never clicks or types", CancelledInput);
        Run("password beside device code remains separate from numeric code", PasswordBesideCode);
        Run("hidden and weak passwords require review; explicit passwordless remains usable", PasswordReview);
        Run("ToDesk security-password capitalization is preserved", PasswordCase);
        Run("reported phone photo recovers complete code and password", ReportedPhoto);
        Run("preview preserves password outside device-code region and inputs stay editable", CompletePreview);
        Run("known software connects automatically despite enhancement or password-review flags", AutoConnectPolicy);
        Run("enhanced auto-connect card starts its connection callback without confirmation", EnhancedCardStarts);
        if (args.Length >= 2 && args[0] == "--ui")
        {
            Run("Sunlogin replaces old password and clears it for passwordless retry", () => PasswordField(args[1]));
            Run("live WPF card screenshots and idle eight-second expiry", () => LiveCards(args[1]));
            Run("real-photo preview stays open for password correction", () => PhotoPreview(args[1]));
        }
        Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed, {_skipped} skipped");
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS: " + name); }
        catch (SkipTestException ex) { _skipped++; Console.WriteLine("SKIP: " + name + " (" + ex.Message + ")"); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL: " + name + "\n" + ex); }
    }

    private static int ProbePassword(string path, int angle)
    {
        BitmapSource image = new BitmapImage(new Uri(System.IO.Path.GetFullPath(path)));
        image.Freeze();
        if (angle != 0) image = RemoteConnectHelper.Services.OcrService.RotateForOcr(image, angle);
        var doc = Task.Run(() => RemoteConnectHelper.Services.OcrService.RecognizeAsync(image)).GetAwaiter().GetResult()!;
        foreach (var w in doc.Words.Where(w => w.Text.Any(char.IsLetterOrDigit)))
            Console.WriteLine($"({w.X:0},{w.Y:0},{w.W:0},{w.H:0}) {w.Text}");
        var selected = new RecognizerEngine().Run(doc);
        Console.WriteLine($"ORIGINAL: code={selected.DeviceCode}, password={selected.Password}");
        var hit = CodeExtractor.FindDeviceCodes(doc, 9, 10).First(h => h.Code == selected.DeviceCode);
        for (int multiplier = 1; multiplier <= 3; multiplier++)
        {
            int x = (int)Math.Ceiling(hit.X + hit.W + 12);
            int y = Math.Max(0, (int)(hit.CenterY - hit.H * multiplier / 2));
            int height = Math.Min(image.PixelHeight - y, (int)Math.Ceiling(hit.H * multiplier));
            var crop = new CroppedBitmap(image, new Int32Rect(x, y, image.PixelWidth - x, height));
            crop.Freeze();
            var refined = Task.Run(() => RemoteConnectHelper.Services.OcrService.RecognizeAsync(crop)).GetAwaiter().GetResult()!;
            Console.WriteLine($"CROP x={x} y={y} h={height}: {refined.FullText.Replace('\n',' ')}");
        }
        return 0;
    }

    private static int OcrVariants(string path)
    {
        var source = new BitmapImage(new Uri(System.IO.Path.GetFullPath(path)));
        source.Freeze();
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = source.PixelWidth * 4;
        var original = new byte[stride * source.PixelHeight];
        bgra.CopyPixels(original, stride, 0);
        foreach (int threshold in new[] { -1, 100, 140, 170, 200 })
        foreach (bool invert in new[] { false, true })
        {
            var pixels = (byte[])original.Clone();
            for (int p = 0; p < pixels.Length; p += 4)
            {
                int gray = (pixels[p] * 114 + pixels[p + 1] * 587 + pixels[p + 2] * 299) / 1000;
                if (threshold >= 0) gray = gray >= threshold ? 255 : 0;
                if (invert) gray = 255 - gray;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = (byte)gray; pixels[p + 3] = 255;
            }
            var processed = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            processed.Freeze();
            var doc = Task.Run(() => RemoteConnectHelper.Services.OcrService.RecognizeAsync(processed)).GetAwaiter().GetResult()!;
            Console.WriteLine($"THRESHOLD={threshold} INVERT={invert}: {doc.FullText.Replace('\n', ' ')}");
        }
        return 0;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void NativeProbe(string output)
    {
        System.IO.Directory.CreateDirectory(output);
        foreach (var kind in new[] { RemoteAppKind.Sunlogin, RemoteAppKind.ToDesk })
        {
            var process = kind == RemoteAppKind.Sunlogin ? "AweSun" : "ToDesk";
            string? exe = kind == RemoteAppKind.Sunlogin ? AppPaths.FindSunlogin() : AppPaths.FindToDesk();
            string cls = kind == RemoteAppKind.Sunlogin ? "FLUTTER_RUNNER_WIN32_WINDOW" : "H-SMILE-FRAME";
            var win = ScreenOps.FindWindow(new[] { process }, new[] { cls }, new[] { "FlutterMultiWindow" });
            Console.WriteLine($"NATIVE {kind}: exe={exe}, window={win}");
            if (win == null) continue;
            Check(ScreenOps.EnsureForeground(win), "Cannot bring actual client to foreground");
            win = ScreenOps.FindWindow(new[] { process }, new[] { cls }, new[] { "FlutterMultiWindow" })!;
            using var bitmap = ScreenOps.CaptureClient(win);
            bitmap.Save(System.IO.Path.Combine(output, kind + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            var doc = Task.Run(() => ScreenOps.OcrAsync(bitmap)).GetAwaiter().GetResult();
            var text = $"NATIVE {kind}: exe={exe}, window={win}\n" + doc?.FullText;
            System.IO.File.WriteAllText(System.IO.Path.Combine(output, kind + ".txt"), text);
            Console.WriteLine(text);
        }
        Console.WriteLine("LOG: " + RemoteConnectHelper.Services.Logger.TodayLogPath);
    }
    private static object? Read(object target, string name) => target.GetType().GetField(name, PrivateInstance)!.GetValue(target);
    private static void Write(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, PrivateInstance | BindingFlags.DeclaredOnly)!.Invoke(target, args);
    private static ConfirmCardWindow Card(string code = "123456789", RemoteAppKind kind = RemoteAppKind.Sunlogin,
        Func<RemoteAppKind, string, string, ConnectionContext, Task<ConnectResult>>? connect = null)
        => new(new RecognizedResult { Kind = kind, DeviceCode = code, Password = "Ab1234" }, null,
            connect ?? ((_, _, _, _) => Task.FromResult(ConnectResult.Fail("local test"))));
    private static void PumpUntil(Func<bool> done, int timeoutMs = 3000)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + timeoutMs;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || Environment.TickCount64 >= deadline) frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        Check(done(), "Timed out waiting for UI continuation");
    }

    private static void ImageIdentity()
    {
        var hash = typeof(RemoteConnectHelper.App).GetMethod("QuickHash", PrivateStatic)!;
        string Hash(BitmapSource b) => (string)hash.Invoke(null, new object[] { b })!;
        var first = RenderCode("123456000");
        Check(Hash(first) == Hash(RenderCode("123456000")), "Identical screenshots miss cache");
        Check(Hash(first) != Hash(RenderCode("123456002")), "Different device code hits old cache");
        var set = new HashSet<string>();
        for (int i = 0; i < 50; i++) Check(set.Add(Hash(RenderCode((123456000 + i).ToString()))), "Screenshot collision");
        var pixels = Enumerable.Repeat((byte)255, 8 * 4).ToArray();
        var a = BitmapSource.Create(2, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var b = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        Check(Hash(a) != Hash(b), "Different dimensions share hash");
    }

    private static void Keyboard()
    {
        using var hook = new KeyHook();
        int enter = 0, esc = 0;
        hook.EnterPressed += () => enter++;
        hook.EscapePressed += () => esc++;
        var buffer = Marshal.AllocHGlobal(32);
        try
        {
            for (int i = 0; i < 32; i++) Marshal.WriteByte(buffer, i, 0);
            IntPtr Send(int key, int msg, int flags = 0)
            {
                Marshal.WriteInt32(buffer, key);
                Marshal.WriteInt32(buffer, 8, flags);
                return (IntPtr)Call(hook, "Callback", 0, (IntPtr)msg, buffer)!;
            }
            Check(Send(13, 0x100) == (IntPtr)1, "Enter not swallowed");
            Check(Send(13, 0x100) == (IntPtr)1 && enter == 1, "Held Enter repeats or leaks");
            Send(13, 0x101);
            Send(27, 0x100); Send(27, 0x101);
            Send(13, 0x100); Send(13, 0x101);
            Send(27, 0x100); Send(27, 0x101);
            Check(enter == 2 && esc == 2, "Enter/Esc stopped working after first use");
            hook.HookEnter = false;
            Send(13, 0x100); Send(13, 0x101);
            Check(enter == 2, "Connecting intercepts remote client's Enter");
            Send(27, 0x100, 0x10); Send(27, 0x101, 0x10);
            Check(esc == 2, "Injected disconnect Esc re-enters user cancellation");
            Send(27, 0x100); Send(27, 0x101);
            Check(esc == 3, "Connecting disables Esc");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Countdown()
    {
        var card = Card();
        Check(!(bool)Read(card, "_held")!, "Autofill held card");
        ((TextBox)card.FindName("CodeBox")).Text = "987654321";
        Check((bool)Read(card, "_held")!, "User edit failed to hold card");
        card.ForceClose("test cleanup");
        var idle = Card();
        Call(idle, "OnLoaded", null, new RoutedEventArgs());
        Write(idle, "_autoDeadline", DateTime.Now.AddSeconds(-1));
        Call(idle, "OnTick", null, EventArgs.Empty);
        Check(idle.ClosedTask.IsCompleted, "Idle card did not close");
    }

    private static void CloseDuringConnect()
    {
        var pending = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConnectionContext? context = null;
        var card = Card(connect: (_, _, _, c) => { context = c; return pending.Task; });
        Call(card, "Connect");
        Check(!((KeyHook)Read(card, "_keyHook")!).HookEnter, "Connecting captures Enter");
        Check(!((FrameworkElement)card.FindName("CandidatePanel")).IsEnabled, "Candidates editable during connection");
        Call(card, "CloseBtn_Click", null, null);
        Check(context!.UserCancelled && context.Token.IsCancellationRequested, "Close did not cancel worker");
        Check(!card.ClosedTask.IsCompleted, "Pipeline released before cancelled worker cleanup");
        pending.SetResult(ConnectResult.Ok()); // Late success must not reopen or update a closed card.
        PumpUntil(() => card.ClosedTask.IsCompleted);
        Check(!((DispatcherTimer)Read(card, "_autoClose")!).IsEnabled, "Late success restarted timer of closed card");
    }

    private static void OtherClosePaths()
    {
        foreach (bool native in new[] { false, true })
        {
            var pending = new TaskCompletionSource<ConnectResult>();
            ConnectionContext? context = null;
            var card = Card(connect: (_, _, _, c) => { context = c; return pending.Task; });
            Call(card, "Connect");
            if (native) card.Close(); else card.ForceClose();
            Check(context!.Token.IsCancellationRequested, "Replacement/native close failed to cancel");
            pending.SetCanceled(context.Token);
            PumpUntil(() => card.ClosedTask.IsCompleted);
        }
    }

    private static void Retry()
    {
        int attempts = 0;
        var contexts = new List<ConnectionContext>();
        var card = Card(connect: (_, _, _, c) =>
        {
            contexts.Add(c);
            return ++attempts == 1 ? Task.FromException<ConnectResult>(new InvalidOperationException("test exception"))
                : Task.FromResult(ConnectResult.NeedPwd("test password correction"));
        });
        Call(card, "Connect"); Call(card, "Connect"); Call(card, "Connect");
        Check(attempts == 3 && contexts.Distinct().Count() == 3, "Failed connection blocked retry or reused cancellation token");
        Check(((KeyHook)Read(card, "_keyHook")!).HookEnter, "Failed connection did not restore Enter");
        card.ForceClose("test cleanup");
    }

    private static void ValidateCodes()
    {
        foreach (var kind in new[] { RemoteAppKind.Sunlogin, RemoteAppKind.ToDesk })
        foreach (var code in new[] { "", "12345678", "abcdefghi", "12345678901", "１２３４５６７８９", "123456789", "1234567890", "123 456 789" })
        {
            bool accepted = false;
            var card = Card(code, kind, (_, _, _, _) => { accepted = true; return Task.FromResult(ConnectResult.Fail("local test")); });
            Call(card, "Connect");
            bool expected = code is "123456789" or "123 456 789" || (kind == RemoteAppKind.Sunlogin && code == "1234567890");
            Check(accepted == expected, $"Wrong validation: {kind} {code}");
            card.ForceClose("test cleanup");
        }
    }

    private static void CancelledInput()
    {
        var context = new ConnectionContext(RemoteAppKind.Sunlogin);
        context.CancelByUser();
        var coordOps = typeof(SunloginConnector).Assembly.GetType("RemoteConnectHelper.Automation.CoordOps")!;
        try
        {
            coordOps.GetMethod("Fill")!.Invoke(null, new object[] { new ScreenOps.Win(), 0d, 0d, "NEVER TYPE", context, false });
            throw new InvalidOperationException("Cancelled input continued");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { }
    }

    private static void PasswordBesideCode()
    {
        var words = new List<OcrWord>
        {
            new() { Text = "向日葵", X = 50, Y = 20, W = 80, H = 24 },
            new() { Text = "111", X = 50, Y = 150, W = 65, H = 40 },
            new() { Text = "222", X = 125, Y = 150, W = 65, H = 40 },
            new() { Text = "333", X = 200, Y = 150, W = 65, H = 40 },
            new() { Text = "Abc", X = 450, Y = 150, W = 65, H = 40 },
            new() { Text = "456", X = 525, Y = 150, W = 65, H = 40 }
        };
        var doc = new OcrDoc { Words = words, FullText = "向日葵\n111 222 333 Abc 456", FlatText = "向日葵111222333Abc456", ImgWidth = 800, ImgHeight = 400 };
        var result = new RecognizerEngine().Run(doc);
        Check(result.DeviceCode == "111222333" && result.Password == "Abc456", "Side-by-side code swallowed password");
        Check(result.PasswordNeedsReview, "Unanchored password was automatically trusted");
    }

    private static OcrDoc PasswordDoc(string label, string password)
    {
        var words = new List<OcrWord>
        {
            new() { Text = "ToDesk", X = 50, Y = 20, W = 80, H = 24 },
            new() { Text = "设备代码", X = 50, Y = 90, W = 100, H = 24 },
            new() { Text = "123456789", X = 50, Y = 130, W = 180, H = 32 },
            new() { Text = label, X = 50, Y = 230, W = 100, H = 24 }
        };
        if (password.Length > 0) words.Add(new() { Text = password, X = 170, Y = 230, W = 140, H = 24 });
        string text = string.Join("\n", words.Select(w => w.Text));
        return new OcrDoc { Words = words, FullText = text, FlatText = text.Replace("\n", ""), ImgWidth = 600, ImgHeight = 400 };
    }

    private static void PasswordReview()
    {
        var engine = new RecognizerEngine();
        var masked = engine.Run(PasswordDoc("临时密码", "••••••"));
        Check(masked.Password == "" && masked.PasswordNeedsReview, "Hidden password mistaken for passwordless");
        var allowed = engine.Run(PasswordDoc("密码可为空", ""));
        Check(!allowed.PasswordNeedsReview, "Explicit passwordless screenshot requires pointless correction");
        var known = engine.Run(PasswordDoc("临时密码", "ab12cd0r"));
        Check(known.Password == "ab12cd0r" && !known.PasswordNeedsReview, "Clear anchored password requires correction");
    }

    private static void PasswordCase()
    {
        var engine = new RecognizerEngine();
        var security = engine.Run(PasswordDoc("安全密码", "AbS7O2"));
        Check(security.Password == "AbS7O2", "User-defined security password lost capitalization");
        var temporary = engine.Run(PasswordDoc("临时密码", "ab12cdOr"));
        Check(temporary.Password == "ab12cd0r", "Temporary-password OCR correction regressed");
    }

    private static void PhotoPreview(string output)
    {
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var samples = LoadPrivatePhotos(root).Where(p => p.Purpose == "preview").ToList();
        if (samples.Count == 0) throw new SkipTestException("no private preview fixtures configured");
        foreach (var sample in samples)
        {
            var source = new BitmapImage(new Uri(System.IO.Path.Combine(root, "testcases", sample.File)));
            source.Freeze();
            var result = new RecognizedResult { Kind = RemoteAppKind.Sunlogin, DeviceCode = sample.Code,
                Password = sample.Password, PasswordNeedsReview = true, ImageRotation = sample.Rotation,
                TargetCodeX = sample.X, TargetCodeY = sample.Y };
            var card = new ConfirmCardWindow(result, source, (_, _, _, _) => Task.FromResult(ConnectResult.Fail("local test")));
            DisableSystemHook(card);
            card.Left = 300; card.Top = 200;
            try
            {
                card.Show(); card.UpdateLayout();
                Check((bool)Read(card, "_held")!, "Review card counts down while password is being checked");
                Check(((Border)card.FindName("PreviewBorder")).Visibility == Visibility.Visible, "Original screenshot is hidden");
                Check(((System.Windows.Controls.Image)card.FindName("SourcePreview")).Source != null, "Photo failed to load");
                var connect = (System.Windows.Controls.Button)card.FindName("ConnectBtn");
                Check(connect.TranslatePoint(new Point(0, connect.ActualHeight), card).Y < card.ActualHeight - 10,
                    "Preview pushes connection controls outside the card");
                Capture(card, System.IO.Path.Combine(output, sample.File + ".card.png"));
                Write(card, "_autoDeadline", DateTime.Now.AddSeconds(-1));
                Call(card, "OnTick", null, EventArgs.Empty);
                Check(!card.ClosedTask.IsCompleted, "Password review card disappears before confirmation");
                Call(card, "PreviewBtn_Click", null, new RoutedEventArgs());
                Check(((Border)card.FindName("PreviewBorder")).Visibility == Visibility.Collapsed, "Preview cannot collapse");
            }
            finally { card.ForceClose("test cleanup"); }
        }
    }

    private sealed record PrivatePhoto(string File, string Code, string Password, double X, double Y,
        int Rotation, int Width, int Height, string Purpose);

    private static List<PrivatePhoto> LoadPrivatePhotos(string root)
    {
        string manifest = System.IO.Path.Combine(root, "testcases/private-fixtures.json");
        if (Environment.GetEnvironmentVariable("RCH_TEST_DISABLE_LOCAL_FIXTURES") == "1"
            || !System.IO.File.Exists(manifest))
            throw new SkipTestException("private screenshots and credentials are not published; see README");
        return System.Text.Json.JsonSerializer.Deserialize<List<PrivatePhoto>>(System.IO.File.ReadAllText(manifest))
            ?? throw new InvalidOperationException("Invalid private fixture manifest");
    }

    private static void ReportedPhoto()
    {
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var sample = LoadPrivatePhotos(root).FirstOrDefault(p => p.Purpose == "ocr")
            ?? throw new SkipTestException("no private OCR fixture configured");
        var source = new BitmapImage(new Uri(System.IO.Path.Combine(root, "testcases", sample.File)));
        source.Freeze();
        var method = typeof(RemoteConnectHelper.App).GetMethod("RecognizeWithOrientationAsync", PrivateInstance)!;
        var application = System.Windows.Application.Current;
        var pair = Task.Run(async () => await (Task<(OcrDoc? doc, RecognizedResult? result)>)
            method.Invoke(application, new object[] { source })!).GetAwaiter().GetResult();
        Check(pair.result != null && pair.result.DeviceCode == sample.Code, "Reported device code was lost or guessed incorrectly");
        Check(pair.result!.Password == sample.Password, "Reported password was replaced by unrelated page text");
        Check(pair.result.ImageEnhanced, "Image-enhancement diagnostic flag was lost");
        Check(source.PixelWidth == sample.Width && source.PixelHeight == sample.Height, "OCR modified original screenshot");
    }

    private static void CompletePreview()
    {
        // 密码距设备码很远，原来的固定 220px 裁剪会删除它所在的像素。
        var source = BitmapSource.Create(1400, 1000, 96, 96, PixelFormats.Bgra32, null,
            new byte[1400 * 1000 * 4], 1400 * 4);
        source.Freeze();
        var card = new ConfirmCardWindow(new RecognizedResult { DeviceCode = "123456789", PasswordNeedsReview = true,
            TargetCodeX = 300, TargetCodeY = 100 }, source, (_, _, _, _) => Task.FromResult(ConnectResult.Fail("local test")));
        try
        {
            var preview = (BitmapSource)((System.Windows.Controls.Image)card.FindName("SourcePreview")).Source;
            Check(preview.PixelWidth == 1400 && preview.PixelHeight == 1000, "Preview crops out password pixels");
            foreach (string fieldName in new[] { "CodeBox", "PwdBox" })
            {
                var field = (TextBox)card.FindName(fieldName);
                Check(field.IsEnabled && !field.IsReadOnly, "Manual correction input is disabled");
                Check(!System.Windows.Input.InputMethod.GetIsInputMethodEnabled(field), "IME can rewrite numeric codes and ASCII passwords");
            }
        }
        finally { card.ForceClose("test cleanup"); }
    }

    private static bool AutomaticallyConnects(RecognizedResult result)
        => (bool)typeof(RemoteConnectHelper.App).GetMethod("CanAutoConnect", PrivateStatic)!.Invoke(null, new object[] { result })!;

    private static void AutoConnectPolicy()
    {
        foreach (var kind in new[] { RemoteAppKind.Sunlogin, RemoteAppKind.ToDesk })
        foreach (bool enhanced in new[] { false, true })
        foreach (bool review in new[] { false, true })
        {
            var result = new RecognizedResult { Kind = kind, DeviceCode = "123456789", BrandConfident = true,
                ImageEnhanced = enhanced, PasswordNeedsReview = review, Password = "Ab1234" };
            Check(AutomaticallyConnects(result), "Enhancement/password review blocks known software");
            result.Password = "";
            Check(AutomaticallyConnects(result), "Empty password requires unnecessary confirmation");
            result.BrandConfident = false;
            Check(!AutomaticallyConnects(result), "Uncertain software connects without selection");
        }
        foreach (var code in new[] { "", "12345678", "12345678901", "abcdefghi", "１２３４５６７８９" })
            Check(!AutomaticallyConnects(new RecognizedResult { Kind = RemoteAppKind.Sunlogin, DeviceCode = code }), "Invalid device code connects automatically");
        Check(AutomaticallyConnects(new RecognizedResult { Kind = RemoteAppKind.Sunlogin, DeviceCode = "1234567890" }), "Ten-digit Sunlogin code rejected");
        Check(!AutomaticallyConnects(new RecognizedResult { Kind = RemoteAppKind.ToDesk, DeviceCode = "1234567890" }), "Ten-digit ToDesk code accepted");
        Check(!AutomaticallyConnects(new RecognizedResult { Kind = RemoteAppKind.Unknown, DeviceCode = "123456789" }), "Unknown software connects automatically");
        Check(!AutomaticallyConnects(new RecognizedResult { Kind = RemoteAppKind.Sunlogin, DeviceCode = "123456789", Error = "missing code" }), "Recognition error ignored");
    }

    private static void EnhancedCardStarts()
    {
        bool connected = false;
        var result = new RecognizedResult { Kind = RemoteAppKind.Sunlogin, DeviceCode = "123456789",
            Password = "Ab1234", BrandConfident = true, ImageEnhanced = true, PasswordNeedsReview = true };
        var card = new ConfirmCardWindow(result, null, (_, code, pwd, _) =>
        {
            Check(code == "123456789" && pwd == "Ab1234", "Automatic callback changes credentials");
            connected = true;
            return Task.FromResult(ConnectResult.Fail("local test, no remote operation"));
        }, autoConnect: AutomaticallyConnects(result));
        try
        {
            Call(card, "OnLoaded", null, new RoutedEventArgs());
            PumpUntil(() => connected);
        }
        finally { card.ForceClose("test cleanup"); }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private sealed record InputState(long Hwnd, int X, int Y, int W, int H,
        double CodeX, double CodeY, double PasswordX, double PasswordY, string Code, string Password,
        long Timestamp, string Focus);

    private static int InputFixture(string stateFile)
    {
        var app = new System.Windows.Application();
        var code = new TextBox { Width = 320, Height = 32, Text = "OLD CODE" };
        var password = new TextBox { Width = 320, Height = 32, Text = "OldPwd" };
        var canvas = new Canvas();
        Canvas.SetLeft(code, 30); Canvas.SetTop(code, 50); canvas.Children.Add(code);
        Canvas.SetLeft(password, 30); Canvas.SetTop(password, 135); canvas.Children.Add(password);
        var window = new Window { Title = "本地输入验收（独立测试进程）", Width = 580, Height = 260,
            Left = 100, Top = 100, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Topmost = true, Content = canvas, Background = Brushes.White };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            var origin = window.PointToScreen(new Point(0, 0));
            var cp = code.PointToScreen(new Point(60, 16));
            var pp = password.PointToScreen(new Point(60, 16));
            var dpi = VisualTreeHelper.GetDpi(window);
            var state = new InputState(new WindowInteropHelper(window).Handle.ToInt64(), (int)origin.X, (int)origin.Y,
                (int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY),
                cp.X - origin.X, cp.Y - origin.Y, pp.X - origin.X, pp.Y - origin.Y, code.Text, password.Text,
                Environment.TickCount64, code.IsKeyboardFocused ? "code" : password.IsKeyboardFocused ? "password" : "other");
            System.IO.File.WriteAllText(stateFile + ".tmp", System.Text.Json.JsonSerializer.Serialize(state));
            System.IO.File.Move(stateFile + ".tmp", stateFile, true);
        };
        window.Loaded += (_, _) => timer.Start();
        window.Closed += (_, _) => timer.Stop();
        app.Run(window);
        return 0;
    }

    private static void PasswordField(string output)
    {
        System.IO.Directory.CreateDirectory(output);
        string stateFile = System.IO.Path.Combine(output, "input-state-" + Guid.NewGuid().ToString("N") + ".json");
        var launch = new System.Diagnostics.ProcessStartInfo(System.IO.Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe"));
        launch.ArgumentList.Add("--input-fixture"); launch.ArgumentList.Add(stateFile);
        using var fixture = System.Diagnostics.Process.Start(launch)!;
        InputState? state = null;
        InputState ReadState()
        {
            try { state = System.Text.Json.JsonSerializer.Deserialize<InputState>(System.IO.File.ReadAllText(stateFile)) ?? state; }
            catch (System.IO.IOException) { }
            return state!;
        }
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            PumpUntil(() => ReadState() != null, 8000);
            hwnd = (IntPtr)state!.Hwnd;
            InputSimulator.BringToFront(hwnd);
            Check(GetForegroundWindow() == hwnd, "Local fixture is not foreground; refusing to type");
            var win = new ScreenOps.Win { Hwnd = hwnd, X = state.X, Y = state.Y, W = state.W, H = state.H };
            var fill = typeof(SunloginConnector).GetMethod("FillCredentials", PrivateStatic)!;
            void Fill(string password, bool slow)
            {
                Check(GetForegroundWindow() == hwnd, "Local fixture lost foreground; refusing to type");
                var finished = new TaskCompletionSource<bool>();
                var thread = new Thread(() =>
                {
                    try
                    {
                        fill.Invoke(null, new object[] { win, state.CodeX, state.CodeY,
                            state.PasswordX, state.PasswordY, "123456789", password,
                            new ConnectionContext(RemoteAppKind.Sunlogin), slow });
                        finished.SetResult(true);
                    }
                    catch (Exception ex) { finished.SetException(ex); }
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA); thread.Start();
                PumpUntil(() => finished.Task.IsCompleted, 8000);
                finished.Task.GetAwaiter().GetResult();
                long completion = Environment.TickCount64;
                PumpUntil(() => ReadState().Timestamp >= completion, 3000);
                Console.WriteLine($"INPUT: expected=[{password}], code=[{state.Code}], password=[{state.Password}], focus={state.Focus}, foreground={GetForegroundWindow()}");
            }
            void Screenshot(string name)
            {
                using var bitmap = ScreenOps.CaptureClient(win);
                bitmap.Save(System.IO.Path.Combine(output, name), System.Drawing.Imaging.ImageFormat.Png);
            }
            Fill("NewPwd", false);
            Screenshot("password-filled.png");
            Check(state.Code == "123456789" && state.Password == "NewPwd", $"Credentials did not replace old values: code=[{state.Code}], password=[{state.Password}]");
            foreach (var password in new[] { "aX123m", "ab12cd0r", "A+^%~(){}[]" })
            {
                Fill(password, false);
                Check(state.Password == password, $"Password changed during native input: expected=[{password}], actual=[{state.Password}]");
            }
            Fill("", false);
            Check(state.Password == "", "Normal passwordless connection retained old password");
            Fill("OldPwd", false);
            Fill("", true);
            Check(state.Password == "", "Slow retry retained old password");
            Screenshot("password-cleared.png");
        }
        catch
        {
            if (hwnd != IntPtr.Zero)
            {
                using var failedScreen = ScreenOps.CaptureScreen();
                failedScreen.Save(System.IO.Path.Combine(output, "input-failed-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
                System.IO.File.WriteAllText(System.IO.Path.Combine(output, "input-failed-state.json"),
                    System.Text.Json.JsonSerializer.Serialize(ReadState()));
            }
            throw;
        }
        finally
        {
            if (hwnd != IntPtr.Zero) PostMessage(hwnd, 0x10, IntPtr.Zero, IntPtr.Zero);
            if (!fixture.WaitForExit(3000)) fixture.Kill();
            if (System.IO.File.Exists(stateFile)) System.IO.File.Delete(stateFile);
        }
    }

    private static void LiveCards(string output)
    {
        System.IO.Directory.CreateDirectory(output);
        var card = Card();
        DisableSystemHook(card);
        card.Left = 300; card.Top = 200;
        try
        {
            card.Show(); card.UpdateLayout();
            Check(!(bool)Read(card, "_held")!, "Showing idle card held it without user interaction");
            Capture(card, System.IO.Path.Combine(output, "card-countdown.png"));
            PumpUntil(() => card.ClosedTask.IsCompleted, 9500);
        }
        finally { card.ForceClose("test cleanup"); }
        var pending = new TaskCompletionSource<ConnectResult>();
        ConnectionContext? context = null;
        var connecting = Card(connect: (_, _, _, c) => { context = c; return pending.Task; });
        DisableSystemHook(connecting);
        connecting.Left = 300; connecting.Top = 200;
        try
        {
            connecting.Show(); connecting.UpdateLayout(); Call(connecting, "Connect");
            Capture(connecting, System.IO.Path.Combine(output, "card-connecting.png"));
            Call(connecting, "Stop");
            Check(context!.UserCancelled, "Live stop did not cancel connection");
            pending.SetResult(ConnectResult.Cancel());
            PumpUntil(() => !(bool)Read(connecting, "_connecting")!);
            Capture(connecting, System.IO.Path.Combine(output, "card-cancelled.png"));
            PumpUntil(() => connecting.ClosedTask.IsCompleted);
        }
        finally { connecting.ForceClose("test cleanup"); }
    }

    private static void DisableSystemHook(ConfirmCardWindow card)
    {
        // Live rendering does not need to intercept the user's global keys.
        var handler = card.GetType().GetMethod("OnSourceInitialized", PrivateInstance | BindingFlags.DeclaredOnly)!;
        card.SourceInitialized -= (EventHandler)handler.CreateDelegate(typeof(EventHandler), card);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var renderDeadline = Environment.TickCount64 + 200;
        PumpUntil(() => Environment.TickCount64 >= renderDeadline);
        var origin = window.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(window);
        int width = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen((int)origin.X, (int)origin.Y, 0, 0, new System.Drawing.Size(width, height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static BitmapSource RenderCode(string code)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 800, 500));
            Draw(dc, "向日葵远程控制", 70, 70, 28);
            Draw(dc, "本机识别码", 70, 180, 24);
            Draw(dc, code, 70, 230, 36);
            Draw(dc, "验证码 Ab1234", 70, 320, 24);
        }
        var bitmap = new RenderTargetBitmap(800, 500, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }
    private static void Draw(DrawingContext dc, string text, double x, double y, double size)
        => dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), size, Brushes.Black, 1), new Point(x, y));
}
