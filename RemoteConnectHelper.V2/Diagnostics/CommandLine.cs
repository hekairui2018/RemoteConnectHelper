using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.Ocr;
using RemoteConnectHelper.V2.UI;

namespace RemoteConnectHelper.V2.Diagnostics;

public static class CommandLine
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            if (args is ["--verify"]) return RegressionSuite.RunAsync().GetAwaiter().GetResult();
            if (args is ["--selftest", var image])
            {
                using var pipeline = new RecognitionPipeline();
                var result = pipeline.AnalyzeAsync(File.ReadAllBytes(image), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args is ["--batch", var folder, var output]) return BatchAsync(folder, output).GetAwaiter().GetResult();
            if (args is ["--evaluate", var images, var manifest, var results]) return Benchmark.RunAsync(images, manifest, results).GetAwaiter().GetResult();
            if (args is ["--render", var png]) { Render(png); return 0; }
            if (args is ["--render", var sample, var rendered]) { Render(rendered, sample); return 0; }
            if (args is ["--verify-ui", var uiSample, var uiPng]) { Render(uiPng, uiSample, true); return 0; }
            Console.Error.WriteLine("--verify | --selftest <image> | --batch <folder> <output.json> | --evaluate <folder> <manifest.json> <output-folder> | --render <output.png>");
            return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task<int> BatchAsync(string folder, string output)
    {
        using var pipeline = new RecognitionPipeline();
        var rows = new List<object>();
        int errors = 0;
        foreach (string file in Directory.EnumerateFiles(folder).Where(p => new[] { ".jpg", ".png", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Order())
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var r = await pipeline.AnalyzeAsync(await File.ReadAllBytesAsync(file), CancellationToken.None);
                rows.Add(new { Image = Path.GetFileName(file), App = r.AppName, Code = r.DeviceCode, Password = r.Password, Decision = r.Decision.ToString(),
                    Variant = r.Document.Variant, Engine = r.Document.Engine, Ms = clock.ElapsedMilliseconds, Blocks = r.Document.Blocks.Count });
                Console.WriteLine($"{Path.GetFileName(file)}: {r.AppName}, code={r.DeviceCode}, pwd={r.Password}, {r.Decision}, {clock.ElapsedMilliseconds}ms");
            }
            catch (Exception ex) { errors++; rows.Add(new { Image = Path.GetFileName(file), Error = ex.Message }); Console.WriteLine("FAIL " + Path.GetFileName(file) + " " + ex.Message); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }
        return rows.Count > 0 && errors == 0 ? 0 : 1;
    }
    private static void Render(string output, string? sample = null, bool verify = false)
    {
        // 仅离屏渲染，不调用 Show/Run，不注册热键、托盘或剪贴板监听。
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new AssistantController { AutoConnect = false };
        if (sample != null) Task.Run(() => controller.RecognizeAsync(File.ReadAllBytes(sample))).GetAwaiter().GetResult();
        var window = new ShellWindow(controller, new Preferences { AutoConnect = false }) { CanClose = true };
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1008, 702)); root.Arrange(new Rect(0, 0, 1008, 702)); root.UpdateLayout();
        if (verify)
        {
            var code = (System.Windows.Controls.TextBox)window.FindName("CodeBox");
            var password = (System.Windows.Controls.TextBox)window.FindName("PasswordBox");
            if (!code.IsEnabled || !password.IsEnabled || code.IsReadOnly || password.IsReadOnly) throw new InvalidOperationException("Recognized fields are not editable");
            var candidates = (System.Windows.Controls.WrapPanel)window.FindName("CodeCandidates");
            foreach (System.Windows.Controls.Button candidate in candidates.Children)
            {
                candidate.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                if (code.Text != candidate.Content.ToString()) throw new InvalidOperationException("Candidate selection did not update device code");
                var chosen = controller.Result!.DeviceCodes.First(c => c.Value == code.Text);
                string expectedPassword = controller.Result.Passwords.Where(p => p.DeviceCandidateId == chosen.Id).MaxBy(p => p.RuleScore)?.Value ?? "";
                if (password.Text != expectedPassword) throw new InvalidOperationException("Candidate password paired incorrectly");
            }
            if (candidates.Children.Count > 0) ((System.Windows.Controls.Button)candidates.Children[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            foreach (var name in new[] { "ConnectButton", "StopButton" })
            {
                var button = (FrameworkElement)window.FindName(name);
                var position = button.TranslatePoint(new System.Windows.Point(0, 0), root);
                if (position.Y < 0 || position.Y + button.ActualHeight > root.ActualHeight) throw new InvalidOperationException("Action button outside viewport");
                for (DependencyObject? parent = VisualTreeHelper.GetParent(button); parent != null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is System.Windows.Controls.ScrollViewer) throw new InvalidOperationException("Action button scrolls away");
            }
            string expectedCode = code.Text;
            code.Text = "123456789"; password.Text = "Aa1234";
            if (code.Text != "123456789" || password.Text != "Aa1234") throw new InvalidOperationException("Manual edit failed");
            code.Text = expectedCode; password.Text = controller.Result?.Password ?? "";
            // Restore the recognized overlays after the intentional edit check.
            if (candidates.Children.Count > 0) ((System.Windows.Controls.Button)candidates.Children[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Console.WriteLine("PASS offscreen fields/edit/candidate pairing/persistent action buttons; native focus and input not tested");
        }
        var bitmap = new RenderTargetBitmap(1060, 742, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 246, 251)), null, new Rect(0, 0, 1060, 742));
        bitmap.Render(visual); bitmap.Render(root);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var stream = File.Create(output); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        window.Close(); controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); app.Shutdown();
    }
}
