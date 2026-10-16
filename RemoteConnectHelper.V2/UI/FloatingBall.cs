using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MenuItem = System.Windows.Controls.MenuItem;

namespace RemoteConnectHelper.V2.UI;

public sealed class FloatingBall : Window
{
    public event Action? ReadRequested;
    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public FloatingBall()
    {
        Title = "二代远程助手"; Width = Height = 62; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        Left = SystemParameters.WorkArea.Right - 86; Top = SystemParameters.WorkArea.Bottom - 110;
        var border = new Border { Background = new SolidColorBrush(Color.FromRgb(49, 99, 222)), CornerRadius = new CornerRadius(31),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(3), Cursor = System.Windows.Input.Cursors.Hand,
            Child = new System.Windows.Controls.Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/app_glyph.png")), Margin = new Thickness(12) } };
        Content = border; ToolTip = "二代远程助手 · 点击读取截图，拖动换位置";
        MouseLeftButtonDown += (_, e) =>
        {
            var start = PointToScreen(e.GetPosition(this)); DragMove();
            var end = PointToScreen(System.Windows.Input.Mouse.GetPosition(this));
            if ((end - start).Length < 5) ReadRequested?.Invoke();
        };
        var menu = new System.Windows.Controls.ContextMenu();
        Add("读取截图", () => ReadRequested?.Invoke()); Add("显示助手", () => OpenRequested?.Invoke()); Add("退出二代助手", () => ExitRequested?.Invoke());
        ContextMenu = menu;
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }
}
