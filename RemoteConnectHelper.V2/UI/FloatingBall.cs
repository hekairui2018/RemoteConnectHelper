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
    public event Action? MenuRequested;
    private bool _down,_moved;
    private System.Windows.Point _start;
    private double _left,_top;
    public FloatingBall()
    {
        Title = "灰Desk"; Width = Height = 62; WindowStyle = WindowStyle.None;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        Left = SystemParameters.WorkArea.Right - 86; Top = SystemParameters.WorkArea.Bottom - 110;
        var logo=new System.Windows.Controls.Image { Source=new BitmapImage(new Uri("pack://application:,,,/Assets/app_logo.png")), Stretch=Stretch.Uniform, UseLayoutRounding=true, SnapsToDevicePixels=true, Cursor=System.Windows.Input.Cursors.Hand };
        RenderOptions.SetBitmapScalingMode(logo,BitmapScalingMode.HighQuality);
        Content = logo; ToolTip = "灰Desk · 点击识别并连接，拖动换位置";
        SourceInitialized += (_, _) => DesktopWindows.Passive(this, true);
        MouseLeftButtonDown += (_, e) =>
        {
            _down=true;_moved=false;_start=PointToScreen(e.GetPosition(this));_left=Left;_top=Top;CaptureMouse();e.Handled=true;
        };
        MouseMove+=(_,e)=>
        {
            if(!_down)return;var point=PointToScreen(e.GetPosition(this));var delta=point-_start;
            if(delta.Length>=5)_moved=true;if(!_moved)return;
            var dpi=VisualTreeHelper.GetDpi(this);Left=_left+delta.X/dpi.DpiScaleX;Top=_top+delta.Y/dpi.DpiScaleY;
        };
        MouseLeftButtonUp+=(_,e)=>{if(!_down)return;_down=false;ReleaseMouseCapture();if(!_moved)ReadRequested?.Invoke();e.Handled=true;};
        LostMouseCapture+=(_,_)=>_down=false;
        MouseRightButtonUp+=(_,e)=>{if(MenuRequested!=null){e.Handled=true;MenuRequested.Invoke();}};
        var menu = new System.Windows.Controls.ContextMenu();
        Add("识别并连接", () => ReadRequested?.Invoke()); Add("显示助手", () => OpenRequested?.Invoke()); Add("退出二代助手", () => ExitRequested?.Invoke());
        ContextMenu = menu;
        void Add(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }
}
