using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Infrastructure;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Window = System.Windows.Window;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Panel = System.Windows.Controls.Panel;

namespace RemoteConnectHelper.V2.UI;

public partial class ShellWindow : Window
{
    private readonly AssistantController _controller;
    private readonly Preferences _preferences;
    private RecognitionResult? _displayed;
    private byte[]? _displayedImage;
    private bool _updating;
    private Window? _preview;
    private long _sourceRevision;
    private DeviceCodeCandidate? _markedCode;
    private PasswordCandidate? _markedPassword;
    public event Action? BallRequested;
    public event Action? HiddenByUser;
    public bool CanClose { get; set; }
    public bool WatchClipboard => WatchCheck.IsChecked == true;

    public ShellWindow(AssistantController controller, Preferences preferences)
    {
        InitializeComponent(); _controller = controller; _preferences = preferences;
        // 工作区较小时收进屏幕内，底部连接按钮仍保持可见。
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        _updating = true; AppBox.SelectedIndex = 0;
        AutoCheck.IsChecked = preferences.AutoConnect; WatchCheck.IsChecked = preferences.WatchClipboard;
        _controller.AutoConnect = preferences.AutoConnect; _updating = false;
        VersionText.Text = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "2.0.0";
        controller.Changed += Controller_Changed;
        UpdateState();
    }
    private void Controller_Changed()
    {
        if (Dispatcher.CheckAccess()) UpdateState();
        else Dispatcher.BeginInvoke(new Action(UpdateState));
    }
    private void UpdateState()
    {
        if (_controller.Image is { } bytes && !ReferenceEquals(bytes, _displayedImage))
        {
            _displayedImage = bytes;
            _displayed = null; Overlay.Children.Clear();
            _markedCode = null; _markedPassword = null;
            _updating = true;
            AppBox.SelectedIndex = 0; CodeBox.Clear(); PasswordBox.Clear();
            CodeCandidates.Children.Clear(); PasswordCandidates.Children.Clear(); DetailsText.Clear();
            _updating = false;
            _preview?.Close();
            using var stream = new MemoryStream(bytes);
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream; source.EndInit(); source.Freeze();
            SourceImage.Source = source; EmptyImage.Visibility = Visibility.Collapsed;
        }
        if (_controller.Result is { } result && !ReferenceEquals(result, _displayed))
        {
            _displayed = result; _updating = true;
            _markedCode = result.SelectedCode; _markedPassword = result.SelectedPassword;
            AppBox.SelectedIndex = (int)result.App;
            CodeBox.Text = result.DeviceCode; PasswordBox.Text = result.Password;
            CodeCandidates.Children.Clear(); PasswordCandidates.Children.Clear();
            foreach (var code in result.DeviceCodes.Count > 1 ? result.DeviceCodes.Take(5) : [])
                AddCandidate(CodeCandidates, code.Value, () => SelectCode(code));
            FillPasswordCandidates(result.SelectedCode?.Id);
            DetailsText.Text = result.Document.Engine + " · " + result.Document.ElapsedMs + " ms · " + result.Document.Blocks.Count + " 个文字块\n"
                + string.Join("\n", result.SelectedCode?.Evidence ?? [])
                + (result.Warnings.Count > 0 ? "\n" + string.Join("\n", result.Warnings) : "");
            _updating = false; DrawOverlay();
        }
        StatusText.Text = _controller.Status;
        StatusTitle.Text = _controller.Stage switch
        {
            AssistantStage.Recognizing => "正在识别", AssistantStage.Connecting => "正在连接",
            AssistantStage.Launched => "已发起连接", AssistantStage.Failed => "可以修改后重试",
            AssistantStage.Cancelled => "已停止", AssistantStage.NeedInput => "请核对连接信息", _ => "等待截图"
        };
        bool editing = !_controller.Busy;
        AppBox.IsEnabled = CodeBox.IsEnabled = PasswordBox.IsEnabled = editing;
        CodeCandidates.IsEnabled = PasswordCandidates.IsEnabled = editing;
        ConnectButton.IsEnabled = editing && ConnectionPolicy.ValidCode(SelectedApp, CodeBox.Text);
        StopButton.IsEnabled = _controller.Busy;
    }
    private RemoteApp SelectedApp => AppBox.SelectedIndex switch { 1 => RemoteApp.Sunlogin, 2 => RemoteApp.ToDesk, _ => RemoteApp.Unknown };
    private static void AddCandidate(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 5, 5), FontSize = 11 };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void SelectCode(DeviceCodeCandidate code)
    {
        _updating = true;
        AppBox.SelectedIndex = (int)code.App; CodeBox.Text = code.Value;
        var pwd = _displayed?.Passwords.Where(p => p.DeviceCandidateId == code.Id).MaxBy(p => p.RuleScore);
        PasswordBox.Text = pwd?.Value ?? ""; FillPasswordCandidates(code.Id);
        _markedCode = code; _markedPassword = pwd;
        _updating = false; DrawOverlay(); UpdateConnectEnabled();
    }
    private void FillPasswordCandidates(string? codeId)
    {
        PasswordCandidates.Children.Clear();
        foreach (var candidate in (_displayed?.Passwords ?? []).Where(p => p.DeviceCandidateId == codeId && p.Value != PasswordBox.Text).Take(3))
            AddCandidate(PasswordCandidates, candidate.Value, () =>
            {
                _updating = true; PasswordBox.Text = candidate.Value; _updating = false;
                _markedPassword = candidate; DrawOverlay(); UpdateConnectEnabled();
            });
    }
    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if (_displayed == null || SourceImage.Source is not BitmapSource source || PreviewArea.ActualWidth <= 0) return;
        double scale = Math.Min(PreviewArea.ActualWidth / source.PixelWidth, PreviewArea.ActualHeight / source.PixelHeight);
        double dx = (PreviewArea.ActualWidth - source.PixelWidth * scale) / 2, dy = (PreviewArea.ActualHeight - source.PixelHeight * scale) / 2;
        if (_markedCode is { } code) AddBox(code.Bounds, "#3565DF");
        if (_markedPassword is { } password) AddBox(password.Bounds, "#E99230");
        void AddBox(Box bounds, string color)
        {
            var rectangle = new System.Windows.Shapes.Rectangle { Width = Math.Max(2, bounds.Width * scale), Height = Math.Max(2, bounds.Height * scale),
                Stroke = new BrushConverter().ConvertFromString(color) as Brush, StrokeThickness = 2, RadiusX = 3, RadiusY = 3 };
            Canvas.SetLeft(rectangle, dx + bounds.X * scale); Canvas.SetTop(rectangle, dy + bounds.Y * scale); Overlay.Children.Add(rectangle);
        }
    }
    public async Task ReadClipboardAsync()
    {
        long revision = Interlocked.Increment(ref _sourceRevision);
        try
        {
            var source = RemoteConnectHelper.Services.ClipboardImage.TryGet();
            if (source == null) { _controller.ShowMessage("剪贴板里没有图片。先框选截图，再点「读取截图」。"); return; }
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(stream);
            if(revision == _sourceRevision) await _controller.RecognizeAsync(stream.ToArray());
        }
        catch (Exception ex) { _controller.ShowMessage("读取截图失败：" + ex.Message); }
    }
    private async Task ReadFileAsync(string path)
    {
        long revision = Interlocked.Increment(ref _sourceRevision);
        try { var bytes = await File.ReadAllBytesAsync(path); if(revision == _sourceRevision) await _controller.RecognizeAsync(bytes); }
        catch (Exception ex) { _controller.ShowMessage("图片未能打开：" + ex.Message); }
    }
    private async void Read_Click(object sender, RoutedEventArgs e) => await ReadClipboardAsync();
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) await ReadFileAsync(dialog.FileName);
    }
    private async void Connect_Click(object sender, RoutedEventArgs e) => await _controller.ConnectAsync(SelectedApp, CodeBox.Text.Trim().Replace(" ", ""), PasswordBox.Text);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await _controller.StopAsync();
    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _preferences.AutoConnect = AutoCheck.IsChecked == true; _preferences.WatchClipboard = WatchCheck.IsChecked == true;
        _controller.AutoConnect = _preferences.AutoConnect; _preferences.Save();
    }
    private void AppBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && _controller != null) UpdateConnectEnabled(); }
    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || _controller == null) return;
        if (ReferenceEquals(sender, CodeBox)) _markedCode = null;
        if (ReferenceEquals(sender, PasswordBox)) _markedPassword = null;
        DrawOverlay(); UpdateConnectEnabled();
    }
    private void UpdateConnectEnabled() => ConnectButton.IsEnabled = !_controller.Busy && ConnectionPolicy.ValidCode(SelectedApp, CodeBox.Text);
    private void Ball_Click(object sender, RoutedEventArgs e) => BallRequested?.Invoke();
    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Root); Process.Start(new ProcessStartInfo("explorer.exe", Log.Root) { UseShellExecute = true });
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (SourceImage.Source is not BitmapSource source) return;
        if (_preview != null) { _preview.Activate(); return; }
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new System.Windows.Controls.Image { Source = source, Width = source.PixelWidth, Height = source.PixelHeight, Stretch = Stretch.None } };
        _preview = new Window { Title = "完整原图", Content = viewer, Owner = this, Width = Math.Min(1100, SystemParameters.WorkArea.Width - 60),
            Height = Math.Min(780, SystemParameters.WorkArea.Height - 80), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _preview.Closed += (_, _) => _preview = null; _preview.Show();
    }
    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 输入框内仍使用标准文本粘贴；图片粘贴快捷键不抢占手工改码。
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        { e.Handled = true; await ReadClipboardAsync(); }
        else if (e.Key == Key.Escape && _controller.Busy) { e.Handled = true; await _controller.StopAsync(); }
        else if (e.Key == Key.Enter && ConnectButton.IsEnabled) { e.Handled = true; await _controller.ConnectAsync(SelectedApp, CodeBox.Text.Trim().Replace(" ", ""), PasswordBox.Text); }
    }
    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } paths) await ReadFileAsync(paths[0]);
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (CanClose) { _controller.Changed -= Controller_Changed; _preview?.Close(); return; }
        e.Cancel = true; Hide(); HiddenByUser?.Invoke();
    }
    private void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => DrawOverlay();
    public void SetHotkeyAvailable(bool available) { if (!available) FooterText.Text = "快捷键被其他程序占用，请先退出旧助手；仍可点击读取截图"; }
}
