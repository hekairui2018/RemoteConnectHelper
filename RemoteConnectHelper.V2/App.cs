using System.Windows;
using RemoteConnectHelper.Hotkey;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Infrastructure;
using RemoteConnectHelper.V2.UI;
using Forms=System.Windows.Forms;

namespace RemoteConnectHelper.V2;
public sealed class App : System.Windows.Application
{
    private AssistantController? _controller;
    private DesktopHost? _host;
    private Preferences? _preferences;
    private FloatingBall? _ball;
    private HotkeyManager? _hotkey;
    private RemoteConnectHelper.UI.ClipboardListenerWindow? _clipboard;
    private Forms.NotifyIcon? _tray;
    private EventWaitHandle? _openSignal;
    private RegisteredWaitHandle? _openWait;
    private bool _exiting;
    [STAThread]
    public static int Main(string[] args)
    {
        // Diagnostics branch before tray/hotkeys/real connector creation.
        if(args.Length>0)return Diagnostics.CommandLine.Run(args);
        using var mutex=new Mutex(true,"Local\\GrayDeskV2-SingleInstance",out bool owns);
        if(!owns){try{using var existing=EventWaitHandle.OpenExisting("Local\\GrayDeskV2-Open");existing.Set();}catch(WaitHandleCannotBeOpenedException){}return 0;}
        AppDomain.CurrentDomain.UnhandledException+=(_,e)=>{if(e.ExceptionObject is Exception ex)Log.Write("进程未处理异常 "+ex.GetType().FullName+" "+ex.StackTrace);};
        var app=new App{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.DispatcherUnhandledException+=(_,e)=>{Log.Write("UI exception "+e.Exception.GetType().Name+" "+e.Exception.StackTrace);e.Handled=true;app._controller?.ShowMessage("操作未完成，请重试；详情已写入本地日志");};
        app.Run();return 0;
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);_preferences=Preferences.Load();ThemeManager.Apply(_preferences.Theme);Microsoft.Win32.SystemEvents.UserPreferenceChanged+=SystemThemeChanged;_controller=new();_host=new(_controller,_preferences);
        MainWindow=_host.Card;
        _=_host.RefreshRunningSoftwareAsync();
        try{_host.Card.LoadWallpaper(_preferences.WallpaperPath);}catch(Exception ex){Log.Write("壁纸读取失败 "+ex.GetType().Name);}
        _ball=new(){Opacity=_preferences.EffectiveFloatingOpacity};
        _ball.ReadRequested+=()=>{_host.SetEntryPlacement(_ball);_=ReadAsync(false);};_ball.OpenRequested+=()=>{_host.SetEntryPlacement(_ball);_host.OpenCurrent();};_ball.ExitRequested+=()=>_=ExitAsync();
        if(_preferences.ShowFloatingBall)_ball.Show();
        _host.SetEntryPlacement(_preferences.ShowFloatingBall?_ball:null);
        _hotkey=new();_hotkey.Pressed+=()=>{_host.SetEntryPlacement(_ball.IsVisible?_ball:null);_=ReadAsync(false);};
        bool hotkeyOk=_hotkey.Register(0x0002|0x0001,0x52);
        _clipboard=new();_clipboard.ClipboardSettled+=()=>{if(_preferences.WatchClipboard&&_preferences.ClipboardConsent)_=ReadAsync(true);};_clipboard.Start();
        _tray=new Forms.NotifyIcon{Text=hotkeyOk?"灰Desk · Ctrl+Alt+R":"灰Desk · 快捷键被占用，请使用托盘",
            Icon=LoadTrayIcon(),Visible=true};
        var menu=new Forms.ContextMenuStrip();bool menuFromBall=false;
        void PositionFromMenu()=>_host.SetEntryPlacement(menuFromBall?_ball:null);
        menu.Items.Add("识别并连接  Ctrl+Alt+R",null,(_,_)=>{PositionFromMenu();_=ReadAsync(false);});
        var showMain=new Forms.ToolStripMenuItem("显示主界面");
        showMain.Click+=(_,_)=>{PositionFromMenu();_host.SetMainVisible(!_host.MainVisible);};menu.Items.Add(showMain);
        var listening=new Forms.ToolStripMenuItem("监听剪贴板");
        listening.Click+=(_,_)=>{
            if(!_preferences.ClipboardConsent){_host.ShowHelp();return;}
            _preferences.WatchClipboard=!_preferences.WatchClipboard;_host.ApplySettings();
        };
        var stop=menu.Items.Add("停止当前操作",null,(_,_)=>_=_host.StopAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(listening);
        var appearance=new Forms.ToolStripMenuItem("外观");
        var ballItem=new Forms.ToolStripMenuItem("显示悬浮球"){Checked=_preferences.ShowFloatingBall,CheckOnClick=true};
        ballItem.CheckedChanged+=(_,_)=>{_preferences.ShowFloatingBall=ballItem.Checked;_preferences.Save();if(ballItem.Checked)_ball.Show();else _ball.Hide();_host.SetEntryPlacement(ballItem.Checked?_ball:null);};
        menu.Items.Add(ballItem);
        var themeMenu=ThemeManager.CreateMenu(mode=>{_preferences.Theme=mode;_preferences.Save();ThemeManager.Apply(mode);});
        themeMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        themeMenu.DropDownItems.Add("本地壁纸…",null,(_,_)=>{
            _host.OpenCurrent();
            var pickerPrefs=new Preferences{LastImageFolder=_preferences.LastWallpaperFolder,ImagePickerBounds=_preferences.WallpaperPickerBounds};
            string? path;try{path=ImageFilePicker.Show(_host.Card,pickerPrefs,clientGuid:new Guid("542FD339-96D5-42DE-B711-9123AA0A2F42"));}
            finally{_preferences.LastWallpaperFolder=pickerPrefs.LastImageFolder;_preferences.WallpaperPickerBounds=pickerPrefs.ImagePickerBounds;_preferences.Save();}
            if(path==null)return;
            try{_host.Card.LoadWallpaper(path);_preferences.WallpaperPath=path;_preferences.Save();}
            catch(Exception ex){System.Windows.MessageBox.Show(_host.Card,"壁纸未能打开："+ex.Message,"灰Desk",MessageBoxButton.OK,MessageBoxImage.Information);}
        });
        themeMenu.DropDownItems.Add("樱花飘落 GIF",null,(_,_)=>{
            string path=System.IO.Path.Combine(AppContext.BaseDirectory,"Wallpapers","樱花飘落_1920x1080.gif");
            try{_host.Card.LoadWallpaper(path);_preferences.WallpaperPath=path;_preferences.Save();}
            catch(Exception ex){System.Windows.MessageBox.Show(_host.Card,"壁纸未能打开："+ex.Message,"灰Desk");}
        });
        themeMenu.DropDownItems.Add("雪峰飘雪 GIF",null,(_,_)=>{
            string path=System.IO.Path.Combine(AppContext.BaseDirectory,"Wallpapers","雪峰飘雪_1920x1080.gif");
            try{_host.Card.LoadWallpaper(path);_preferences.WallpaperPath=path;_preferences.Save();}
            catch(Exception ex){System.Windows.MessageBox.Show(_host.Card,"壁纸未能打开："+ex.Message,"灰Desk");}
        });
        var clearWallpaper=themeMenu.DropDownItems.Add("清除壁纸",null,(_,_)=>{_host.Card.LoadWallpaper(null);_preferences.WallpaperPath=null;_preferences.Save();});
        themeMenu.DropDownOpening+=(_,_)=>clearWallpaper.Enabled=_host.Card.HasWallpaper;
        appearance.DropDownItems.Add(themeMenu);
        var opacityMenu=new Forms.ToolStripMenuItem("透明度");
        var opacityPicker=new OpacityPicker();
        var opacityHost=new Forms.ToolStripControlHost(opacityPicker){AutoSize=false,Size=opacityPicker.Size,Margin=Forms.Padding.Empty,Padding=Forms.Padding.Empty};
        opacityMenu.DropDownItems.Add(opacityHost);
        opacityPicker.SetValue((int)Math.Round(_preferences.EffectiveFloatingOpacity*100));
        opacityPicker.ValueChanged+=percent=>{
            double level=percent/100d;_preferences.FloatingOpacity=level;_ball.Opacity=level;menu.Opacity=level;appearance.DropDown.Opacity=level;opacityMenu.DropDown.Opacity=level;
        };
        opacityMenu.DropDownOpening+=(_,_)=>{opacityPicker.SetValue((int)Math.Round(_preferences.EffectiveFloatingOpacity*100));opacityMenu.DropDown.Opacity=_preferences.EffectiveFloatingOpacity;};
        opacityMenu.DropDown.Closed+=(_,_)=>{opacityPicker.EndStep();_preferences.Save();};
        appearance.DropDownItems.Add(opacityMenu);menu.Items.Add(appearance);
        var more=new Forms.ToolStripMenuItem("更多");
        more.DropDownItems.Add("打开图片…",null,(_,_)=>{PositionFromMenu();_=_host.OpenFileAsync();});
        more.DropDownItems.Add(new Forms.ToolStripSeparator());
        more.DropDownItems.Add("快捷操作说明",null,(_,_)=>_host.ShowQuickHelp());
        more.DropDownItems.Add("软件说明",null,(_,_)=>_host.ShowHelp());
        more.DropDownItems.Add("远程软件…",null,(_,_)=>_host.ShowSoftware());
        more.DropDownItems.Add("打开日志",null,(_,_)=>_host.OpenLogs());
        menu.Items.Add(more);menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出",null,(_,_)=>_=ExitAsync());
        DesktopWindows.KeepSubmenusOnMonitor(menu);
        menu.Opening+=(_,_)=>{
            menu.Opacity=_preferences.EffectiveFloatingOpacity;
            showMain.Checked=_host.MainVisible;stop.Visible=_controller.Busy;stop.Enabled=_controller.Busy;listening.Checked=_preferences.WatchClipboard&&_preferences.ClipboardConsent;
            listening.Text="剪贴板监听";
        };
        ThemeManager.Changed+=()=>ThemeManager.ApplyMenu(menu);ThemeManager.ApplyMenu(menu);
        _tray.ContextMenuStrip=menu;_tray.MouseDown+=(_,_)=>menuFromBall=false;_tray.DoubleClick+=(_,_)=>{_host.SetEntryPlacement(null);_host.OpenCurrent();};
        _ball.MenuRequested+=()=>{menuFromBall=true;DesktopWindows.ShowBallMenu(menu,_ball,_preferences.EffectiveFloatingOpacity);};
        _ball.ContextMenu=null;
        _host.StateChanged+=()=>Dispatcher.BeginInvoke(new Action(()=>
        {
            if(_tray==null)return;
            string text="灰Desk · "+(_host.PendingClipboard?"有新截图，按快捷键读取":_controller.Stage switch{
                AssistantStage.Recognizing=>"正在识别",AssistantStage.Connecting=>"连接中",AssistantStage.NeedInput=>"待核对",
                AssistantStage.Failed=>"可修改后重试",_=>"Ctrl+Alt+R"});
            _tray.Text=text.Length>63?text[..63]:text;
        }));
        _openSignal=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\GrayDeskV2-Open");
        _openWait=ThreadPool.RegisterWaitForSingleObject(_openSignal,(_,_)=>Dispatcher.BeginInvoke(new Action(()=>_host.OpenCurrent())),null,Timeout.Infinite,false);
        Dispatcher.BeginInvoke(new Action(()=>{_host.Card.RestorePreviewVisibility(true);_host.OpenCurrent();}));
        Log.Write("灰Desk启动 "+System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion);
        if(!_preferences.ClipboardConsent && _preferences.WatchClipboard)
            Dispatcher.BeginInvoke(new Action(()=>_host.ShowHelp()));
    }
    private static System.Drawing.Icon LoadTrayIcon(){
        using var stream=System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        using var source=new System.Drawing.Icon(stream,System.Windows.Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)source.Clone();
    }
    private void SystemThemeChanged(object sender,Microsoft.Win32.UserPreferenceChangedEventArgs e){if(!_exiting&&_preferences?.Theme=="System")Dispatcher.BeginInvoke(new Action(()=>ThemeManager.Apply("System")));}
    private Task ReadAsync(bool background)=>_host==null||_exiting?Task.CompletedTask:background?_host.ReadClipboardAsync(true):_host.ReadAndConnectAsync(useDisplayed:false);
    private async Task ExitAsync()
    {
        if(_exiting)return;_exiting=true;
        try{
            Microsoft.Win32.SystemEvents.UserPreferenceChanged-=SystemThemeChanged;
            _openWait?.Unregister(null);_openSignal?.Dispose();_hotkey?.Dispose();_clipboard?.Close();
            if(_host!=null)await _host.ShutdownAsync();
            if(_controller!=null)await _controller.DisposeAsync();
            _host?.Card.ShutdownCard();
            _ball?.Close();if(_tray!=null){_tray.Visible=false;_tray.Dispose();}
        }finally{Shutdown();}
    }
}
