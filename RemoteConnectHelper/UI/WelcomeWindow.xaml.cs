using System.Windows;
using System.Windows.Input;

namespace RemoteConnectHelper.UI;

public partial class WelcomeWindow : Window
{
    /// <summary>用户在窗口里选择的"开机自启"状态，关闭后由 App 读取并应用。</summary>
    public bool WantAutoStart { get; private set; }

    /// <summary>是否勾选"以后不再自动弹出"。</summary>
    public bool DontShowAgain { get; private set; }

    /// <param name="autoStartEnabled">注册表里当前的开机自启状态。</param>
    /// <param name="firstTime">是否首次启动（首次默认推荐勾选开机自启）。</param>
    public WelcomeWindow(bool autoStartEnabled, bool firstTime = false)
    {
        InitializeComponent();
        Icon = RemoteConnectHelper.App.AppIconImage;
        // 首次启动默认推荐开机自启；之后从托盘"使用说明"打开则反映当前真实设置
        ChkAutoStart.IsChecked = firstTime || autoStartEnabled;
        ChkDontShow.IsChecked = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 窗口未被鼠标捕获时 DragMove 会抛，忽略 */ }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        WantAutoStart = ChkAutoStart.IsChecked == true;
        DontShowAgain = ChkDontShow.IsChecked == true;
        Close();
    }
}
