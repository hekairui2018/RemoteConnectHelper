using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;

namespace RemoteConnectHelper.V2.UI;

/// <summary>Local-only background playback. Decode frames on demand, not into an unbounded frame cache.</summary>
public sealed class WallpaperPlayer:IDisposable
{
    private readonly DispatcherTimer _timer=new();
    private System.Drawing.Image? _gif;
    private MemoryStream? _gifStream;
    private int[] _delays=[];
    private int _frame;
    public BitmapSource Frame {get;private set;}=null!;
    public int FrameCount=>Math.Max(1,_delays.Length);
    public event Action? FrameChanged;
    public WallpaperPlayer(string path)
    {
        var file=new FileInfo(path);if(!file.Exists)throw new FileNotFoundException("壁纸文件不存在",path);
        if(file.Length>50_000_000)throw new InvalidDataException("壁纸文件过大，请选择50MB以内的图片");
        byte[] bytes=File.ReadAllBytes(path);
        using(var data=SKData.CreateCopy(bytes))using(var codec=SKCodec.Create(data)){
            if(codec==null||(long)codec.Info.Width*codec.Info.Height>20_000_000)throw new InvalidDataException("壁纸无法读取或分辨率过大");
        }
        try{
            if(Path.GetExtension(path).Equals(".gif",StringComparison.OrdinalIgnoreCase)){
                _gifStream=new(bytes,false);_gif=System.Drawing.Image.FromStream(_gifStream);
                int count=_gif.GetFrameCount(System.Drawing.Imaging.FrameDimension.Time);
                byte[]? timing=null;try{timing=_gif.GetPropertyItem(0x5100)?.Value;}catch(ArgumentException){}
                _delays=Enumerable.Range(0,count).Select(i=>timing!=null&&timing.Length>i*4+3?Math.Clamp(BitConverter.ToInt32(timing,i*4)*10,20,60000):100).ToArray();
                SetFrame(0);_timer.Tick+=(_,_)=>{try{SetFrame((_frame+1)%_delays.Length);FrameChanged?.Invoke();}catch{_timer.Stop();}};
            }else{
                using var bitmap=SKBitmap.Decode(bytes)??throw new InvalidDataException("壁纸无法解码");using var png=bitmap.Encode(SKEncodedImageFormat.Png,100);Frame=Decode(png.ToArray());
            }
        }catch{Dispose();throw;}
    }
    private static BitmapSource Decode(byte[] bytes){using var stream=new MemoryStream(bytes);var frame=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);frame.Freeze();return frame;}
    private void SetFrame(int index){
        _frame=index;_gif!.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Time,index);
        using var rendered=new System.Drawing.Bitmap(_gif.Width,_gif.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using(var graphics=System.Drawing.Graphics.FromImage(rendered))graphics.DrawImage(_gif,0,0,_gif.Width,_gif.Height);
        using var stream=new MemoryStream();rendered.Save(stream,System.Drawing.Imaging.ImageFormat.Png);Frame=Decode(stream.ToArray());_timer.Interval=TimeSpan.FromMilliseconds(_delays[index]);
    }
    public void SetPlaying(bool playing){if(playing&&FrameCount>1)_timer.Start();else _timer.Stop();}
    public void Dispose(){_timer.Stop();_gif?.Dispose();_gifStream?.Dispose();_gif=null;_gifStream=null;}
}
