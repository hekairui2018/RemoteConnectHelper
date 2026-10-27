using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.Connections;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Parsing;
using RemoteConnectHelper.V2.UI;
using SkiaSharp;
using Window=System.Windows.Window;
using TextBox=System.Windows.Controls.TextBox;
using Button=System.Windows.Controls.Button;

namespace RemoteConnectHelper.V2.Diagnostics;

/// <summary>Shows only own synthetic windows; both OCR and connector dependencies are replaced.</summary>
public static class DesktopInteractionTests
{
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    private delegate void WinEventCallback(IntPtr hook,uint eventId,IntPtr window,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint minimum,uint maximum,IntPtr module,WinEventCallback callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect {public int L,T,R,B;}
    private static readonly List<object> Results=[];
    private static string _output="";
    private static int _failed;
    private static string? _baselineV1;
    public static int Run(string output,string? baselineV1=null)
    {
        _output=Path.GetFullPath(output);Directory.CreateDirectory(_output);Results.Clear();_failed=0;
        _baselineV1=baselineV1;
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            try{await Scenarios();}
            catch(Exception ex){_failed++;Results.Add(new{Name="harness",Passed=false,Error=ex.ToString()});}
            finally{
                await File.WriteAllTextAsync(Path.Combine(_output,"interaction-results.json"),JsonSerializer.Serialize(new{
                    CompletedAt=DateTimeOffset.Now,Scope="Real HWND tests with synthetic images, fake OCR and fake connector; no real remote connections",
                    ForegroundTestKind="Own simulated chat window and actual assistant HWNDs, not production WeChat",
                    Tests=Results,Failed=_failed
                },new JsonSerializerOptions{WriteIndented=true}));
                foreach(Window w in app.Windows.Cast<Window>().ToArray())try{if(w is ConnectionCard card)card.ShutdownCard();else w.Close();}catch{}
                app.Shutdown();
            }
        }));
        app.Run();Console.WriteLine($"DESKTOP RESULT {Results.Count-_failed} passed, {_failed} failed");return _failed==0?0:1;
    }
    public static int RunSoftware(string output)
    {
        _output=Path.GetFullPath(output);Directory.CreateDirectory(_output);int exit=1;
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            await using var rig=new Rig();var preferences=new Infrastructure.Preferences();int saved=0;
            var catalog=new Discovery.SoftwareCatalog(preferences,()=>saved++);var card=new ConnectionCard(rig.Controller,catalog);var manager=new SoftwareManagerWindow(catalog);
            try{
                ThemeManager.Apply("Light");card.RestorePreviewVisibility(true);card.EnterEditor();manager.Show();await Settle();
                IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject{
                    for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var descendant in Descendants<T>(child))yield return descendant;}
                }
                var rename=Descendants<Button>(manager).First(b=>b.Content?.ToString()=="更换名称");manager.RenamePrompt=_=>"客户向日葵";rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle();
                Click(card,"SoftwarePickerButton");var dropdown=Control<Button>(card,"SoftwarePickerButton").ContextMenu!;
                Check(dropdown.Items.OfType<System.Windows.Controls.MenuItem>().Any(m=>m.Header?.ToString()=="客户向日葵"),"Rename did not sync to main selector");dropdown.IsOpen=false;
                var checks=Descendants<System.Windows.Controls.CheckBox>(manager).ToArray();Check(checks.Length==2,"Per-row delete checkboxes missing");checks[0].IsChecked=true;
                string? question=null;manager.ConfirmDelete=text=>{question=text;return false;};manager.DeleteChecked();Check(catalog.Entries.Count==2&&question!.Contains("客户向日葵")&&question.Contains("不删除或卸载"),"Delete confirmation incorrect or cancellation ignored");
                manager.ConfirmDelete=_=>true;manager.DeleteChecked();Check(catalog.Entries.Count==1,"Confirmed delete did not remove selected row");
                Check(Control<System.Windows.Controls.ComboBox>(card,"AppBox").Items.Count==2,"Deleted row remained in main selector");
                catalog.Add(Path.Combine(_output,"ExtraLauncher.exe"),validate:false);await Settle();
                Check(catalog.Entries.Last().Name=="ExtraLauncher"&&catalog.Entries.Last().App==RemoteApp.Unknown,"Executable default name or unsupported classification wrong");
                Click(card,"SoftwarePickerButton");dropdown=Control<Button>(card,"SoftwarePickerButton").ContextMenu!;
                var extra=dropdown.Items.OfType<System.Windows.Controls.MenuItem>().Single(m=>m.Header?.ToString()=="ExtraLauncher");extra.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));dropdown.IsOpen=false;
                Control<TextBox>(card,"CodeBox").Text="123456789";Check(Control<Button>(card,"ConnectButton").IsEnabled,"Custom app explicit adaptive action disabled");
                Check(saved>=3,"Changes were not persisted through catalog save callback");
                string json=System.Text.Json.JsonSerializer.Serialize(preferences);var restored=System.Text.Json.JsonSerializer.Deserialize<Infrastructure.Preferences>(json)!;Check(restored.SoftwareEntries!.Count==2&&restored.SoftwareEntries.All(e=>e.Id!="sunlogin"),"Catalog reload restored deleted built-in");
                var add=Descendants<Button>(manager).Single(b=>b.Content?.ToString()=="添加");add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle();
                Check(add.ContextMenu!.Items.OfType<System.Windows.Controls.MenuItem>().Select(m=>m.Header?.ToString()).SequenceEqual(new[]{"自动添加","手动添加"}),"Add choices incorrect");add.ContextMenu.IsOpen=false;
                Capture(manager,"49-软件列表管理");Capture(card,"50-软件名称同步与窗口尺寸");
                Check(Control<TextBlock>(card,"WindowSizeText").Text.Contains("×"),"Full window dimensions missing");
                card.RestorePreviewVisibility(false);await Settle();Check(Control<TextBlock>(card,"WindowSizeText").Text.StartsWith(Math.Round(card.ActualWidth*DesktopWindows.DpiScale(card))+"×"),"Collapsed window dimensions stale");
                Console.WriteLine("SOFTWARE UI RESULT passed: row checkboxes; rename/main synchronization; delete confirmation and cancellation; launcher default name; custom software explicit adaptive action; preferences round-trip; add menu");exit=0;
            }catch(Exception ex){Console.WriteLine("SOFTWARE UI FAIL "+ex);}
            finally{manager.Close();card.ShutdownCard();app.Shutdown();}
        }));app.Run();return exit;
    }
    public static int RunTheme(string output)
    {
        _output=Path.GetFullPath(output);Directory.CreateDirectory(_output);int exit=1;
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            await using var rig=new Rig();
            try{
                ThemeManager.Apply("Light");rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();rig.Card.ShowOriginal();await Settle();
                var source=Control<System.Windows.Controls.Image>(rig.Card,"SourceImage").Source;var code=Control<TextBox>(rig.Card,"CodeBox").Text;
                using var menu=new System.Windows.Forms.ContextMenuStrip();var appearance=new System.Windows.Forms.ToolStripMenuItem("外观");menu.Items.Add(appearance);
                var prefs=new Infrastructure.Preferences();var choices=ThemeManager.CreateMenu(mode=>{prefs.Theme=mode;ThemeManager.Apply(mode);ThemeManager.ApplyMenu(menu);});appearance.DropDownItems.Add(choices);
                Check(choices.DropDownItems.Count==3,"Theme menu does not have three choices");
                ((System.Windows.Forms.ToolStripMenuItem)choices.DropDownItems[1]).PerformClick();await Settle();
                Check(ThemeManager.IsDark&&prefs.Theme=="Dark","Dark theme selection failed");
                Check(((System.Windows.Media.SolidColorBrush)Control<Border>(rig.Card,"ShellBackground").Background).Color==System.Windows.Media.Color.FromRgb(37,45,57),"Shell did not update dynamically");
                Check(Control<TextBox>(rig.Card,"CodeBox").Text==code&&ReferenceEquals(Control<System.Windows.Controls.Image>(rig.Card,"SourceImage").Source,source)&&rig.Card.PreviewExpanded,"Theme changed draft/image/preview");
                Capture(rig.Card,"45-深色主题");
                ThemeManager.ApplyMenu(menu);
                using(var standalone=new System.Windows.Forms.ContextMenuStrip()){
                    foreach(System.Windows.Forms.ToolStripMenuItem item in choices.DropDownItems)standalone.Items.Add(new System.Windows.Forms.ToolStripMenuItem(item.Text){Checked=item.Tag?.ToString()==ThemeManager.Current});
                    ThemeManager.ApplyMenu(standalone);standalone.Show(new System.Drawing.Point(100,100));await Settle();CaptureNativeMenu(standalone,"46-主题选项菜单");standalone.Close();
                }
                ((System.Windows.Forms.ToolStripMenuItem)choices.DropDownItems[0]).PerformClick();await Settle();Check(!ThemeManager.IsDark,"Light selection failed");Capture(rig.Card,"47-浅色主题");
                ((System.Windows.Forms.ToolStripMenuItem)choices.DropDownItems[2]).PerformClick();Check(ThemeManager.Current=="System"&&ThemeManager.IsDark==ThemeManager.SystemIsDark(),"System theme resolution failed");
                string saved=System.Text.Json.JsonSerializer.Serialize(prefs);Check(System.Text.Json.JsonSerializer.Deserialize<Infrastructure.Preferences>(saved)!.Theme=="System","Theme preference did not round trip");
                ThemeManager.Apply("invalid");Check(ThemeManager.Current=="Light","Unknown theme does not recover to light");
                string wallpaperPath=Path.Combine(_output,"wallpaper-test.png");
                using(var wallpaper=new SKBitmap(640,360)){
                    using(var canvas=new SKCanvas(wallpaper)){using var fill=new SKPaint{Shader=SKShader.CreateLinearGradient(new SKPoint(0,0),new SKPoint(640,360),[SKColors.MidnightBlue,SKColors.Teal,SKColors.Gold],null,SKShaderTileMode.Clamp)};canvas.DrawRect(0,0,640,360,fill);}
                    using var png=wallpaper.Encode(SKEncodedImageFormat.Png,100);File.WriteAllBytes(wallpaperPath,png.ToArray());
                }
                ThemeManager.Apply("Dark");rig.Card.LoadWallpaper(wallpaperPath);await Settle();
                Check(rig.Card.HasWallpaper&&Control<Border>(rig.Card,"ShellBackground").Background is System.Windows.Media.ImageBrush imageBrush&&imageBrush.Stretch==System.Windows.Media.Stretch.UniformToFill,"Wallpaper does not cover shell");
                Check(ReferenceEquals(Control<System.Windows.Controls.Image>(rig.Card,"SourceImage").Source,source),"Wallpaper replaced original customer image");
                var visual=Control<Grid>(rig.Card,"WindowVisual");
                var rendered=new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);rendered.Render(visual);var pixel=new byte[4];
                rendered.CopyPixels(new Int32Rect(20,100,1,1),pixel,4,0);Check(pixel[3]==255,"Wallpaper made main card transparent");
                var pinned=(FrameworkElement)rig.Card.PinnedPreviewWindow!.Content;
                var previewBitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)pinned.ActualWidth,(int)pinned.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);previewBitmap.Render(pinned);
                var previewArea=Control<Grid>(rig.Card,"PreviewArea");var corner=previewArea.TranslatePoint(new System.Windows.Point(2,2),pinned);
                previewBitmap.CopyPixels(new Int32Rect((int)corner.X,(int)corner.Y,1,1),pixel,4,0);Check(pixel[3]>=88&&pixel[3]<=92,"Wallpaper made preview blank space opaque");
                Capture(rig.Card,"48-本地壁纸与透明预览");
                // Embedded two-frame GIF only for playback validation; no network or user clipboard access.
                string gifPath=Path.Combine(_output,"wallpaper-test.gif");
                File.WriteAllBytes(gifPath,Convert.FromBase64String("R0lGODlhIAAgAIEAAAAAgAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQACAAAACwAAAAAIAAgAAAINQABCBxIsKDBgwgTKlzIsKHDhxAjSpxIsaLFixgzatzIsaPHjyBDihxJsqTJkyhTqlzJUmRAACH5BAEIAAEALAAAAAAgACAAgQCAgAAAAAAAAAAAAAg1AAEIHEiwoMGDCBMqXMiwocOHECNKnEixosWLGDNq3Mixo8ePIEOKHEmypMmTKFOqXMlSZEAAOw=="));
                using(var player=new WallpaperPlayer(gifPath)){
                    Check(player.FrameCount==2,"GIF decoded as static wallpaper");int frames=0;player.FrameChanged+=()=>frames++;player.SetPlaying(true);await Task.Delay(300);player.SetPlaying(false);Check(frames>0,"GIF did not animate");int stopped=frames;await Task.Delay(100);Check(frames==stopped,"Paused GIF keeps rendering");
                }
                rig.Card.LoadWallpaper(gifPath);await Task.Delay(150);Check(rig.Card.HasWallpaper,"Animated wallpaper could not attach to shell");
                string sakura=Path.Combine(AppContext.BaseDirectory,"Wallpapers","樱花飘落_1920x1080.gif");using(var petals=new WallpaperPlayer(sakura)){Check(petals.FrameCount==72,"Bundled sakura GIF missing animation frames");}
                rig.Card.LoadWallpaper(sakura);await Task.Delay(150);
                Check(((System.Windows.Media.SolidColorBrush)Control<TextBlock>(rig.Card,"PreviewHeading").Foreground).Color.R<100,"Light wallpaper label contrast was not adapted");
                Capture(rig.Card,"51-樱花飘落壁纸");
                string snow=Path.Combine(AppContext.BaseDirectory,"Wallpapers","雪峰飘雪_1920x1080.gif");using(var flakes=new WallpaperPlayer(snow)){Check(flakes.FrameCount==96,"Snow peak GIF frame count incorrect");}
                rig.Card.LoadWallpaper(snow);await Task.Delay(200);Capture(rig.Card,"52-雪峰飘雪壁纸");
                ThemeManager.Apply("Dark");await Settle();ThemeManager.Apply("Light");await Settle();
                Check(((System.Windows.Media.SolidColorBrush)Control<TextBox>(rig.Card,"CodeBox").Background).Color==System.Windows.Media.Color.FromRgb(241,245,252),"Wallpaper editor inputs stayed dark after Light");
                Check(((System.Windows.Media.SolidColorBrush)Control<Button>(rig.Card,"FullImageButton").Background).Color.R>220,"Pinned preview toolbar stayed dark after Light");
                Check(rig.Card.PinnedPreviewWindow?.Background==System.Windows.Media.Brushes.Transparent,"Theme overwrote transparent pin window background");
                Capture(rig.Card,"56-壁纸下深色切回浅色");
                rig.Card.LoadWallpaper(null);Check(!rig.Card.HasWallpaper&&Control<Border>(rig.Card,"ShellBackground").Background is System.Windows.Media.SolidColorBrush,"Clear wallpaper failed");

                Console.WriteLine("THEME UI RESULT passed: light/dark live change, three menu options, system resolution, preference serialization, unchanged draft and original bitmap; wallpaper cover, preview alpha, opaque main, GIF playback/pause, clear wallpaper");exit=0;
            }catch(Exception ex){Console.WriteLine("THEME UI FAIL "+ex.Message);}
            finally{ThemeManager.Apply("Light");rig.Card.ShutdownCard();app.Shutdown();}
        }));app.Run();return exit;
    }
    public static int RunHelp(string output)
    {
        _output=Path.GetFullPath(output);Directory.CreateDirectory(_output);int exit=1;
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            await using var rig=new Rig();var prefs=new Infrastructure.Preferences{AutoConnect=false,WatchClipboard=false,ClipboardConsent=true};
            var host=new DesktopHost(rig.Controller,prefs);
            try{
                host.ShowHelp();await Settle();
                var window=app.Windows.OfType<Window>().Single(w=>w.Title=="灰Desk · 软件说明");
                var scroll=(ScrollViewer)window.Content;var panel=(StackPanel)scroll.Content;
                Check(panel.Children.OfType<System.Windows.Controls.CheckBox>().Count()==1,"Help has more than the consent checkbox");
                string text=string.Join("\n",panel.Children.OfType<TextBlock>().Select(t=>t.Text));
                Check(text.Contains("关闭后不自动读取")&&text.Contains("不受监听开关影响")&&text.Contains("识别与连接"),"Software explanation is incomplete");
                Check(prefs.AutoConnect==false&&prefs.WatchClipboard==false,"Opening help changed options");
                var save=panel.Children.OfType<Button>().Single();
                Check(save.TransformToAncestor(scroll).TransformBounds(new Rect(save.RenderSize)).Bottom<=scroll.ActualHeight+1,"Help save button is clipped");
                Capture(window,"42-软件说明仅保留确认勾选");window.Close();exit=0;
                Console.WriteLine("HELP UI RESULT 1 passed, 0 failed; exactly one consent checkbox; auto-connect/listening preferences retained");
            }catch(Exception ex){Console.WriteLine("HELP UI FAIL "+ex.Message);}
            finally{await host.ShutdownAsync();host.Card.ShutdownCard();app.Shutdown();}
        }));app.Run();return exit;
    }
    private static async Task Test(string name,Func<Task> action)
    {
        var clock=Stopwatch.StartNew();
        try{await action();Results.Add(new{Name=name,Passed=true,ElapsedMs=clock.ElapsedMilliseconds});Console.WriteLine("PASS "+name);}
        catch(Exception ex){_failed++;Results.Add(new{Name=name,Passed=false,ElapsedMs=clock.ElapsedMilliseconds,Error=ex.Message});Console.WriteLine("FAIL "+name+": "+ex.Message);}
    }
    private static void Check(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);}
    private static T Control<T>(ConnectionCard card,string name) where T:FrameworkElement=>(T)card.FindName(name);
    private static void Click(ConnectionCard card,string name)=>Control<System.Windows.Controls.Primitives.ButtonBase>(card,name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static async Task Settle()=>await Task.Delay(100);
    private static void CheckToolbarAlignment(ConnectionCard card)
    {
        var buttons=new[]{"FullImageButton","FocusFieldsButton","PreviewCloseButton"}.Select(n=>Control<Button>(card,n)).ToArray();
        var bounds=buttons.Select(b=>new Rect(b.PointToScreen(new System.Windows.Point()),new System.Windows.Size(b.ActualWidth,b.ActualHeight))).ToArray();
        Check(bounds.All(b=>Math.Abs(b.Top-bounds[0].Top)<1&&Math.Abs(b.Height-bounds[0].Height)<1),"Preview buttons have different tops/heights");
        Check(ReferenceEquals(buttons[0].Parent,buttons[1].Parent)&&ReferenceEquals(buttons[1].Parent,buttons[2].Parent),"Preview actions do not share one layout row");
        Check(bounds[1].Left>=bounds[0].Right&&bounds[2].Left>=bounds[1].Right,"Preview actions overlap");
    }
    private static void RenderComposite(ConnectionCard card,System.Windows.Media.Imaging.RenderTargetBitmap target)
    {
        var root=Control<Grid>(card,"WindowVisual");var visual=new System.Windows.Media.DrawingVisual();
        using(var dc=visual.RenderOpen()){
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root),null,new Rect(root.RenderSize));
            if(card.PinnedPreviewWindow is {IsVisible:true,Content:FrameworkElement preview}){
                var origin=root.PointFromScreen(preview.PointToScreen(new System.Windows.Point()));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(preview),null,new Rect(origin,preview.RenderSize));
            }
        }
        target.Render(visual);
    }
    private static void Capture(Window window,string name)
    {
        window.UpdateLayout();var handle=new WindowInteropHelper(window).EnsureHandle();
        if(!GetWindowRect(handle,out var rect))throw new InvalidOperationException("No HWND bounds");
        using var bitmap=new System.Drawing.Bitmap(Math.Max(1,rect.R-rect.L),Math.Max(1,rect.B-rect.T));
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc=graphics.GetHdc();
            try{if(!PrintWindow(handle,dc,2))throw new InvalidOperationException("Native capture failed");}
            finally{graphics.ReleaseHdc(dc);}
        }
        if(window is ConnectionCard card&&card.PinnedPreviewWindow is {IsVisible:true} pin){
            var ph=new WindowInteropHelper(pin).EnsureHandle();GetWindowRect(ph,out var pr);
            using var overlay=new System.Drawing.Bitmap(Math.Max(1,pr.R-pr.L),Math.Max(1,pr.B-pr.T));
            using(var pg=System.Drawing.Graphics.FromImage(overlay)){var pd=pg.GetHdc();try{Check(PrintWindow(ph,pd,2),"Pinned preview native capture failed");}finally{pg.ReleaseHdc(pd);}}
            using var composed=System.Drawing.Graphics.FromImage(bitmap);composed.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;composed.DrawImageUnscaled(overlay,pr.L-rect.L,pr.T-rect.T);
        }
        bitmap.Save(Path.Combine(_output,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
        bool nonBlack=false;
        for(int y=0;y<bitmap.Height;y+=Math.Max(1,bitmap.Height/20))
        for(int x=0;x<bitmap.Width;x+=Math.Max(1,bitmap.Width/20))
        {var pixel=bitmap.GetPixel(x,y);if(pixel.R>5||pixel.G>5||pixel.B>5)nonBlack=true;}
        if(!nonBlack)
        {
            // Keep black native capture as failure evidence. Never label a WPF fallback "native screenshot".
            var root=(FrameworkElement)window.Content;
            var rendered=new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1,(int)root.ActualWidth),Math.Max(1,(int)root.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
            rendered.Render(root);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
            using var file=File.Create(Path.Combine(_output,name+"-WPF-only.png"));encoder.Save(file);
            Results.Add(new{Name=name+" native capture unavailable",Passed=true,NativeCaptureAvailable=false,Note="Black native capture retained; WPF-only image is visual layout evidence, not a native screenshot"});
        }
    }
    private static void CaptureWpfMenu(System.Windows.Controls.ContextMenu menu,string name)
    {
        var source=(System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(menu);
        Check(GetWindowRect(source.Handle,out var rect),"WPF menu bounds unavailable");
        using var bitmap=new System.Drawing.Bitmap(Math.Max(1,rect.R-rect.L),Math.Max(1,rect.B-rect.T));
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap)){
            var dc=graphics.GetHdc();try{Check(PrintWindow(source.Handle,dc,2),"WPF menu capture failed");}finally{graphics.ReleaseHdc(dc);}
        }
        bitmap.Save(Path.Combine(_output,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
    }
    private static void CaptureNativeMenu(System.Windows.Forms.ContextMenuStrip menu,string name)
    {
        var rect=DesktopWindows.NativeBounds(menu.Handle);
        using var bitmap=new System.Drawing.Bitmap((int)rect.Width,(int)rect.Height);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
        {
            var dc=graphics.GetHdc();try{Check(PrintWindow(menu.Handle,dc,2),"Menu native capture failed");}finally{graphics.ReleaseHdc(dc);}
        }
        bitmap.Save(Path.Combine(_output,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
    }
    private static async Task Scenarios()
    {
        var chatBox=new TextBox{Text="模拟客服聊天：这里继续输入，不应被助手抢走",AcceptsReturn=true,FontSize=18,Margin=new Thickness(20)};
        var chat=new Window{Title="受控模拟聊天（不会发送消息）",Content=chatBox,Width=610,Height=370,Left=40,Top=60,WindowStartupLocation=WindowStartupLocation.Manual};
        chat.Show();chat.Activate();chatBox.Focus();await Settle();
        var chatHandle=new WindowInteropHelper(chat).Handle;
        await Test("desktop is interactive",()=>{Check(DesktopWindows.GetForegroundWindow()==chatHandle,"No interactive desktop foreground; do not claim focus tests passed");return Task.CompletedTask;});
        await using(var rig=new Rig())
        {
            await Test("idle starts without an assistant window",()=>{
                Check(!rig.Card.IsVisible,"Assistant shown at startup");return Task.CompletedTask;
            });
            await Test("normal flow is passive, duplicate triggers do not reconnect, terminal hides and clears",async()=>{
                rig.DelayMs=500;var task=rig.Controller.RecognizeAsync(rig.ImageA);await Task.Delay(180);
                Check(rig.Controller.Stage==AssistantStage.Connecting,"Not connecting");
                Capture(rig.Card,"01-连接状态");
                Check(DesktopWindows.GetForegroundWindow()==chatHandle,"Foreground unavailable or passive card stole chat focus");
                Check((DesktopWindows.ExtendedStyle(rig.Card)&DesktopWindows.NoActivate)!=0,"Missing NOACTIVATE");
                await rig.Controller.RecognizeAsync(rig.ImageA);await task;await Settle();
                Check(rig.Calls.Count==1,"Repeated screenshot connected twice");
                Capture(rig.Card,"02-已发起");
                rig.Card.FinishIfCurrent();Check(!rig.Card.IsVisible,"Terminal not hidden");
                Check(rig.Controller.Image==null&&rig.Controller.Result==null&&Control<TextBox>(rig.Card,"PasswordBox").Text=="","Stale private data retained");
                await rig.Controller.RecognizeAsync(rig.ImageA);Check(rig.Calls.Count==1,"Finished screenshot retriggered");
                Check(rig.Card.ExplicitActivations==0,"Normal path activated UI");
            });
        }
        await using(var rig=new Rig())
        {
            rig.Manual=true;
            await Test("ambiguous result waits without activation; explicit edit accepts native keyboard input",async()=>{
                chat.Activate();chatBox.Focus();await Settle();
                await rig.Controller.RecognizeAsync(rig.ImageA);await Settle();
                Check(rig.Calls.Count==0,"Ambiguous result auto-connected");
                Capture(rig.Card,"03-待核对");
                rig.Card.EnterEditor();await Settle();
                Capture(rig.Card,"04-核对编辑");
                Click(rig.Card,"OriginalButton");await Settle();Capture(rig.Card,"04b-原图展开");
                Check(DesktopWindows.GetForegroundWindow()==chatHandle || DesktopWindows.GetForegroundWindow()==new WindowInteropHelper(rig.Card).Handle,"Foreground unavailable for native edit test");
                Check(DesktopWindows.GetForegroundWindow()==new WindowInteropHelper(rig.Card).Handle,"Explicit editing did not activate");
                var code=Control<TextBox>(rig.Card,"CodeBox");code.Focus();code.SelectAll();
                // Guard real input: only the foreground HWND owned by this synthetic card receives it.
                Check(DesktopWindows.GetForegroundWindow()==new WindowInteropHelper(rig.Card).Handle,"Input guard failed");
                System.Windows.Forms.SendKeys.SendWait("123456789");await Settle();
                Check(DesktopWindows.GetForegroundWindow()==new WindowInteropHelper(rig.Card).Handle,"Native Tab input guard failed");
                System.Windows.Forms.SendKeys.SendWait("{TAB}");await Settle();
                Check(ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement,Control<TextBox>(rig.Card,"PasswordBox")),"Tab did not move from code to password");
                Control<TextBox>(rig.Card,"CodeBox").Focus();
                Check(code.Text=="123456789","Native keyboard input failed");
                Capture(rig.Card,"04-核对编辑");
            });
            await Test("failure and password retry retain draft; manual retry is explicit",async()=>{
                rig.Next=ConnectResult.NeedPwd("模拟：对方需要密码，请核对后重试");
                Click(rig.Card,"ConnectButton");await Task.Delay(600);
                Check(rig.Controller.Stage==AssistantStage.NeedInput,"Need-password result lost");
                chat.Activate();chatBox.Focus();await Settle();
                rig.Card.EnterEditor();await Settle();
                var password=Control<TextBox>(rig.Card,"PasswordBox");password.Text="Ab7890";
                Capture(rig.Card,"05-失败补填");
                rig.Next=ConnectResult.Confirm("模拟已发起");
                Click(rig.Card,"ConnectButton");await Task.Delay(600);
                Check(rig.Calls.Count==2&&rig.Calls[1].Password=="Ab7890","Retry mixed password or did not run");
                rig.Card.FinishIfCurrent();
            });
        }
        await using(var rig=new Rig())
        {
            await Test("stop waits for this request cleanup and leaves other windows alone",async()=>{
                rig.DelayMs=3000;var task=rig.Controller.RecognizeAsync(rig.ImageA);await Task.Delay(130);
                Check(rig.Controller.Stage==AssistantStage.Connecting,"No in-flight request");
                Click(rig.Card,"StopButton");await task;await Task.Delay(100);
                Check(rig.Cancelled==1&&rig.CleanupCompleted,"Cancel did not finish cleanup");
                Check(rig.Controller.Stage==AssistantStage.Cancelled,"Stopped status missing");
                Check(chat.IsVisible,"Existing window closed");
                Capture(rig.Card,"06-已停止");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("A replaced by B cannot publish old progress or mix credentials",async()=>{
                rig.DelayMs=1000;var first=rig.Controller.RecognizeAsync(rig.ImageA);await Task.Delay(120);
                var next=rig.Controller.RecognizeAsync(rig.ImageB);await Task.WhenAll(first,next);
                Check(rig.Cancelled==1&&rig.Calls.Count==2,"Replacement did not serialize");
                Check(rig.Calls[0].Code=="123456789"&&rig.Calls[1].Code=="987654321","Customer codes mixed");
                Check(rig.Controller.Result?.DeviceCode=="987654321","Old OCR replaced B");
                Check(Control<TextBox>(rig.Card,"PasswordBox").Text=="Cd5678","Old password remained");
                rig.Card.FinishIfCurrent();
            });
        }
        await using(var rig=new Rig())
        {
            await Test("background copying does not interrupt editing; user edits survive status updates",async()=>{
                var preferences=new Infrastructure.Preferences{AutoConnect=false,WatchClipboard=true,ClipboardConsent=true};
                var host=new DesktopHost(rig.Controller,preferences);
                try{
                    await rig.Controller.RecognizeAsync(rig.ImageA);host.Card.EnterEditor();
                    var box=Control<TextBox>(host.Card,"PasswordBox");box.Text="My1234";
                    Check(host.AcceptBackgroundImage==!host.Card.IsKeyboardFocusWithin,"Clipboard editing focus boundary incorrect");
                    bool accepted=host.ProcessClipboardNotification(true);
                    Check(accepted==!host.Card.IsKeyboardFocusWithin,"Clipboard focused editor guard incorrect");
                    Check(host.PendingClipboard==host.Card.IsKeyboardFocusWithin,"Pending image indication incorrect");
                    Check(!host.ProcessClipboardNotification(false)&&!host.PendingClipboard,"Text copying opened UI or kept stale image indication");
                    rig.Controller.ShowMessage("模拟其他状态更新");await Settle();
                    Check(box.Text=="My1234","Progress overwrote manual draft");
                }finally{host.Card.ShutdownCard();}
            });
        }
        await using(var rig=new Rig())
        {
            await Test("blank/invalid image recovers to editable manual entry",async()=>{
                await rig.Controller.RecognizeAsync([1,2,3]);Check(rig.Controller.Stage==AssistantStage.Failed,"Invalid image did not fail");
                await rig.Controller.NewManualAsync();rig.Card.EnterEditor();await Settle();
                Check(Control<TextBox>(rig.Card,"CodeBox").IsEnabled&&Control<TextBox>(rig.Card,"PasswordBox").IsEnabled,"Manual inputs disabled");
                Check(Control<TextBox>(rig.Card,"CodeBox").Text=="","Old code retained");
                Capture(rig.Card,"07-手动填写");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("candidate switching keeps each code with its password and can switch back",async()=>{
                var document=new OcrDocument([
                    new("s","ToDesk",new Box(20,20,100,25),.99f,"simulation","synthetic"),
                    new("a","123456789",new Box(100,100,180,25),.99f,"simulation","synthetic"),
                    new("pa","Ab1234",new Box(400,100,110,25),.99f,"simulation","synthetic"),
                    new("b","987654321",new Box(100,350,180,25),.99f,"simulation","synthetic"),
                    new("pb","Cd5678",new Box(400,350,110,25),.99f,"simulation","synthetic")
                ],800,600,"simulation","synthetic");
                rig.ResultOverride=new RecognitionEngine().Analyze(document) with {Decision=ConnectionDecision.Manual};
                await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();await Settle();
                var options=Control<WrapPanel>(rig.Card,"CodeOptions");
                Check(options.Children.Count==2,"Code options missing");
                foreach(Button option in options.Children)
                {
                    option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    string code=Control<TextBox>(rig.Card,"CodeBox").Text;
                    Check(Control<TextBox>(rig.Card,"PasswordBox").Text==(code=="123456789"?"Ab1234":"Cd5678"),"Password belongs to other code");
                }
                ((Button)options.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Control<TextBox>(rig.Card,"CodeBox").Text=="123456789","Cannot restore first target");
                Check(Control<Button>(rig.Card,"CodeCandidateButton").IsVisible,"Candidate button is missing beside short field");
                Click(rig.Card,"CodeCandidateButton");await Settle();
                var candidateMenu=Control<Button>(rig.Card,"CodeCandidateButton").ContextMenu!;
                Check(candidateMenu.IsOpen&&candidateMenu.Items.Count==1&&((System.Windows.Controls.MenuItem)candidateMenu.Items[0]).Header.ToString()=="987654321","Candidate menu repeated current code or duplicate values");
                ((System.Windows.Controls.MenuItem)candidateMenu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));candidateMenu.IsOpen=false;
                Check(Control<TextBox>(rig.Card,"CodeBox").Text=="987654321"&&Control<TextBox>(rig.Card,"PasswordBox").Text=="Cd5678","Popup paired wrong code/password");
                // Same displayed number may exist in different engine/software evidence; never repeat it in the menu.
                options.Children.Add(new Button{Content="123456789"});
                options.Children.Add(new Button{Content="123456789"});
                Click(rig.Card,"CodeCandidateButton");candidateMenu=Control<Button>(rig.Card,"CodeCandidateButton").ContextMenu!;
                Check(candidateMenu.Items.Count==1&&((System.Windows.Controls.MenuItem)candidateMenu.Items[0]).Header.ToString()=="123456789","Identical alternatives appeared multiple times");
                candidateMenu.IsOpen=false;
                Control<TextBox>(rig.Card,"CodeBox").Text="123 456 789";
                Click(rig.Card,"CodeCandidateButton");candidateMenu=Control<Button>(rig.Card,"CodeCandidateButton").ContextMenu!;
                Check(candidateMenu.Items.Count==1&&((System.Windows.Controls.MenuItem)candidateMenu.Items[0]).Header.ToString()=="987654321","Formatted current code was not excluded");candidateMenu.IsOpen=false;
                options.Children.Clear();options.Children.Add(new Button{Content="123456789"});
                Control<TextBox>(rig.Card,"CodeBox").Text="123456789";
                Check(Control<Button>(rig.Card,"CodeCandidateButton").Visibility==Visibility.Collapsed,"No-alternative candidate entry remains visible");
                Capture(rig.Card,"11-候选配对");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("uncertain connection retains small card, fields and original image",async()=>{
                rig.Next=ConnectResult.Unknown();await rig.Controller.RecognizeAsync(rig.ImageA);
                await Task.Delay(1700);
                Check(rig.Controller.Stage==AssistantStage.Uncertain&&rig.Card.IsVisible,"Unconfirmed connection hidden");
                Check(Control<TextBox>(rig.Card,"CodeBox").Text=="123456789"&&Control<TextBox>(rig.Card,"PasswordBox").Text=="Ab1234","Uncertain fields cleared");
                Check(rig.Controller.Image!=null,"Uncertain original discarded");
                Click(rig.Card,"OriginalButton");await Settle();Capture(rig.Card,"12-尚未确认保留原图");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("native Enter submits edited fields to fake connector",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();await Settle();
                Control<TextBox>(rig.Card,"PasswordBox").Text="My1234";Control<TextBox>(rig.Card,"PasswordBox").Focus();
                Check(DesktopWindows.GetForegroundWindow()==new WindowInteropHelper(rig.Card).Handle,"Foreground unavailable; native Enter not sent");
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");await Task.Delay(650);
                Check(rig.Calls.Count==1&&rig.Calls[0].Password=="My1234","Enter did not submit edited password");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("opening a completed result to edit cancels terminal auto-hide",async()=>{
                await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();
                Control<TextBox>(rig.Card,"PasswordBox").Text="Edit1234";await Task.Delay(1750);
                Check(rig.Card.IsVisible&&Control<TextBox>(rig.Card,"PasswordBox").Text=="Edit1234","Terminal timer interrupted deliberate editing");
            });
        }
        await Test("normal lifecycle counters and timing are measured independently of foreground availability",async()=>{
            var samples=new List<object>();
            for(int i=0;i<5;i++)
            {
                await using var rig=new Rig();rig.DelayMs=120;
                var clock=Stopwatch.StartNew();await rig.Controller.RecognizeAsync(rig.ImageA);
                long launched=clock.ElapsedMilliseconds;
                await rig.Controller.RecognizeAsync(rig.ImageA);
                if(i==0)Capture(rig.Card,"02-已发起");
                while(rig.Card.IsVisible && clock.ElapsedMilliseconds<5000)await Task.Delay(25);
                Check(!rig.Card.IsVisible&&rig.Card.TerminalHides==1,"Automatic terminal hide did not run");
                Check(rig.Controller.Image==null&&rig.Controller.Result==null,"Old displayed result retained");
                Check(rig.Card.PassiveShows==1&&rig.Card.ExplicitActivations==0,"Normal path reopened or explicitly activated UI");
                Check(rig.Calls.Count==1,"Duplicate connection");
                samples.Add(new{Run=i+1,LaunchedMs=launched,HiddenMs=clock.ElapsedMilliseconds,
                    rig.Card.PassiveShows,rig.Card.ExplicitActivations,rig.Card.TerminalHides,ConnectorCalls=rig.Calls.Count});
            }
            Results.Add(new{Name="normal-lifecycle-measurements",Passed=true,Samples=samples,
                Note="Fake OCR 55ms and connector 120ms, five scripted runs. ExplicitActivations counts application calls, not OS foreground changes or human completion time."});
        });
        await using(var rig=new Rig())
        {
            await Test("narrow work area stacks original preview without hiding input fields",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);
                rig.Card.AnchorArea=new Rect(0,0,640,760);Click(rig.Card,"OriginalButton");await Settle();
                Check(Grid.GetRow(Control<Grid>(rig.Card,"ImagePanel"))==1,"Preview did not stack on narrow screen");
                Check(Control<TextBox>(rig.Card,"CodeBox").ActualWidth>=150,"Preview squeezed input fields");
                Capture(rig.Card,"13-窄屏原图对照");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("manual default is Sunlogin; original is left and fields remain short",async()=>{
                await rig.Controller.NewManualAsync();rig.Card.EnterEditor();Click(rig.Card,"OriginalButton");await Settle();
                Check(Control<System.Windows.Controls.ComboBox>(rig.Card,"AppBox").SelectedIndex==1,"Manual app is not Sunlogin");
                Check(Control<TextBox>(rig.Card,"CodeBox").ActualWidth==Control<TextBox>(rig.Card,"PasswordBox").ActualWidth,"Device and password widths differ");
                Check(rig.Card.FindName("EditButton")==null,"Redundant review button still exists");
                Check(Control<Button>(rig.Card,"ReadButton").Visibility==Visibility.Collapsed&&Control<Button>(rig.Card,"OpenButton").Content.ToString()=="打开图片","Duplicate reread control or old open-image label remains");
                Check(Control<TextBox>(rig.Card,"CodeBox").ActualWidth==180,"Device field too wide");
                Check(Grid.GetColumn(Control<Grid>(rig.Card,"ImagePanel"))==0&&Grid.GetColumn(Control<ScrollViewer>(rig.Card,"EditorScroll"))==1,"Image is not left of fields");
                Capture(rig.Card,"14-默认向日葵左图右填");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("clipboard finds and marks fields without filling or connecting; recognize fills the displayed image",async()=>{
                rig.Manual=true;var prefs=new Infrastructure.Preferences{AutoConnect=false,WatchClipboard=true,ClipboardConsent=true};
                uint sequence=100;byte[] image=rig.ImageA;
                var host=new DesktopHost(rig.Controller,prefs,(_,_)=>Task.FromResult<Infrastructure.ClipboardSnapshot?>(new(sequence,image,null)));
                try
                {
                    await host.ManualAsync();Click(host.Card,"OriginalButton");
                    await host.ReadClipboardAsync(true);
                    Check(rig.Controller.Result==null&&rig.Controller.Image!.SequenceEqual(rig.ImageA)&&rig.Controller.PreviewResult?.DeviceCode=="123456789"&&rig.AnalysisCalls==1,"Clipboard failed to prepare marked fields");
                    image=rig.ImageB;sequence++;await host.ReadClipboardAsync(true);await Settle();
                    Check(rig.Controller.Result==null&&Control<TextBox>(host.Card,"PasswordBox").Text==""&&rig.AnalysisCalls==2&&rig.Controller.PreviewResult?.DeviceCode=="987654321","Background copy filled fields or failed to prepare preview");
                    Check(Control<Grid>(host.Card,"ImagePanel").Visibility==Visibility.Visible,"New clipboard collapsed open preview");
                    Check(rig.Controller.Image!.SequenceEqual(rig.ImageB)&&rig.Calls.Count==0,"Preview did not switch or fake connector unexpectedly called");
                    Check(host.Card.AutomaticPresentations==0,"Clipboard preview raised window");
                    Check(host.Card.PreviewZoom==1&&Control<Grid>(host.Card,"ImageViewport").Clip==null,"Copied image did not default to full view");
                    Click(host.Card,"FocusFieldsButton");await Settle();
                    Check(host.Card.PreviewZoom>1&&Control<Canvas>(host.Card,"Overlay").Children.Count==2,"Copy did not enable field localization before fill");
                    Capture(host.Card,"38-复制后框选但未填入");
                    // Clipboard A differs from currently shown B: fill must use the marked preview B.
                    image=rig.ImageA;sequence++;
                    Click(host.Card,"RecognizeButton");await Task.Delay(350);
                    Check(rig.Controller.Result?.DeviceCode=="987654321"&&rig.Calls.Count==0&&rig.AnalysisCalls==2,"Fill did not reuse current marked preview without connecting");
                    Check(Control<TextBox>(host.Card,"CodeBox").Text=="987654321"&&Control<TextBox>(host.Card,"PasswordBox").Text=="Cd5678","Marked code/password did not fill inputs");
                    Control<TextBox>(host.Card,"CodeBox").Clear();Click(host.Card,"RecognizeButton");await Task.Delay(350);
                    Check(Control<TextBox>(host.Card,"CodeBox").Text=="987654321"&&rig.AnalysisCalls==2&&rig.Calls.Count==0,"Repeat fill failed or reran OCR");
                    rig.Controller.AutoConnect=true;rig.Manual=false;
                    // Use a distinct clear image so the fake OCR returns an automatic decision.
                    image=rig.ImageA;sequence++;await host.ReadClipboardAsync(true);
                    Check(rig.Calls.Count==0&&rig.AnalysisCalls==3&&Control<TextBox>(host.Card,"CodeBox").Text=="","New copy filled or connected instead of only preparing marks");
                    await host.ReadClipboardAsync(false);
                    Check(rig.Calls.Count==1&&rig.Calls[0].Code=="123456789","Explicit ball-equivalent read did not recognize and connect");
                    Capture(host.Card,"15-复制新图自动切换");
                }
                finally{await host.ShutdownAsync();host.Card.ShutdownCard();}
            });
        }
        await using(var rig=new Rig())
        {
            await Test("new image and explicit entry get a brief front pulse; progress never pins or repeats it",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);await Settle();
                Check((DesktopWindows.ExtendedStyle(rig.Card)&8)!=0,"New image was not temporarily brought above topmost windows");
                Check((DesktopWindows.ExtendedStyle(rig.Card)&DesktopWindows.NoActivate)!=0&&rig.Card.ExplicitActivations==0,"Automatic front pulse activated keyboard editing");
                Check(rig.Card.AutomaticPresentations==1&&rig.Card.ExplicitRaises==0,"New image was not presented once");
                rig.Controller.ShowMessage("模拟OCR进度");Check(rig.Card.AutomaticPresentations==1,"Progress repeated front presentation");
                await Task.Delay(950);Check(!rig.Card.Topmost&&(DesktopWindows.ExtendedStyle(rig.Card)&8)==0,"Temporary topmost was not released");
                rig.Card.BringForwardOnce();Check(rig.Card.ExplicitRaises==1,"Explicit entry did not raise once");
                Check((DesktopWindows.ExtendedStyle(rig.Card)&8)!=0,"Explicit click did not bring card above topmost windows");
                await Task.Delay(950);
                rig.Controller.ShowMessage("模拟后台状态更新");rig.Card.ApplyState();
                Check(rig.Card.ExplicitRaises==1&&!rig.Card.Topmost&&(DesktopWindows.ExtendedStyle(rig.Card)&8)==0,"Background update re-raised or pinned card");
                await rig.Controller.RecognizeAsync(rig.ImageB);
                Check(rig.Card.AutomaticPresentations==2,"Second image did not get its own single presentation");
                Capture(rig.Card,"17-卡片取消持续置顶");
                rig.Card.HideToTray();Check((DesktopWindows.ExtendedStyle(rig.Card)&8)==0,"Hiding did not clear temporary topmost state");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("preview defaults to full image with marks; manual locate, click, wheel and pan work",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);
                Click(rig.Card,"OriginalButton");await Settle();
                Check(rig.Card.PreviewZoom==1&&Control<Grid>(rig.Card,"ImageViewport").Clip==null,"Default preview unexpectedly localized instead of showing full image");
                Check((DesktopWindows.ExtendedStyle(rig.Card)&0x80000)!=0,"Window is not layered; transparency would only fade onto an opaque surface");
                var visual=Control<Grid>(rig.Card,"WindowVisual");
                var rendered=new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);RenderComposite(rig.Card,rendered);
                var pixels=new byte[4];rendered.CopyPixels(new Int32Rect(100,100,1,1),pixels,4,0);Check(pixels[3]>=88&&pixels[3]<=92,"Blank preview area does not have 35% alpha");
                var codeMark=(System.Windows.Shapes.Rectangle)Control<Canvas>(rig.Card,"Overlay").Children[0];
                var local=new System.Windows.Point(Canvas.GetLeft(codeMark)+codeMark.Width/2,Canvas.GetTop(codeMark)+codeMark.Height/2);
                var sample=visual.PointFromScreen(Control<Canvas>(rig.Card,"Overlay").PointToScreen(local));
                rendered.CopyPixels(new Int32Rect((int)sample.X,(int)sample.Y,1,1),pixels,4,0);Check(pixels[3]==255,"Actual image and marked number unexpectedly translucent");
                var toolbarBounds=new Rect(visual.PointFromScreen(Control<DockPanel>(rig.Card,"ImageToolbar").PointToScreen(new System.Windows.Point())),Control<DockPanel>(rig.Card,"ImageToolbar").RenderSize);
                var toolbarSample=new System.Windows.Point(toolbarBounds.X+toolbarBounds.Width/2,toolbarBounds.Y+toolbarBounds.Height/2);
                rendered.CopyPixels(new Int32Rect((int)toolbarSample.X,(int)toolbarSample.Y,1,1),pixels,4,0);Check(pixels[3]==255,$"Image toolbar alpha={pixels[3]} bounds={toolbarBounds}");
                rendered.CopyPixels(new Int32Rect(700,100,1,1),pixels,4,0);Check(pixels[3]==255,"Right editor unexpectedly translucent");
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rendered));
                using(var file=File.Create(Path.Combine(_output,"26-透明度渲染检查-WPF.png")))encoder.Save(file);
                Capture(rig.Card,"18-默认全图加框");
                var preview=Control<Grid>(rig.Card,"PreviewArea");
                var center=new System.Windows.Point(preview.ActualWidth/2,preview.ActualHeight/2);
                rig.Card.ResetPreview();Check(rig.Card.PreviewZoom==1&&rig.Card.PreviewPan.Length==0,"Full image reset failed");
                var down=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonDownEvent};
                var up=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent};
                preview.RaiseEvent(down);preview.RaiseEvent(up);
                Check(rig.Card.PreviewZoom==1,"Single click applied before double-click discrimination");
                await Task.Delay(System.Windows.Forms.SystemInformation.DoubleClickTime+100);
                Check(rig.Card.PreviewZoom>1,"Single click no longer enlarges image");
                rig.Card.ZoomPreview(3,center);rig.Card.PanPreview(new Vector(30,-20));
                var doubleDown=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonDownEvent};
                typeof(System.Windows.Input.MouseButtonEventArgs).GetProperty("ClickCount")!.SetValue(doubleDown,2);
                preview.RaiseEvent(doubleDown);
                Check(rig.Card.PreviewZoom==1&&rig.Card.PreviewPan.Length==0&&Control<Grid>(rig.Card,"ImageViewport").Clip==null,"Double click did not restore complete image");
                rig.Card.ResetPreview();
                preview.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=UIElement.MouseWheelEvent});
                Check(rig.Card.PreviewZoom>1,"Wheel handler did not enlarge image");
                rig.Card.ZoomPreview(3,center);var before=rig.Card.PreviewPan;rig.Card.PanPreview(new Vector(30,-20));
                Check((rig.Card.PreviewPan-before-new Vector(30,-20)).Length<.1,"Image panning did not move the viewport");
                var viewport=Control<Grid>(rig.Card,"ImageViewport");
                Check(viewport.Children.Contains(Control<System.Windows.Controls.Image>(rig.Card,"SourceImage"))&&viewport.Children.Contains(Control<Canvas>(rig.Card,"Overlay")),"Image and OCR boxes use different transform containers");
                rig.Card.FocusFields();await Settle();Check(Control<Button>(rig.Card,"FocusFieldsButton").Background is System.Windows.Media.SolidColorBrush selectedRegion&&selectedRegion.Color==System.Windows.Media.Color.FromRgb(234,241,255),"Located mode not selected");Capture(rig.Card,"19-图片缩放与标记");
                rig.Card.ResetPreview();await Settle();Check(Control<Button>(rig.Card,"FullImageButton").Background is System.Windows.Media.SolidColorBrush selectedFull&&selectedFull.Color==System.Windows.Media.Color.FromRgb(234,241,255),"Full image mode not selected");Capture(rig.Card,"20-切回完整原图");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("ball entry opens beside ball; tray entry opens bottom-right; dragging survives status and resize",async()=>{
                var ball=new FloatingBall();
                try
                {
                    var area=DesktopWindows.WorkAreaAtPointer();
                    DesktopWindows.Place(ball,area,null,new System.Windows.Point(area.Left+80,area.Top+180));ball.Show();await Settle();
                    rig.Card.SetEntryPlacement(ball);rig.Card.EnterEditor();await Settle();
                    var bb=DesktopWindows.NativeBounds(new WindowInteropHelper(ball).Handle);
                    var cb=DesktopWindows.NativeBounds(new WindowInteropHelper(rig.Card).Handle);
                    Check(cb.Left>=bb.Right&&cb.Left-bb.Right<30&&Math.Abs(cb.Top-bb.Top)<3,"Card is not beside ball");
                    Capture(rig.Card,"21-悬浮球入口定位");
                    rig.Card.HideToTray();rig.Card.SetEntryPlacement(null);rig.Card.EnterEditor();await Settle();
                    area=rig.Card.AnchorArea;cb=DesktopWindows.NativeBounds(new WindowInteropHelper(rig.Card).Handle);
                    Check(Math.Abs(area.Right-cb.Right-16*DesktopWindows.DpiScale(rig.Card))<3&&Math.Abs(area.Bottom-cb.Bottom-16*DesktopWindows.DpiScale(rig.Card))<3,"Tray card is not bottom-right");
                    var desired=new System.Windows.Point(area.Right-400,area.Top+100);rig.Card.MoveCardTo(desired);await Settle();
                    var editor=Control<ScrollViewer>(rig.Card,"EditorScroll");var origin=editor.PointToScreen(new System.Windows.Point());
                    rig.Controller.ShowMessage("模拟进度刷新");Click(rig.Card,"OriginalButton");await Settle();
                    Check((editor.PointToScreen(new System.Windows.Point())-origin).Length<3,"Editor moved when original expanded");
                    Check(Control<Grid>(rig.Card,"WindowVisual").OpacityMask!=null&&Control<Border>(rig.Card,"EditorBackground").Visibility==Visibility.Visible,"Left preview is not translucent with an opaque editor");
                    Capture(rig.Card,"24-左侧半透明原图");
                    Click(rig.Card,"OriginalButton");await Settle();
                    Check((editor.PointToScreen(new System.Windows.Point())-origin).Length<3,"Editor moved when original collapsed");
                    Check(Control<TextBox>(rig.Card,"CodeBox").ActualWidth==180,"Collapsing resized input fields");
                    Click(rig.Card,"OriginalButton");await Settle();
                    var beforeEntry=DesktopWindows.NativeBounds(new WindowInteropHelper(rig.Card).Handle);
                    rig.Card.SetEntryPlacement(ball);rig.Card.EnterEditor();await Settle();
                    Check(DesktopWindows.NativeBounds(new WindowInteropHelper(rig.Card).Handle).TopLeft==beforeEntry.TopLeft&&Control<Grid>(rig.Card,"ImagePanel").IsVisible,"Visible entry moved window or collapsed original");
                    rig.Card.HideToTray();rig.Card.SetEntryPlacement(ball);rig.Card.EnterEditor();await Settle();
                    Check(Control<Grid>(rig.Card,"ImagePanel").Visibility==Visibility.Visible,"Hidden entry lost chosen expanded state");
                    Capture(rig.Card,"22-拖动后保持位置");
                }
                finally{ball.Close();}
            });
        }
        await using(var rig=new Rig())
        {
            await Test("continuous dragging emits one native move per step without corner hops or resizing",async()=>{
                await rig.Controller.NewManualAsync();rig.Card.EnterEditor();await Settle();
                var area=rig.Card.AnchorArea;var start=new System.Windows.Point(area.Left+100,area.Top+100);
                rig.Card.MoveCardTo(start);await Settle();
                var handle=new WindowInteropHelper(rig.Card).Handle;
                var initial=DesktopWindows.NativeBounds(handle);int events=0;
                WinEventCallback callback=(_,_,window,objectId,childId,_,_)=>{if(window==handle&&objectId==0&&childId==0)events++;};
                var hook=SetWinEventHook(0x800B,0x800B,IntPtr.Zero,callback,(uint)Environment.ProcessId,0,0);
                Check(hook!=IntPtr.Zero,"Native window movement event hook unavailable");
                const int steps=20;
                try
                {
                    for(int i=1;i<=steps;i++)
                    {
                        var point=start+new Vector(i*4,i*2);rig.Card.MoveCardTo(point);await Task.Delay(20);
                        var bounds=DesktopWindows.NativeBounds(handle);
                        Check(Math.Abs(bounds.Left-point.X)<2&&Math.Abs(bounds.Top-point.Y)<2,"Drag position differs from cursor path");
                        Check(bounds.Size==initial.Size,"Drag resized window");
                    }
                    await Settle();Check(events>=steps&&events<=steps+2,$"Expected one move per step; observed {events} events for {steps} steps");
                    Results.Add(new{Name="drag-native-move-count",Passed=true,Steps=steps,NativeLocationEvents=events,ExpectedPerStep=1});
                    Capture(rig.Card,"23-连续拖动修正");
                }
                finally{UnhookWinEvent(hook);GC.KeepAlive(callback);}
            });
        }
        await using(var rig=new Rig())
        {
            await Test("connecting releases front pulse before the client is controlled",async()=>{
                rig.DelayMs=1200;var task=rig.Controller.RecognizeAsync(rig.ImageA);
                for(int i=0;i<100&&rig.Controller.Stage!=AssistantStage.Connecting;i++)await Task.Delay(10);
                Check(rig.Controller.Stage==AssistantStage.Connecting,"Connecting stage not reached");
                Check((DesktopWindows.ExtendedStyle(rig.Card)&8)==0,"Temporary topmost could obstruct remote client controls");
                await task;
            });
        }
        await using(var rig=new Rig())
        {
            await Test("compact manual entry has adjacent connection controls and preserves failed retry",async()=>{
                await rig.Controller.NewManualAsync();rig.Card.EnterEditor();await Settle();
                var button=Control<Button>(rig.Card,"ConnectButton");var code=Control<TextBox>(rig.Card,"CodeBox");var password=Control<TextBox>(rig.Card,"PasswordBox");
                var recognizeButton=Control<Button>(rig.Card,"RecognizeButton");
                Check(Math.Abs(button.ActualWidth-recognizeButton.ActualWidth)<.5&&button.ActualHeight==recognizeButton.ActualHeight,"Equal-priority recognize/connect actions have unequal sizes");
                Check(!button.IsEnabled&&button.Content.ToString()=="识别并连接 ↵","Empty code enabled a connection or wrong initial label");
                code.Text="123456789";password.Text="Ab1234";
                Check(button.IsEnabled,"Valid manual code did not enable connection");
                Check(button.PointToScreen(new System.Windows.Point()).Y>password.PointToScreen(new System.Windows.Point()).Y&&code.ActualWidth==password.ActualWidth,"Fields are not aligned above connection button");
                Click(rig.Card,"SoftwarePickerButton");
                var softwareMenu=Control<Button>(rig.Card,"SoftwarePickerButton").ContextMenu!;
                Check(softwareMenu.Items.Count==2,"Software list missing supported connectors");
                ((System.Windows.Controls.MenuItem)softwareMenu.Items[1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));softwareMenu.IsOpen=false;
                Check(Control<System.Windows.Controls.ComboBox>(rig.Card,"AppBox").SelectedIndex==2&&Control<TextBlock>(rig.Card,"SoftwareName").Text=="ToDesk","Software menu did not select connector");
                Control<System.Windows.Controls.ComboBox>(rig.Card,"AppBox").SelectedIndex=1;
                rig.Next=ConnectResult.Fail("模拟连接失败");Click(rig.Card,"ConnectButton");await Task.Delay(600);
                Check(button.IsEnabled&&button.Content.ToString()=="重新连接 ↵"&&code.Text=="123456789","Failure lost fields or retry action");
                password.Text="Cd5678";Click(rig.Card,"ConnectButton");await Task.Delay(600);
                Check(rig.Calls.Count==2&&rig.Calls[1].Password=="Cd5678","Retry did not use edited password");
                Capture(rig.Card,"25-紧凑核对框与重连");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("recognition records history without successful connection; history only refills",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();
                var recent=Control<System.Windows.Controls.Panel>(rig.Card,"RecentOptions");Check(recent.Children.Count==1&&rig.Calls.Count==0,"Recognized target not recorded before connection");
                Click(rig.Card,"ConnectButton");await Task.Delay(600);Check(recent.Children.Count==1,"Confirmed target not recorded");
                var row=(Grid)recent.Children[0];((Button)row.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                row=(Grid)recent.Children[0];Check(((Button)row.Children[0]).Content.ToString()=="★","Favorite did not toggle");
                ((Button)row.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Settle();
                Check(Control<TextBox>(rig.Card,"CodeBox").Text=="123456789"&&Control<TextBox>(rig.Card,"PasswordBox").Text==""&&rig.Controller.Image==null,"History refill retained another customer's image or password");
                Check(rig.Calls.Count==1,"History refill automatically connected");
                Capture(rig.Card,"28-新版主界面与最近连接");
                row=(Grid)recent.Children[0];((Button)row.Children[2]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(recent.Children.Count==0,"Delete did not remove recent target");
            });
        }
        await Test("vertical opacity picker supports typed bounds, unit arrows and accelerating hold",async()=>{
            using var picker=new OpacityPicker();using var menu=new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(new System.Windows.Forms.ToolStripControlHost(picker));menu.Show(100,150);await Settle();
            var number=picker.Controls.OfType<System.Windows.Forms.TextBox>().Single();
            var slider=picker.Controls.OfType<System.Windows.Forms.TrackBar>().Single();
            number.Text="57";Check(picker.Value==57&&slider.Value==57,"Typed percentage not synchronized");
            slider.Value=63;Check(picker.Value==63&&number.Text=="63","Slider did not update input");
            picker.SetValue(0);Check(picker.Value==10,"Minimum is not 10");picker.SetValue(101);Check(picker.Value==100,"Maximum is not 100");
            picker.SetValue(30);picker.BeginStep(1);picker.EndStep();Check(picker.Value==31,"Single arrow click is not one unit");
            picker.BeginStep(1);await Task.Delay(750);int early=picker.Value;await Task.Delay(750);int later=picker.Value;picker.EndStep();
            Check(later-early>early-32,"Holding arrow did not accelerate");
            int stopped=picker.Value;await Task.Delay(200);Check(picker.Value==stopped,"Repeat continued after release");
            CaptureNativeMenu(menu,"27-竖向透明度调节");menu.Close();
        });
        await Test("nested opacity menu stays on the originating monitor at both lower edges",async()=>{
            foreach(var screen in System.Windows.Forms.Screen.AllScreens)
            foreach(bool right in new[]{false,true})
            {
                var ball=new FloatingBall();using var root=new System.Windows.Forms.ContextMenuStrip();
                var appearance=new System.Windows.Forms.ToolStripMenuItem("外观");var opacity=new System.Windows.Forms.ToolStripMenuItem("透明度");
                using var picker=new OpacityPicker();opacity.DropDownItems.Add(new System.Windows.Forms.ToolStripControlHost(picker));appearance.DropDownItems.Add(opacity);
                root.Items.Add("打开主界面");root.Items.Add("读取截图");root.Items.Add(appearance);root.Items.Add("更多");root.Items.Add("退出");
                DesktopWindows.KeepSubmenusOnMonitor(root);
                try{
                    var a=screen.WorkingArea;var area=new Rect(a.X,a.Y,a.Width,a.Height);
                    DesktopWindows.Place(ball,area,null,new System.Windows.Point(right?area.Right-70:area.Left+5,area.Bottom-70));ball.Show();await Settle();
                    DesktopWindows.ShowBallMenu(root,ball,.85);appearance.ShowDropDown();await Settle();opacity.ShowDropDown();await Settle();
                    Check(area.Contains(DesktopWindows.NativeBounds(appearance.DropDown.Handle)),"Appearance submenu left originating monitor");
                    Check(area.Contains(DesktopWindows.NativeBounds(opacity.DropDown.Handle)),"Opacity controls clipped at screen boundary");
                    if(right)CaptureNativeMenu(opacity.DropDown as System.Windows.Forms.ContextMenuStrip ?? root,"29-边缘菜单位置-"+screen.DeviceName.Replace("\\"," "));
                }finally{root.Close();ball.Close();}
            }
        });
        await using(var rig=new Rig())
        {
            await Test("startup preview keeps app information on main card; clear removes image while retaining panel and fields",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();rig.Card.ShowOriginal();await Settle();
                var editor=Control<ScrollViewer>(rig.Card,"EditorScroll");var header=Control<Grid>(rig.Card,"HeaderBar");
                Check(editor.IsAncestorOf(header)&&rig.Card.FindName("RecognitionDetails")==null,"App heading or recognition details remained on image side");
                Check(header.PointToScreen(new System.Windows.Point()).X>=editor.PointToScreen(new System.Windows.Point()).X,"Title outside main card");
                var overlay=Control<Canvas>(rig.Card,"Overlay");Check(overlay.Children.Count==2,"Both selected fields were not marked");
                Check(overlay.Children.OfType<System.Windows.Shapes.Rectangle>().All(m=>m.StrokeThickness*rig.Card.PreviewZoom<2.1),"Mark outline grows over characters during zoom");
                var close=Control<Button>(rig.Card,"PreviewCloseButton");Check(close.IsVisible&&close.Opacity==1,"Image close is hidden or translucent");
                CheckToolbarAlignment(rig.Card);
                rig.Controller.ShowMessage("密码包含易混或低置信度字符，请核对原图");await Settle();
                Check(Control<TextBlock>(rig.Card,"RiskText").Visibility==Visibility.Visible,"Specific reason is hidden in status card");
                Capture(rig.Card,"30-信息归主界面与放大标记");
                Click(rig.Card,"PreviewCloseButton");await Settle();
                Check(Control<Grid>(rig.Card,"ImagePanel").Visibility==Visibility.Visible&&rig.Card.IsVisible&&Control<TextBox>(rig.Card,"CodeBox").Text=="123456789"&&Control<System.Windows.Controls.Image>(rig.Card,"SourceImage").Source==null,"Clear preview collapsed panel or cleared filled code");
                Capture(rig.Card,"31-仅收起图片保留主界面");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("title controls minimize to taskbar, restore and maximize without losing draft",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();rig.Card.ShowOriginal();await Settle();
                Click(rig.Card,"MinimizeButton");await Settle();
                Check(rig.Card.WindowState==WindowState.Minimized&&rig.Card.ShowInTaskbar&&rig.Card.IsVisible,"Minimize hid to tray instead of taskbar");
                rig.Card.EnterEditor();await Settle();Check(rig.Card.WindowState==WindowState.Normal,"Explicit open did not restore minimized window");
                Click(rig.Card,"MaximizeButton");await Settle();Check(rig.Card.WindowState==WindowState.Maximized,"Maximize failed");
                Check(Control<TextBox>(rig.Card,"CodeBox").Text=="123456789","Maximize lost input");
                CheckToolbarAlignment(rig.Card);Capture(rig.Card,"32-最大化与任务栏");
                Click(rig.Card,"MaximizeButton");await Settle();Check(rig.Card.WindowState==WindowState.Normal&&rig.Card.Width==768,"Restore did not recover card layout");
                Capture(rig.Card,"33-标题栏三按钮");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("text copying previews and marks without filling or connecting; explicit recognition fills only",async()=>{
                var prefs=new Infrastructure.Preferences{AutoConnect=false,WatchClipboard=true,ClipboardConsent=true};
                uint seq=400;string text="向日葵 设备码：123456789；验证码：Ab1234";
                var host=new DesktopHost(rig.Controller,prefs,(_,_)=>Task.FromResult<Infrastructure.ClipboardSnapshot?>(new(seq,null,text)));
                try{
                    await host.ReadClipboardAsync(true);Check(rig.Controller.Result==null&&rig.Controller.PreviewResult?.DeviceCode=="123456789"&&rig.Controller.PreviewText==text&&Control<TextBox>(host.Card,"CodeBox").Text==""&&rig.Calls.Count==0&&rig.AnalysisCalls==0,"Background text filled, connected, or failed to preview");
                    await host.ReadClipboardAsync(false,true);Check(rig.Controller.Result?.DeviceCode=="123456789"&&rig.Calls.Count==0,"Text recognition-only connected or did not fill fields");
                    await host.ReadClipboardAsync(false);Check(rig.Calls.Count==1,"Explicit text connect after recognition-only was blocked as duplicate");
                    rig.DelayMs=1000;var work=rig.Controller.RecognizeAsync(rig.ImageB);await Task.Delay(100);
                    text="普通聊天文字";seq++;await host.ReadClipboardAsync(true);await work;
                    Check(rig.Cancelled==0,"Background clipboard cancelled an active explicit operation");
                }finally{await host.ShutdownAsync();host.Card.ShutdownCard();}
            });
        }
        await using(var rig=new Rig())
        {
            await Test("connection history ranks closest digit positions first and short inputs stay aligned",async()=>{
                rig.DelayMs=10;
                foreach(var target in new[]{"123456780","123450780","987654321"})
                {
                    var text="向日葵 识别码："+target;var parsed=RemoteTextParser.Parse(text)!;await rig.Controller.AcceptTextAsync(text,parsed,false);
                }
                Control<TextBox>(rig.Card,"CodeBox").Text="123456789";rig.Card.EnterEditor();await Settle();
                var list=Control<System.Windows.Controls.Panel>(rig.Card,"RecentOptions");
                string[] ordered=list.Children.Cast<Grid>().Select(row=>((Button)row.Children[1]).Content.ToString()!).ToArray();
                Check(ordered.SequenceEqual(new[]{"123456780","123450780","987654321"}),"History is not sorted by most matching digit positions");
                Check(Control<TextBlock>(rig.Card,"HistoryHeading").Text=="连接历史","History label not updated");
                var code=Control<TextBox>(rig.Card,"CodeBox");var password=Control<TextBox>(rig.Card,"PasswordBox");
                Check(Control<Button>(rig.Card,"ConnectButton").PointToScreen(new System.Windows.Point()).X+Control<Button>(rig.Card,"ConnectButton").ActualWidth<=Control<ScrollViewer>(rig.Card,"EditorScroll").PointToScreen(new System.Windows.Point()).X+Control<ScrollViewer>(rig.Card,"EditorScroll").ActualWidth+1,"History scrollbar clips action row");
                Check(code.ActualWidth==180&&password.ActualWidth==180&&Math.Abs(code.PointToScreen(new System.Windows.Point()).X-password.PointToScreen(new System.Windows.Point()).X)<1,"Short fields not aligned");
                Capture(rig.Card,"34-灰色操作与连接历史排序");
                foreach(string target in new[]{"111111111","222222222","333333333","444444444","555555555"}){var text="向日葵 识别码："+target;await rig.Controller.AcceptTextAsync(text,RemoteTextParser.Parse(text)!,false);}
                await Settle();var grid=Control<System.Windows.Controls.Primitives.UniformGrid>(rig.Card,"RecentOptions");
                Check(grid.Children.Count==6&&grid.Columns==3&&grid.Rows==2,"History exceeded six entries or two rows");
                Capture(rig.Card,"58-连接历史两行六条");
            });
        }
        await using(var rig=new Rig())
        {
            await Test("vertical resizing keeps width and primary actions available at minimum height",async()=>{
                await rig.Controller.NewManualAsync();rig.Card.EnterEditor();await Settle();
                var hwnd=new WindowInteropHelper(rig.Card).Handle;var start=DesktopWindows.NativeBounds(hwnd);
                IntPtr Point(double x,double y)=>new(unchecked((int)(((uint)(ushort)(short)y<<16)|(ushort)(short)x)));
                Check(SendMessage(hwnd,0x84,IntPtr.Zero,Point(start.Left+50,start.Top+2)).ToInt32()==12,"Top border does not resize");
                Check(SendMessage(hwnd,0x84,IntPtr.Zero,Point(start.Left+50,start.Bottom-2)).ToInt32()==15,"Bottom border does not resize");
                Check(SendMessage(hwnd,0x84,IntPtr.Zero,Point(start.Right-2,start.Top+100)).ToInt32()==11,"Side border does not resize width");
                SendMessage(hwnd,0x231,IntPtr.Zero,IntPtr.Zero);rig.Card.Height=rig.Card.MinHeight;await Settle();SendMessage(hwnd,0x232,IntPtr.Zero,IntPtr.Zero);
                rig.Controller.ShowMessage("模拟刷新");await Settle();
                Check(rig.Card.ActualHeight==rig.Card.MinHeight&&rig.Card.ActualWidth==start.Width,"Status reset resized height or changed fixed width");
                var open=Control<Button>(rig.Card,"OpenButton");var scroll=Control<ScrollViewer>(rig.Card,"EditorScroll");
                Check(open.PointToScreen(new System.Windows.Point()).Y+open.ActualHeight<=scroll.PointToScreen(new System.Windows.Point()).Y+scroll.ActualHeight+1,"Minimum height clips primary image controls");
                Capture(rig.Card,"35-最小高度核心操作");
                SendMessage(hwnd,0x231,IntPtr.Zero,IntPtr.Zero);rig.Card.Width=400;rig.Card.Height=620;await Settle();SendMessage(hwnd,0x232,IntPtr.Zero,IntPtr.Zero);
                Click(rig.Card,"OriginalButton");await Settle();
                Check(rig.Card.ActualHeight==620,"Preview opening did not match current main-card height");
                var editor=Control<ScrollViewer>(rig.Card,"EditorScroll");double oldEditorWidth=editor.ActualWidth;var before=DesktopWindows.NativeBounds(hwnd);
                SendMessage(hwnd,0x231,IntPtr.Zero,IntPtr.Zero);rig.Card.Width+=200;rig.Card.Height+=100;await Settle();SendMessage(hwnd,0x232,IntPtr.Zero,IntPtr.Zero);
                Check(editor.ActualWidth>oldEditorWidth&&rig.Card.ActualHeight==720,"Joined preview/main do not resize together");
                rig.Controller.ShowMessage("模拟整窗刷新");await Settle();Check(rig.Card.ActualHeight==720,"Session resize lost after refresh");
                Capture(rig.Card,"36-预览与主界面整体缩放");
                Click(rig.Card,"OriginalButton");await Settle();Check(rig.Card.ActualWidth>400&&rig.Card.ActualHeight==720,"Collapsing image lost resized main dimensions");
            });
        }
        await Test("copied sideways preview is upright and marked without filling; explicit fill reuses marked fields",async()=>{
            int analyzed=0,connected=0;
            using var rotated=RemoteConnectHelper.V2.Ocr.ImagePreparation.Prepare(MakeImage(false),270,false);
            using var data=rotated.Encode(SKEncodedImageFormat.Png,100);byte[] input=data.ToArray();
            var upright=new RecognitionEngine().Analyze(new OcrDocument([
                new("brand","向日葵",new Box(40,30,130,30),.99f,"simulation","synthetic"),
                new("label","识别码",new Box(40,90,100,30),.99f,"simulation","synthetic"),
                new("code","123456789",new Box(40,130,240,40),.99f,"simulation","synthetic"),
                new("password","Ab1234",new Box(370,130,170,40),.99f,"simulation","synthetic")
            ],800,340,"simulation","synthetic"));
            Box Raw(Box b)=>RemoteConnectHelper.V2.Ocr.ImagePreparation.ToOriginal(b,90,340,800);
            var result=upright with {Document=upright.Document with{Width=340,Height=800,Rotation=90},
                DeviceCodes=upright.DeviceCodes.Select(c=>c with{Bounds=Raw(c.Bounds)}).ToArray(),
                Passwords=upright.Passwords.Select(c=>c with{Bounds=Raw(c.Bounds)}).ToArray(),
                SelectedCode=upright.SelectedCode! with{Bounds=Raw(upright.SelectedCode!.Bounds)},
                SelectedPassword=upright.SelectedPassword! with{Bounds=Raw(upright.SelectedPassword!.Bounds)}};
            await using var controller=new AssistantController((_,_,_)=>{analyzed++;return Task.FromResult(result);},(_,_,_,_)=>{connected++;throw new Exception("Unexpected connection");},(_,_)=>Task.FromResult(90));
            var host=new DesktopHost(controller,new Infrastructure.Preferences{WatchClipboard=true,ClipboardConsent=true},(_,_)=>Task.FromResult<Infrastructure.ClipboardSnapshot?>(new(801,input,null)));
            try{
                host.OpenCurrent();host.Card.ShowOriginal();await host.ReadClipboardAsync(true);await Settle();
                var source=(System.Windows.Media.Imaging.BitmapSource)Control<System.Windows.Controls.Image>(host.Card,"SourceImage").Source;
                Check(source.PixelWidth==800&&source.PixelHeight==340&&controller.Result==null&&controller.PreviewResult!=null&&analyzed==1&&connected==0,"Copy filled, connected, or failed to prepare upright marked preview");
                Check(Control<TextBox>(host.Card,"CodeBox").Text=="","Copy filled a device code");
                await host.ReadClipboardAsync(false,true);await Settle();host.Card.ResetPreview();await Settle();
                Check(analyzed==1&&connected==0&&controller.PreviewRotation==90,"Explicit recognition changed direction or connected");
                Check(Control<Canvas>(host.Card,"Overlay").Children.Count==2,"Rotated field marks missing");
                Capture(host.Card,"37-转正预览与眼睛图标");
            }finally{await host.ShutdownAsync();host.Card.ShutdownCard();}
        });
        await Test("multiple text fields have right-click assignment first and copy second without connecting",async()=>{
            int analyzed=0,connected=0;uint seq=901;
            string text="ToDesk\n设备代码：123456789\n临时密码：Ab1234\n\n设备代码：987654321\n临时密码：754321";
            await using var controller=new AssistantController((_,_,_)=>{analyzed++;throw new Exception("Text invoked image OCR");},(_,_,_,_)=>{connected++;throw new Exception("Assignment connected");});
            var host=new DesktopHost(controller,new Infrastructure.Preferences{WatchClipboard=true,ClipboardConsent=true,AutoConnect=true},(_,_)=>Task.FromResult<Infrastructure.ClipboardSnapshot?>(new(seq,null,text)));
            try{
                host.OpenCurrent();host.Card.ShowOriginal();await host.ReadClipboardAsync(true);await Settle();host.Card.ResetPreview();await Settle();
                var overlay=Control<Canvas>(host.Card,"Overlay");
                System.Windows.Shapes.Rectangle Field(string value)=>overlay.Children.OfType<System.Windows.Shapes.Rectangle>().Single(m=>m.Tag?.ToString()==value);
                Check(overlay.IsHitTestVisible&&overlay.Children.Count==4,"Multiple fields missing or not clickable");
                Check(Control<TextBox>(host.Card,"CodeBox").Text==""&&Control<TextBox>(host.Card,"PasswordBox").Text=="","Text copy filled inputs");
                foreach(var value in new[]{"123456789","987654321","Ab1234","754321"}){
                    var menu=Field(value).ContextMenu!;bool password=value is "Ab1234" or "754321";
                    Check(menu.Items.Count==2&&((System.Windows.Controls.MenuItem)menu.Items[0]).Header.ToString()==(password?"设为密码":"设为设备码")&&((System.Windows.Controls.MenuItem)menu.Items[1]).Header.ToString()=="复制","Wrong menu contents or order");
                }
                Capture(host.Card,"39-文本预览与多组框选");
                var chosen=Field("987654321");chosen.ContextMenu!.PlacementTarget=chosen;chosen.ContextMenu.IsOpen=true;await Settle();CaptureWpfMenu(chosen.ContextMenu,"40-设备码右键菜单");chosen.ContextMenu.IsOpen=false;
                ((System.Windows.Controls.MenuItem)chosen.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Check(Control<TextBox>(host.Card,"CodeBox").Text=="987654321"&&Control<TextBox>(host.Card,"PasswordBox").Text=="","Code assignment modified wrong field");
                var passwordMenu=Field("754321").ContextMenu!;
                ((System.Windows.Controls.MenuItem)passwordMenu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Check(Control<TextBox>(host.Card,"PasswordBox").Text=="754321"&&Control<TextBox>(host.Card,"PasswordBox").SelectedText=="754321","Numeric password assignment or selection failed");
                string? copied=null;host.Card.WriteClipboard=value=>copied=value;uint clipboardBefore=Infrastructure.ClipboardReader.GetClipboardSequenceNumber();
                ((System.Windows.Controls.MenuItem)Field("Ab1234").ContextMenu!.Items[1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Check(copied=="Ab1234"&&Infrastructure.ClipboardReader.GetClipboardSequenceNumber()==clipboardBefore&&Control<TextBox>(host.Card,"PasswordBox").Text=="754321","Copy modified inputs or actual test clipboard");
                long beforeCopyRevision=controller.Revision;var beforeCopyImage=controller.Image;string sourceText=text;
                seq=clipboardBefore;text=copied!;await host.ReadClipboardAsync(true);
                Check(controller.Revision==beforeCopyRevision&&ReferenceEquals(controller.Image,beforeCopyImage)&&controller.PreviewText==sourceText,"Own copy notification reprocessed or replaced preview");
                text=sourceText;
                var alpha=Field("Ab1234");
                alpha.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent});
                copied=null;Check(host.Card.CopySelectedPreview()&&copied=="Ab1234","Selected frame Ctrl+C path failed");
                Check(analyzed==0&&connected==0,"Text preview/assignment ran OCR or connected");
                Capture(host.Card,"41-右键分别填入设备码密码");
                Click(host.Card,"RecognizeButton");await Task.Delay(200);
                Check(controller.PreviewText==text&&controller.Image!=null&&Control<TextBox>(host.Card,"CodeBox").Text=="987654321"&&Control<TextBox>(host.Card,"PasswordBox").Text=="754321"&&connected==0,"Fill lost selected text fields or preview");
            }finally{await host.ShutdownAsync();host.Card.ShutdownCard();}
        });
        await using(var rig=new Rig()){
            await Test("preview uses opaque proportional toolbar/footer; image panning leaves window fixed",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();rig.Card.ShowOriginal();await Settle();
                var toolbar=Control<DockPanel>(rig.Card,"ImageToolbar");var footer=Control<Border>(rig.Card,"PreviewFooter");
                Check(toolbar.ActualHeight==36&&footer.ActualHeight==28,"Toolbar/footer proportions changed");
                Check(Control<TextBlock>(rig.Card,"PreviewEngineText").Text.Contains("PP-OCRv5"),"Image engine attribution missing");
                var bounds=DesktopWindows.NativeBounds(new WindowInteropHelper(rig.Card).Handle);rig.Card.PanPreview(new Vector(35,-15));await Settle();
                Check(rig.Card.PreviewZoom==1&&rig.Card.PreviewPan==new Vector(35,-15),"Full-view image cannot pan independently");
                Check(DesktopWindows.NativeBounds(new WindowInteropHelper(rig.Card).Handle)==bounds,"Image pan moved whole window");
                rig.Card.ResetPreview();await Settle();
                var visual=Control<Grid>(rig.Card,"WindowVisual");var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);RenderComposite(rig.Card,bitmap);
                var point=visual.PointFromScreen(footer.PointToScreen(new System.Windows.Point(20,14)));var pixels=new byte[4];bitmap.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),pixels,4,0);Check(pixels[3]==255,"Footer is translucent");
                footer.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonDownEvent});
                Check(rig.Card.IsDraggingWindow,"Footer does not initiate window dragging");
                footer.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,1,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
                Capture(rig.Card,"43-全图框选与不透明底栏");
            });
        }
        await using(var rig=new Rig()){
            await Test("preview choices survive new content and hiding within session; new session defaults full",async()=>{
                rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.EnterEditor();rig.Card.ShowOriginal();await Settle();
                Click(rig.Card,"FocusFieldsButton");rig.Card.HideToTray();rig.Card.SetEntryPlacement(null);rig.Card.EnterEditor();await Settle();
                Check(rig.Card.PreviewExpanded&&Control<Button>(rig.Card,"FocusFieldsButton").Tag?.ToString()=="Selected","Hide/reopen lost preview choice");
                await rig.Controller.RecognizeAsync(rig.ImageB);await Settle();Check(rig.Card.PreviewZoom>1,"New content lost location mode");
                Click(rig.Card,"OriginalButton");rig.Card.HideToTray();rig.Card.EnterEditor();await Settle();Check(!rig.Card.PreviewExpanded,"Collapsed choice lost on reopen");
                Click(rig.Card,"OriginalButton");await Settle();Check(rig.Card.PreviewZoom>1,"Expand lost location mode");
                await using var fresh=new Rig();fresh.Card.RestorePreviewVisibility(true);await fresh.Controller.RecognizeAsync(fresh.ImageA);fresh.Card.EnterEditor();await Settle();
                Check(fresh.Card.PreviewExpanded&&fresh.Card.PreviewZoom==1&&Control<Button>(fresh.Card,"FullImageButton").Tag?.ToString()=="Selected","Fresh window did not default to expanded/full view");
                Capture(rig.Card,"44-本次运行记住预览选择");
            });
        }
        await using(var rig=new Rig()){
            await Test("preview toggle triangle and clear action keep layout; recognize-and-connect starts from cached preview",async()=>{
                var prefs=new Infrastructure.Preferences{WatchClipboard=true,ClipboardConsent=true,AutoConnect=false};
                var host=new DesktopHost(rig.Controller,prefs,(_,_)=>Task.FromResult<Infrastructure.ClipboardSnapshot?>(new(990,rig.ImageA,null)));
                try{
                    host.OpenCurrent();host.Card.ShowOriginal();await host.ReadClipboardAsync(true);await Settle();
                    Check(Control<TextBlock>(host.Card,"PreviewToggleText").Text=="收起预览","Expanded preview label incorrect");
                    Check(Control<System.Windows.Controls.Panel>(host.Card,"RecentOptions").Children.Count==1&&Control<TextBox>(host.Card,"CodeBox").Text=="","Cached recognition did not record history while retaining empty fields");
                    Check(Control<Button>(host.Card,"ConnectButton").IsEnabled&&Control<Button>(host.Card,"ConnectButton").Content.ToString()=="识别并连接 ↵","Empty preview cannot recognize and connect");
                    Click(host.Card,"ConnectButton");await Task.Delay(650);
                    Check(rig.Calls.Count==1&&rig.Calls[0].Code=="123456789","Combined action did not recognize/fill/connect with auto-connect preference disabled");
                    host.Card.EnterEditor();host.Card.ShowOriginal();await Settle();
                    var code=Control<TextBox>(host.Card,"CodeBox").Text;Click(host.Card,"PreviewCloseButton");await Settle();
                    Check(host.Card.PreviewExpanded&&rig.Controller.Image==null&&Control<System.Windows.Controls.Image>(host.Card,"SourceImage").Source==null&&Control<TextBox>(host.Card,"CodeBox").Text==code,"Clear did not retain expanded state and filled code");
                    Check(Control<Button>(host.Card,"PreviewCloseButton").Content.ToString()=="清空预览"&&Control<Button>(host.Card,"PreviewCloseButton").ActualWidth>=72,"Clear button label clipped");
                    Check(Control<System.Windows.Shapes.Path>(host.Card,"PreviewArrow").RenderTransform.Value.M11<-.9,"Expanded arrow should point left");CheckToolbarAlignment(host.Card);Capture(host.Card,"53-清空预览与识别并连接");
                    Click(host.Card,"OriginalButton");await Task.Delay(180);Check(Control<TextBlock>(host.Card,"PreviewToggleText").Text=="打开预览"&&Control<System.Windows.Shapes.Path>(host.Card,"PreviewArrow").RenderTransform.Value.M11>.9,"Collapsed toggle triangle incorrect");Capture(host.Card,"54-打开预览三角朝左");
                }finally{await host.ShutdownAsync();host.Card.ShutdownCard();}
            });
        }
        await using(var pinRig=new Rig()){
            await Test("preview alone remains topmost and follows move resize hide and collapse",async()=>{
                var card=pinRig.Card;card.EnterEditor();card.ShowOriginal();await Task.Delay(950);card.UpdateLayout();
                var pin=card.PinnedPreviewWindow;Check(pin is {IsVisible:true}&&pin.Topmost,"Preview is not independently pinned");
                Check((DesktopWindows.ExtendedStyle(pin!)&8)!=0&&(DesktopWindows.ExtendedStyle(card)&8)==0,"Editor incorrectly remains topmost");
                var initial=DesktopWindows.NativeBounds(new WindowInteropHelper(card).Handle);card.MoveCardTo(initial.TopLeft+new Vector(10,10));await Settle();
                var moved=DesktopWindows.NativeBounds(new WindowInteropHelper(card).Handle);var preview=DesktopWindows.NativeBounds(new WindowInteropHelper(pin!).Handle);
                Check(Math.Abs(preview.X-moved.X)<2&&Math.Abs(preview.Y-moved.Y)<2,"Pinned preview did not follow whole window");
                card.Width+=60;card.Height+=20;await Settle();Check(Math.Abs(pin!.Height-card.ActualHeight)<2,"Pinned preview did not resize");
                Check(Control<TextBlock>(card,"PreviewHeading").Text=="界面分辨率 "+Control<TextBlock>(card,"WindowSizeText").Text,"Preview heading is not whole-window resolution");Check(Control<TextBlock>(card,"WindowSizeText").Visibility==Visibility.Collapsed,"Duplicate editor dimensions remain visible");
                Check(Control<DockPanel>(card,"ImageToolbar").Cursor==System.Windows.Input.Cursors.SizeAll,"Preview toolbar lacks move cursor");
                Capture(card,"55-预览独立置顶核对区普通显示");
                card.WindowState=WindowState.Maximized;await Settle();double expandedEditorWidth=Control<ScrollViewer>(card,"EditorScroll").ActualWidth;
                Click(card,"OriginalButton");await Settle();
                Check(Math.Abs(Control<ScrollViewer>(card,"EditorScroll").ActualWidth-expandedEditorWidth)<2,"Collapsed maximized editor filled the screen");
                Check(Control<Grid>(card,"WindowVisual").OpacityMask!=null,"Collapsed maximum kept whole-screen wallpaper");
                Capture(card,"57-收起预览最大化保持核对区宽度");
                card.WindowState=WindowState.Normal;card.ShowOriginal();await Settle();
                Click(card,"OriginalButton");await Settle();Check(!pin.IsVisible,"Collapsed preview stayed pinned");
                card.ShowOriginal();await Settle();card.WindowState=WindowState.Minimized;await Settle();Check(!pin.IsVisible,"Minimized preview remained on screen");
                card.WindowState=WindowState.Normal;card.EnterEditor();await Settle();Check(pin.IsVisible,"Restored preview missing");
            });
        }
        await using(var manualRig=new Rig()){
            await Test("manual client completion waits for stable success and hides both panels without connecting",async()=>{
                IReadOnlyList<ClientSessionSample> samples=[];var prefs=new Infrastructure.Preferences();
                var host=new DesktopHost(manualRig.Controller,prefs,(_,_)=>Task.FromResult<Infrastructure.ClipboardSnapshot?>(null),()=>Task.FromResult(samples));
                try{
                    await manualRig.Controller.NewManualAsync();host.OpenCurrent();host.Card.ShowOriginal();await Settle();await host.PollSessionAsync();
                    SessionSample Sample(bool connected,bool verification=false)=>new(8675309,false,false,connected,connected,verification,false,false,"synthetic manual UI");
                    samples=[new(RemoteApp.ToDesk,Sample(false,true))];await host.PollSessionAsync();
                    Check(host.Card.IsVisible&&manualRig.Controller.Stage!=AssistantStage.Confirmed,"Password prompt hid assistant");
                    samples=[new(RemoteApp.ToDesk,Sample(true))];await host.PollSessionAsync();Check(manualRig.Controller.Stage!=AssistantStage.Confirmed,"One success sample hid assistant");
                    await host.PollSessionAsync();Check(manualRig.Controller.Stage==AssistantStage.Confirmed&&manualRig.Calls.Count==0,"Manual confirmation launched connector");
                    await Task.Delay(1700);Check(!host.Card.IsVisible&&host.Card.PinnedPreviewWindow is not {IsVisible:true},"Confirmed manual success did not hide both panels");
                }finally{await host.ShutdownAsync();host.Card.ShutdownCard();}
            });
        }
        await using(var coveredRig=new Rig()){
            await Test("occluding client popups block clicks into another window",async()=>{
                coveredRig.Card.EnterEditor();await Settle();
                var box=Control<TextBox>(coveredRig.Card,"CodeBox");var screen=box.PointToScreen(new System.Windows.Point(20,15));
                var overlay=new Window{Width=100,Height=80,WindowStyle=WindowStyle.None,Topmost=true,ShowActivated=false,ShowInTaskbar=false,Background=System.Windows.Media.Brushes.White,WindowStartupLocation=WindowStartupLocation.Manual};
                try{
                    var scale=DesktopWindows.DpiScale(coveredRig.Card);overlay.Left=screen.X/scale-10;overlay.Top=screen.Y/scale-10;overlay.Show();await Settle();
                    var handle=new WindowInteropHelper(coveredRig.Card).Handle;
                    Check(!RemoteConnectHelper.Automation.ScreenOps.IsClickTargetUncovered(handle,(int)screen.X,(int)screen.Y),"Covered input accepted as clickable");
                    bool blocked=false;try{RemoteConnectHelper.Automation.ScreenOps.ClickVerifiedScreen(handle,(int)screen.X,(int)screen.Y);}catch(InvalidOperationException){blocked=true;}
                    Check(blocked,"Covered click was sent");
                }finally{overlay.Close();}
            });
        }
        await Test("ball menu stays within its actual monitor and shares opacity",async()=>{
            foreach(var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var ball=new FloatingBall{Opacity=.7};
                using var menu=new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("读取截图");menu.Items.Add("显示 / 修改连接信息");menu.Items.Add("关闭剪贴板监听");menu.Items.Add("隐藏悬浮球");menu.Items.Add("透明度 70%");menu.Items.Add("退出");
                try
                {
                    DesktopWindows.Place(ball,new Rect(screen.WorkingArea.X,screen.WorkingArea.Y,screen.WorkingArea.Width,screen.WorkingArea.Height));
                    ball.Show();await Settle();DesktopWindows.ShowBallMenu(menu,ball,.7);await Settle();
                    var area=DesktopWindows.WorkAreaFor(ball);var bounds=DesktopWindows.NativeBounds(menu.Handle);
                    Check(!bounds.IsEmpty&&area.Contains(bounds),"Ball menu escaped its monitor: "+screen.DeviceName);
                    Check(Math.Abs(menu.Opacity-ball.Opacity)<.02,"Menu/ball opacity differ");
                    Results.Add(new{Name="ball-menu-monitor",Passed=true,screen.DeviceName,Area=area.ToString(),Menu=bounds.ToString(),Opacity=menu.Opacity});
                    CaptureNativeMenu(menu,"16-悬浮球菜单-"+screen.DeviceName.Replace("\\","").Replace(".",""));
                }
                finally{menu.Close();ball.Close();}
            }
        });
        await Test("monitor layout remains inside work area across negative coordinates and DPI",()=>{
            foreach(var area in new[]{new Rect(0,0,1920,1040),new Rect(-2560,-200,2560,1400),new Rect(1920,0,1920,1040),new Rect(0,0,800,560)})
            foreach(double scale in new[]{1,1.25,1.5,2,2.5})
            foreach(var size in new[]{(320d,432d),(768d,520d)})
                Check(area.Contains(DesktopWindows.Fit(area,size.Item1,size.Item2,scale)),"Off-screen geometry");
            return Task.CompletedTask;
        });
        await Test("current physical monitor and DPI are recorded",()=>{
            Results.Add(new{Name="physical-displays",Passed=true,Displays=System.Windows.Forms.Screen.AllScreens.Select(s=>new{s.DeviceName,Bounds=s.Bounds.ToString(),WorkArea=s.WorkingArea.ToString()}).ToArray(),
                Dpi=System.Windows.Media.VisualTreeHelper.GetDpi(chat).DpiScaleX,Note="Only attached displays are physical; other DPI combinations above are geometry simulations"});
            return Task.CompletedTask;
        });
        Capture(chat,"08-模拟聊天仍可用");
        await Test("old V2 workspace comparison uses the same fake result",async()=>{
            await using var rig=new Rig();rig.Manual=true;await rig.Controller.RecognizeAsync(rig.ImageA);rig.Card.HideToTray();
            var shell=new ShellWindow(rig.Controller,new Infrastructure.Preferences{AutoConnect=false}){CanClose=true};
            shell.Show();shell.Activate();await Settle();Capture(shell,"09-旧二代工作台");
            Results.Add(new{Name="old-v2-window-size",Passed=true,Width=shell.ActualWidth,Height=shell.ActualHeight,Note="Original Shell XAML; shared fake controller. Activate in its old trigger path is source-evidenced; foreground unavailable here."});
            shell.Close();
        });
        if(_baselineV1!=null)await Test("old V1 card comparison disables hooks and injects a fake callback",async()=>{
            var assembly=System.Reflection.Assembly.LoadFrom(Path.GetFullPath(_baselineV1));
            var resultType=assembly.GetType("RemoteConnectHelper.Recognition.RecognizedResult",true)!;
            var oldResult=Activator.CreateInstance(resultType)!;
            resultType.GetProperty("DeviceCode")!.SetValue(oldResult,"123456789");
            resultType.GetProperty("Password")!.SetValue(oldResult,"Ab1234");
            resultType.GetProperty("BrandConfident")!.SetValue(oldResult,true);
            var kindType=assembly.GetType("RemoteConnectHelper.Recognition.RemoteAppKind",true)!;
            resultType.GetProperty("Kind")!.SetValue(oldResult,Enum.ToObject(kindType,1));
            var windowType=assembly.GetType("RemoteConnectHelper.UI.ConfirmCardWindow",true)!;
            var ctor=windowType.GetConstructors().Single();
            var callbackType=ctor.GetParameters()[2].ParameterType;
            var invoke=callbackType.GetMethod("Invoke")!;
            var connectionResultType=assembly.GetType("RemoteConnectHelper.Automation.ConnectResult",true)!;
            var failure=connectionResultType.GetMethod("Fail")!.Invoke(null,["Simulation only; no remote connection"])!;
            var taskMethod=typeof(Task).GetMethods().Single(m=>m.Name=="FromResult").MakeGenericMethod(connectionResultType);
            var expression=System.Linq.Expressions.Expression.Call(taskMethod,System.Linq.Expressions.Expression.Constant(failure,connectionResultType));
            var callback=System.Linq.Expressions.Expression.Lambda(callbackType,expression,invoke.GetParameters().Select(arg=>System.Linq.Expressions.Expression.Parameter(arg.ParameterType,arg.Name))).Compile();
            using var stream=new MemoryStream(MakeImage(false));var image=new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();image.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();
            var old=(Window)ctor.Invoke([oldResult,image,callback,null,null,false]);
            // Never install the original global Enter/Esc hook during a comparison.
            var hookMethod=windowType.GetMethod("OnSourceInitialized",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,[typeof(object),typeof(EventArgs)],null)!;
            old.SourceInitialized-=(EventHandler)Delegate.CreateDelegate(typeof(EventHandler),old,hookMethod);
            old.Show();windowType.GetMethod("Hold",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(old,null);
            await Settle();Capture(old,"10-一代确认卡");
            Results.Add(new{Name="old-v1-window-size",Passed=true,Width=old.ActualWidth,Height=old.ActualHeight,GlobalHookInstalled=false,ConnectionCallback="fake only"});
            old.Close();
        });
        chat.Close();
    }
    private sealed class Rig : IAsyncDisposable
    {
        public readonly byte[] ImageA=MakeImage(false),ImageB=MakeImage(true);
        public AssistantController Controller{get;}
        public ConnectionCard Card{get;}
        public int DelayMs=300,Cancelled,AnalysisCalls;
        public bool Manual,CleanupCompleted;
        public RecognitionResult? ResultOverride;
        public ConnectResult Next=ConnectResult.Confirm("模拟：已确认连接成功");
        public readonly List<(string Code,string Password)> Calls=[];
        public Rig()
        {
            Controller=new(Analyze,Connect);Card=new(Controller);
        }
        private async Task<RecognitionResult> Analyze(byte[] image,CancellationToken token,Action<string>? progress)
        {
            AnalysisCalls++;bool other=SHA256.HashData(image).SequenceEqual(SHA256.HashData(ImageB));await Task.Delay(55,token);
            if(ResultOverride!=null)return ResultOverride;
            var doc=new OcrDocument([
                new("brand","向日葵",new Box(40,30,130,30),.99f,"simulation","synthetic"),
                new("label","识别码",new Box(40,90,100,30),.99f,"simulation","synthetic"),
                new("code",other?"987654321":"123456789",new Box(40,130,240,40),.99f,"simulation","synthetic"),
                new("password",other?"Cd5678":"Ab1234",new Box(370,130,170,40),.99f,"simulation","synthetic")
            ],800,340,"simulation","synthetic");
            var result=new RecognitionEngine().Analyze(doc);
            return Manual?result with{Decision=ConnectionDecision.Manual,Reason="模拟：设备码存在歧义，请对照原图核对"}:result;
        }
        private async Task<ConnectResult> Connect(RemoteApp app,string code,string password,CancellationToken token)
        {
            Calls.Add((code,password));CleanupCompleted=false;
            try{await Task.Delay(DelayMs,token);return Next;}
            catch(OperationCanceledException){Cancelled++;throw;}
            finally{await Task.Delay(35);CleanupCompleted=true;}
        }
        public async ValueTask DisposeAsync(){await Controller.DisposeAsync();Card.ShutdownCard();}
    }
    private static byte[] MakeImage(bool other)
    {
        using var image=new SKBitmap(800,340);using var canvas=new SKCanvas(image);canvas.Clear(SKColors.White);
        using var font=new SKFont(SKTypeface.FromFamilyName("Consolas"),35);using var ink=new SKPaint{Color=SKColors.DarkSlateBlue,IsAntialias=true};
        canvas.DrawText("SIMULATED CUSTOMER / OFFLINE TEST",35,65,font,ink);
        canvas.DrawText(other?"987 654 321      Cd5678":"123 456 789      Ab1234",35,165,font,ink);
        using var encoded=image.Encode(SKEncodedImageFormat.Png,100);return encoded.ToArray();
    }
}
