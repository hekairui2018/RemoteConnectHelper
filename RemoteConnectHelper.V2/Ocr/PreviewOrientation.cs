using RemoteConnectHelper.V2.Domain;
using SkiaSharp;

namespace RemoteConnectHelper.V2.Ocr;

/// <summary>Page direction from text geometry and the binary line-direction classifier.
/// Does not run text recognition or extract any field values.</summary>
public static class PreviewOrientation
{
    public static int Choose(IEnumerable<(int Rotation, double Confidence)> observations)
    {
        var votes=observations.Where(v=>v.Confidence>=.90).ToArray();
        if(votes.Length<2)return 0;
        var best=votes.GroupBy(v=>v.Rotation).OrderByDescending(g=>g.Count()).First();
        return best.Count()>=2 && best.Count()>=votes.Length*.75 ? best.Key : 0;
    }
    public static Box ToDisplay(Box b,int rotation,double width,double height)=>rotation switch
    {
        90=>new(height-b.Bottom,b.X,b.Height,b.Width),
        180=>new(width-b.Right,height-b.Bottom,b.Width,b.Height),
        270=>new(b.Y,width-b.Right,b.Height,b.Width),
        _=>b
    };
    internal static SKBitmap Crop(SKBitmap source,SKPointI[] points)
    {
        int left=Math.Clamp(points.Min(p=>p.X),0,source.Width-1),top=Math.Clamp(points.Min(p=>p.Y),0,source.Height-1);
        int right=Math.Clamp(points.Max(p=>p.X),left+1,source.Width),bottom=Math.Clamp(points.Max(p=>p.Y),top+1,source.Height);
        var crop=new SKBitmap(right-left,bottom-top);
        using var canvas=new SKCanvas(crop);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source,new SKRect(left,top,right,bottom),new SKRect(0,0,crop.Width,crop.Height));
        return crop;
    }
}
