using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Window=System.Windows.Window;
using Button=System.Windows.Controls.Button;
using Control=System.Windows.Controls.Control;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Panel=System.Windows.Controls.Panel;
namespace RemoteConnectHelper.V2.UI;
public partial class ConnectionCard
{
    private Window? _previewPin;
    private Grid? _previewPinRoot;
    private bool _syncingPin,_movingPreviewBlock;
    private bool _blocksLocked=true;
    private Button? _seamLock;
    private System.Windows.Point _groupMainOrigin,_groupPreviewOrigin;
    internal bool BlocksLocked=>_blocksLocked;
    internal Button? SeamLockButton=>_seamLock;
    private void ToggleBlockLock()
    {
        if(_previewDock==null||_previewPin?.IsVisible!=true)return;
        _blocksLocked=!_blocksLocked;UpdateBlockChrome();UpdateIndependentPreviewMask();
    }
    private void UpdateBlockChrome()
    {
        UpdateWindowDimensions();
        if(WindowVisual.ActualWidth>0&&WindowVisual.ActualHeight>0)
            WindowVisual.Clip=new RectangleGeometry(new Rect(WindowVisual.RenderSize),12,12);
        if(_previewPinRoot==null)return;
        if(_previewPinRoot.ActualWidth>0&&_previewPinRoot.ActualHeight>0)
            _previewPinRoot.Clip=new RectangleGeometry(new Rect(_previewPinRoot.RenderSize),12,12);
        if(_seamLock==null)return;
        _seamLock.Visibility=_previewDock!=null?Visibility.Visible:Visibility.Collapsed;
        _seamLock.HorizontalAlignment=_previewDock switch{"left"=>HorizontalAlignment.Right,"right"=>HorizontalAlignment.Left,_=>HorizontalAlignment.Center};
        _seamLock.VerticalAlignment=_previewDock switch{"top"=>VerticalAlignment.Bottom,"bottom"=>VerticalAlignment.Top,_=>VerticalAlignment.Center};
        _seamLock.Margin=_previewDock switch{"top"=>new Thickness(7,7,7,36),"bottom"=>new Thickness(7,44,7,7),_=>new Thickness(7)};
        var lockPath=new System.Windows.Shapes.Path{Data=Geometry.Parse(_blocksLocked?"M 6,10 L 6,7 C 6,0 18,0 18,7 L 18,10 M 5,10 L 19,10 19,22 5,22 Z M 12,14 L 12,18":"M 6,10 L 6,7 C 6,0 18,0 18,7 M 5,10 L 19,10 19,22 5,22 Z M 12,14 L 12,18"),StrokeThickness=1.8,StrokeLineJoin=PenLineJoin.Round,Width=22,Height=24,Stretch=Stretch.Uniform};
        lockPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,_blocksLocked?"Theme2863D4":"Theme71829C");_seamLock.Content=lockPath;
        // Both states use a clear action label for accessibility and hover help.
        _seamLock.ToolTip=_blocksLocked?"已锁定：一起拖动，点击解锁":"点击锁定：两区一起拖动";
        System.Windows.Automation.AutomationProperties.SetName(_seamLock,_blocksLocked?"解锁两区":"锁定两区");
        _seamLock.SetResourceReference(Control.BackgroundProperty,_blocksLocked?"ThemeEAF1FF":"ThemeWhite");
        _seamLock.SetResourceReference(Control.ForegroundProperty,_blocksLocked?"Theme2863D4":"Theme71829C");
        _seamLock.SetResourceReference(Control.BorderBrushProperty,_blocksLocked?"Theme5486E5":"ThemeD5DFEF");
    }
    private void MoveLockedBlocks(Vector delta)
    {
        if(_previewPin==null)return;
        _syncingPin=true;
        try{
            MoveCardTo(_groupMainOrigin+delta);
            _previewPosition=_groupPreviewOrigin+delta;
            DesktopWindows.MoveOnly(_previewPin,_previewPosition.Value);
        }finally{_syncingPin=false;}
    }
    private System.Windows.Point? _previewPosition;
    private string? _previewDock="top";
    internal Window? PinnedPreviewWindow=>_previewPin;
    internal string? PreviewDock=>_previewDock;
    private void InitializePinnedPreview()
    {
        Loaded+=(_,_)=>{UpdateBlockChrome();SyncPinnedPreview();};LocationChanged+=(_,_)=>SyncPinnedPreview();
        SizeChanged+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{UpdateBlockChrome();SyncPinnedPreview();}),DispatcherPriority.Loaded);
        StateChanged+=(_,_)=>Dispatcher.BeginInvoke(new Action(SyncPinnedPreview),DispatcherPriority.Loaded);
        IsVisibleChanged+=(_,_)=>SyncPinnedPreview();
    }
    private System.Windows.Point PointInCard(UIElement element,System.Windows.Point point)
    {
        if(PresentationSource.FromVisual(element)==PresentationSource.FromVisual(this))return element.TranslatePoint(point,WindowVisual);
        return WindowVisual.PointFromScreen(element.PointToScreen(point));
    }
    private Rect BoundsInCard(UIElement element,Rect rect)=>new(PointInCard(element,rect.TopLeft),PointInCard(element,rect.BottomRight));
    internal static (System.Windows.Point Position,string? Side) SnapBlock(Rect moving,Rect fixedBlock,double threshold)
    {
        var candidates=new[]{
            (Point:new System.Windows.Point(fixedBlock.Left-moving.Width,fixedBlock.Top),Side:"left",Distance:Math.Abs(moving.Right-fixedBlock.Left),Cross:Math.Abs(moving.Top-fixedBlock.Top)),
            (Point:new System.Windows.Point(fixedBlock.Right,fixedBlock.Top),Side:"right",Distance:Math.Abs(moving.Left-fixedBlock.Right),Cross:Math.Abs(moving.Top-fixedBlock.Top)),
            (Point:new System.Windows.Point(fixedBlock.Left,fixedBlock.Top-moving.Height),Side:"top",Distance:Math.Abs(moving.Bottom-fixedBlock.Top),Cross:Math.Abs(moving.Left-fixedBlock.Left)),
            (Point:new System.Windows.Point(fixedBlock.Left,fixedBlock.Bottom),Side:"bottom",Distance:Math.Abs(moving.Top-fixedBlock.Bottom),Cross:Math.Abs(moving.Left-fixedBlock.Left))};
        var hit=candidates.Where(c=>c.Distance<=threshold&&c.Cross<=threshold*2).OrderBy(c=>c.Distance+c.Cross).FirstOrDefault();
        return hit.Side==null?(moving.TopLeft,null):(hit.Point,hit.Side);
    }
    internal void MovePreviewBlockTo(System.Windows.Point point)
    {
        if(_previewPin==null)return;
        var bounds=DesktopWindows.NativeBounds(new WindowInteropHelper(_previewPin).Handle);
        var main=DesktopWindows.NativeBounds(new WindowInteropHelper(this).Handle);
        var snap=SnapBlock(new Rect(point,bounds.Size),main,16*DesktopWindows.DpiScale(this));
        _previewPosition=snap.Position;_previewDock=snap.Side;if(_previewDock==null)_blocksLocked=false;DesktopWindows.MoveOnly(_previewPin,snap.Position);UpdateBlockChrome();UpdateIndependentPreviewMask();
    }
    private void FinishBlockMove()
    {
        if(_blocksLocked){_movingPreviewBlock=false;PreserveVisiblePosition();UpdateBlockChrome();return;}
        if(_movingPreviewBlock){_movingPreviewBlock=false;UpdateBlockChrome();return;}
        if(_previewPin?.IsVisible==true){
            var main=DesktopWindows.NativeBounds(new WindowInteropHelper(this).Handle);var preview=DesktopWindows.NativeBounds(new WindowInteropHelper(_previewPin).Handle);
            var snapped=SnapBlock(main,preview,16*DesktopWindows.DpiScale(this));
            if(snapped.Side!=null){MoveCardTo(snapped.Position);_previewPosition=preview.TopLeft;_previewDock=snapped.Side switch{"left"=>"right","right"=>"left","top"=>"bottom",_=>"top"};}
        }
        PreserveVisiblePosition();UpdateBlockChrome();UpdateIndependentPreviewMask();
    }
    private void SyncPinnedPreview()
    {
        if(_syncingPin||_canClose)return;
        if(!IsLoaded||!IsVisible||!_expanded||WindowState==WindowState.Minimized){_previewPin?.Hide();UpdateWindowDimensions();return;}
        _syncingPin=true;
        try{
            if(_previewPin==null){
                ImagePanel.Children.Remove(PreviewVisual);PreviewVisual.Margin=new Thickness(0);
                _previewPinRoot=new Grid{ClipToBounds=true};_previewPinRoot.Children.Add(PreviewVisual);
                _seamLock=new Button{Width=32,Height=32,FontSize=16,FontFamily=new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),Cursor=System.Windows.Input.Cursors.Hand,Focusable=false,Padding=new Thickness(0)};
                var template=new ControlTemplate(typeof(Button));
                var plate=new FrameworkElementFactory(typeof(Border));plate.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));plate.SetValue(Border.BorderThicknessProperty,new Thickness(1));
                plate.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});
                plate.SetBinding(Border.BorderBrushProperty,new System.Windows.Data.Binding("BorderBrush"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});
                var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
                plate.AppendChild(content);template.VisualTree=plate;_seamLock.Template=template;
                _seamLock.Click+=(_,_)=>ToggleBlockLock();Panel.SetZIndex(_seamLock,100);_previewPinRoot.Children.Add(_seamLock);
                _previewPin=new Window{Title="灰Desk · 预览",Tag="PinnedPreview",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.CanResize,MinWidth=320,MinHeight=420,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,Topmost=true,ShowActivated=false,ShowInTaskbar=false,FontFamily=FontFamily,Content=_previewPinRoot,Resources=Resources};
                _previewPin.PreviewKeyDown+=Card_KeyDown;_previewPin.AllowDrop=true;_previewPin.Drop+=Card_Drop;
                _previewPin.SourceInitialized+=(_,_)=>HwndSource.FromHwnd(new WindowInteropHelper(_previewPin).Handle)?.AddHook(PinnedSizingHook);
                _previewPin.SizeChanged+=(_,_)=>{
                    if(_syncingPin||_previewPin==null||WindowState!=WindowState.Normal)return;
                    _syncingPin=true;try{Width=_compactWidth=Math.Max(320,_previewPin.ActualWidth);Height=_compactHeight=_expandedHeight=Math.Max(420,_previewPin.ActualHeight);_previewWidth=_compactWidth;}finally{_syncingPin=false;}
                    Dispatcher.BeginInvoke(new Action(()=>{DrawOverlay();UpdateIndependentPreviewMask();}),DispatcherPriority.Loaded);
                };
            }
            _previewPin.Width=Math.Max(320,ActualWidth);_previewPin.Height=Math.Max(420,ActualHeight);
            _previewPinRoot!.Background=new VisualBrush(ShellBackground){Stretch=Stretch.Fill};
            new WindowInteropHelper(_previewPin).EnsureHandle();
            var main=DesktopWindows.NativeBounds(new WindowInteropHelper(this).Handle);double width=_previewPin.Width*DesktopWindows.DpiScale(this),height=_previewPin.Height*DesktopWindows.DpiScale(this);
            if(_previewPosition==null&&_previewDock=="left"){
                var area=DesktopWindows.WorkAreaFor(this);
                if(main.Left-width<area.Left&&main.Right+width<=area.Right)_previewDock="right";
                else if(main.Left-width<area.Left)_previewDock="top";
            }
            var point=_previewDock switch{"left"=>new System.Windows.Point(main.Left-width,main.Top),"right"=>new System.Windows.Point(main.Right,main.Top),"top"=>new System.Windows.Point(main.Left,main.Top-height),"bottom"=>new System.Windows.Point(main.Left,main.Bottom),_=>_previewPosition??new System.Windows.Point(main.Left-width,main.Top)};
            if(_previewPosition==null){var area=DesktopWindows.WorkAreaFor(this);point=new System.Windows.Point(Math.Clamp(point.X,area.Left,Math.Max(area.Left,area.Right-width)),Math.Clamp(point.Y,area.Top,Math.Max(area.Top,area.Bottom-height)));}
            _previewPosition=point;DesktopWindows.MoveOnly(_previewPin,point);
            if(!_previewPin.IsVisible)_previewPin.Show();_previewPin.UpdateLayout();UpdateIndependentPreviewMask();
        }finally{_syncingPin=false;}
    }
    private void UpdateIndependentPreviewMask()
    {
        if(_previewPinRoot==null||_previewPin==null||!_previewPin.IsVisible||_previewPin.ActualWidth<1||_previewPin.ActualHeight<1)return;
        UpdateBlockChrome();double w=_previewPin.ActualWidth,h=_previewPin.ActualHeight;var group=new DrawingGroup();
        void Paint(Rect rect,System.Windows.Media.Brush brush){if(!rect.IsEmpty&&rect.Width>0&&rect.Height>0)group.Children.Add(new GeometryDrawing(brush,null,new RectangleGeometry(rect)));}
        Rect Local(UIElement element,Rect rect)=>new(element.TranslatePoint(rect.TopLeft,_previewPinRoot),element.TranslatePoint(rect.BottomRight,_previewPinRoot));
        Paint(new Rect(0,0,w,h),new SolidColorBrush(System.Windows.Media.Color.FromArgb(90,255,255,255)));
        Paint(Local(ImageToolbar,new Rect(ImageToolbar.RenderSize)),System.Windows.Media.Brushes.White);Paint(Local(PreviewFooter,new Rect(PreviewFooter.RenderSize)),System.Windows.Media.Brushes.White);
        if(SourceImage.Source is System.Windows.Media.Imaging.BitmapSource image&&PreviewArea.ActualWidth>0&&PreviewArea.ActualHeight>0){
            double scale=Math.Min(PreviewArea.ActualWidth/image.PixelWidth,PreviewArea.ActualHeight/image.PixelHeight);
            var fitted=new Rect((PreviewArea.ActualWidth-image.PixelWidth*scale)/2,(PreviewArea.ActualHeight-image.PixelHeight*scale)/2,image.PixelWidth*scale,image.PixelHeight*scale);
            if(ImageViewport.Clip!=null)fitted.Intersect(ImageViewport.Clip.Bounds);
            if(!fitted.IsEmpty){var visible=Local(ImageViewport,fitted);visible.Intersect(Local(PreviewArea,new Rect(PreviewArea.RenderSize)));Paint(visible,System.Windows.Media.Brushes.White);}
        }
        if(_seamLock?.IsVisible==true){var lockBounds=Local(_seamLock,new Rect(_seamLock.RenderSize));group.Children.Add(new GeometryDrawing(System.Windows.Media.Brushes.White,null,new RectangleGeometry(lockBounds,10,10)));}
        _previewPinRoot.OpacityMask=new DrawingBrush(group){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,w,h),ViewportUnits=BrushMappingMode.RelativeToBoundingBox,Viewport=new Rect(0,0,1,1),Stretch=Stretch.Fill};
    }
    private IntPtr PinnedSizingHook(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(message!=0x84)return IntPtr.Zero;
        var bounds=DesktopWindows.NativeBounds(window);double x=unchecked((short)lParam.ToInt64()),y=unchecked((short)(lParam.ToInt64()>>16));double edge=6*DesktopWindows.DpiScale(_previewPin!);
        bool left=x<bounds.Left+edge,right=x>=bounds.Right-edge,top=y<bounds.Top+edge,bottom=y>=bounds.Bottom-edge;
        handled=true;return new IntPtr(top?(left?13:right?14:12):bottom?(left?16:right?17:15):left?10:right?11:1);
    }
    private void ClosePinnedPreview(){if(_previewPin!=null){_previewPin.Close();_previewPin=null;}}
}
