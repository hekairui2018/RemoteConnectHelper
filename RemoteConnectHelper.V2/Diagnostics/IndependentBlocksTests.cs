using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using RemoteConnectHelper.V2.Application;
using RemoteConnectHelper.V2.UI;
using SkiaSharp;
namespace RemoteConnectHelper.V2.Diagnostics;
public static class IndependentBlocksTests
{
    [DllImport("user32.dll")]private static extern bool PrintWindow(IntPtr window,IntPtr dc,uint flags);
    private static async Task Settle(){await Task.Delay(200);}
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);int result=1;
        var app=new System.Windows.Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(async()=>{
            var rows=new List<object>();AssistantController? controller=null;ConnectionCard? card=null;
            void Check(bool condition,string name){rows.Add(new{Name=name,Passed=condition});if(!condition)throw new InvalidOperationException(name);}
            try{
                controller=new AssistantController((_,_,_)=>throw new InvalidOperationException("No OCR in window fixture"));
                card=new ConnectionCard(controller){Left=500,Top=180,AnchorArea=new Rect(0,0,1920,1080)};
                using(var bitmap=new SKBitmap(600,360)){
                    bitmap.Erase(new SKColor(230,238,249));using var canvas=new SKCanvas(bitmap);
                    using var paint=new SKPaint{Color=new SKColor(45,76,125),IsAntialias=true};using var font=new SKFont(SKTypeface.Default,42);
                    canvas.DrawText("Preview 123 456 789",32,120,font,paint);using var encoded=bitmap.Encode(SKEncodedImageFormat.Png,100);
                    await controller.PreviewClipboardAsync(encoded.ToArray());
                }
                card.RestorePreviewVisibility(true);card.EnterEditor();card.MoveCardTo(new System.Windows.Point(520,520));await Settle();
                var preview=card.PinnedPreviewWindow!;Check(preview!=null&&preview.IsVisible,"Preview is a separate visible window");
                Check(Math.Abs(preview!.ActualWidth-card.ActualWidth)<1&&Math.Abs(preview.ActualHeight-card.ActualHeight)<1,"Default blocks have identical width and height");
                Rect Main()=>DesktopWindows.NativeBounds(new WindowInteropHelper(card).Handle);
                Rect Preview()=>DesktopWindows.NativeBounds(new WindowInteropHelper(preview).Handle);
                Check(card.BlocksLocked,"Initial attached blocks default to locked");
                Check(card.PreviewExpanded&&card.PreviewDock=="top"&&Math.Abs(Preview().Bottom-Main().Top)<1,"Default preview above editor with shared seam");
                Check(card.Topmost&&preview.Topmost,"Both blocks are permanently topmost");
                DesktopWindows.EndFrontPulse(card);await Settle();
                Check(card.Topmost&&(DesktopWindows.ExtendedStyle(card)&8)!=0,"Ending front pulse preserves editor topmost style");
                var original=Main();
                card.MovePreviewBlockTo(new System.Windows.Point(40,60));await Settle();Check(Main()==original,"Moving preview leaves editor position unchanged");
                var main=Main();var size=Preview().Size;
                foreach(var sample in new[]{(Side:"left",P:new System.Windows.Point(main.Left-size.Width+5,main.Top+6)),(Side:"right",P:new System.Windows.Point(main.Right-5,main.Top+6)),(Side:"top",P:new System.Windows.Point(main.Left+6,main.Top-size.Height+5)),(Side:"bottom",P:new System.Windows.Point(main.Left+6,main.Bottom-5))}){
                    card.MovePreviewBlockTo(sample.P);await Settle();var bounds=Preview();
                    Check(card.PreviewDock==sample.Side,"Snap "+sample.Side);
                    Check(sample.Side is "left" or "right"?Math.Abs(bounds.Top-main.Top)<1:Math.Abs(bounds.Left-main.Left)<1,"Flush alignment "+sample.Side);
                }
                card.MovePreviewBlockTo(new System.Windows.Point(80,80));await Settle();
                Check(card.PreviewDock==null,"Dragging away detaches the blocks");
                preview.Width=420;preview.Height=540;await Settle();
                Check(Math.Abs(card.ActualWidth-preview.ActualWidth)<1&&Math.Abs(card.ActualHeight-preview.ActualHeight)<1,"Resizing preview keeps editor equal");
                card.Width=400;card.Height=520;await Settle();
                Check(Math.Abs(card.ActualWidth-preview.ActualWidth)<1&&Math.Abs(card.ActualHeight-preview.ActualHeight)<1,"Resizing editor keeps preview equal");
                card.MovePreviewBlockTo(new System.Windows.Point(40,80));await Settle();var beforeMain=Main();var beforePreview=Preview();
                async Task Drag(int x,int y,int dx,int dy){
                    card.BeginBlockDrag(true,new System.Windows.Point(x,y));
                    card.UpdateBlockDrag(new System.Windows.Point(x+dx,y+dy));
                    card.EndBlockDrag();await Settle();
                }
                await Drag((int)beforePreview.Left+20,(int)beforePreview.Top+18,30,40);
                Check(Main()==beforeMain,"Dragging preview header does not move editor");
                Check(Math.Abs(Preview().Left-beforePreview.Left-30)<2&&Math.Abs(Preview().Top-beforePreview.Top-40)<2,"Preview header moves only preview");
                var headerMoved=Preview();await Drag((int)headerMoved.Left+80,(int)headerMoved.Bottom-14,25,-25);
                Check(Main()==beforeMain&&Math.Abs(Preview().Left-headerMoved.Left-25)<2,"Preview footer moves only preview");
                Check((DesktopWindows.ExtendedStyle(preview)&0x20)==0,"Preview accepts mouse input; no click-through");
                Check(preview.Topmost,"Preview stays topmost");
                card.ZoomPreview(2,new System.Windows.Point(100,100));Check(card.PreviewZoom==2,"Image zoom works without Alt");
                card.PanPreview(new Vector(12,15));Check(card.PreviewPan.Length>0,"Image panning stays inside preview");card.ResetPreview();
                card.MovePreviewBlockTo(new System.Windows.Point(Main().Left-Preview().Width,Main().Top));await Settle();
                Check(((System.Windows.Controls.Grid)preview.Content).Clip is System.Windows.Media.RectangleGeometry round&&round.RadiusX==12,"Preview has rounded clipping");
                Check(((System.Windows.FrameworkElement)card.FindName("WindowVisual")).Clip is System.Windows.Media.RectangleGeometry editorRound&&editorRound.RadiusX==12,"Editor has matching rounded clipping");
                foreach(var side in new[]{"left","right","top","bottom"}){
                    var m=Main();var b=Preview();var point=side switch{"left"=>new System.Windows.Point(m.Left-b.Width,m.Top),"right"=>new System.Windows.Point(m.Right,m.Top),"top"=>new System.Windows.Point(m.Left,m.Top-b.Height),_=>new System.Windows.Point(m.Left,m.Bottom)};
                    card.MovePreviewBlockTo(point);await Settle();
                    Check(card.SeamLockButton?.Visibility==Visibility.Visible,"Seam control visible "+side);
                    card.SeamLockButton!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Check(card.BlocksLocked,"Seam click locks "+side);
                    foreach(bool dragPreview in new[]{true,false}){
                        var oldMain=Main();var oldPreview=Preview();var start=dragPreview?oldPreview.TopLeft:oldMain.TopLeft;
                        card.BeginBlockDrag(dragPreview,start);card.UpdateBlockDrag(start+new Vector(21,19));card.EndBlockDrag();await Settle();
                        Check(Main().TopLeft==oldMain.TopLeft+new Vector(21,19)&&Preview().TopLeft==oldPreview.TopLeft+new Vector(21,19),"Locked group drag "+side+" "+dragPreview);
                    }
                    card.SeamLockButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Check(!card.BlocksLocked,"Seam click unlocks "+side);
                }
                card.MovePreviewBlockTo(new System.Windows.Point(Main().Left+90,Main().Top+80));await Settle();
                Check(card.SeamLockButton!.Visibility==Visibility.Collapsed&&!card.BlocksLocked,"Detached blocks hide seam control");
                card.MovePreviewBlockTo(new System.Windows.Point(Main().Left-Preview().Width,Main().Top));await Settle();
                card.SeamLockButton!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));await Settle();
                card.Width=384;card.Height=500;await Settle();
                var work=DesktopWindows.WorkAreaFor(card);card.MoveCardTo(new System.Windows.Point(work.Right-384-16,work.Bottom-500-16));
                card.MovePreviewBlockTo(new System.Windows.Point(Main().Left,Main().Top-Preview().Height));await Settle();
                Check(((System.Windows.Controls.TextBlock)card.FindName("PreviewHeading")).Text=="界面分辨率 384×1000","Stacked layout reports 384x1000 total resolution");
                // Capture only our opaque test surfaces; exclude unrelated desktop/remote sessions.
                var mainShot=Main();var previewShot=Preview();var total=mainShot;total.Union(previewShot);
                using var image=new System.Drawing.Bitmap((int)Math.Ceiling(total.Width),(int)Math.Ceiling(total.Height));
                using(var graphics=System.Drawing.Graphics.FromImage(image)){
                    graphics.Clear(System.Drawing.Color.White);
                    void Capture(Window window,Rect bounds){
                        using var own=new System.Drawing.Bitmap((int)bounds.Width,(int)bounds.Height);using(var painter=System.Drawing.Graphics.FromImage(own)){var dc=painter.GetHdc();try{PrintWindow(new WindowInteropHelper(window).Handle,dc,2);}finally{painter.ReleaseHdc(dc);}}
                        graphics.DrawImageUnscaled(own,(int)(bounds.Left-total.Left),(int)(bounds.Top-total.Top));
                    }
                    Capture(preview,previewShot);Capture(card,mainShot);
                }
                image.Save(Path.Combine(output,"等宽等高独立窗口.png"));
                result=0;
            }catch(Exception ex){rows.Add(new{Name="Fixture",Passed=false,Error=ex.Message});}
            finally{card?.ShutdownCard();if(controller!=null)await controller.DisposeAsync();File.WriteAllText(Path.Combine(output,"独立窗口验收.json"),JsonSerializer.Serialize(new{Passed=result==0,Scope="Managed drag state and real window geometry/rendering; no OS mouse injection; physical header/footer drag awaits user confirmation",Tests=rows},new JsonSerializerOptions{WriteIndented=true}));app.Shutdown();}
        }));
        app.Run();return result;
    }
}
