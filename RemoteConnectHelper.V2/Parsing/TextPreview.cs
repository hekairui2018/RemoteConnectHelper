using System.Text.RegularExpressions;
using RemoteConnectHelper.V2.Domain;
using SkiaSharp;

namespace RemoteConnectHelper.V2.Parsing;
public sealed record TextPreview(byte[] Image,RecognitionResult? Result)
{
    public static TextPreview Create(string text)
    {
        using var font=new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei"),24);
        var blocks=new List<OcrTextBlock>();var runs=new List<(string Text,float X,float Y)>();
        float x=28,y=28,maxX=400;bool truncated=false;
        foreach(Match match in Regex.Matches(text.Replace("\r\n","\n").Replace('\r','\n').Replace("\t","    "),@"\n|[^\S\n]+|[A-Za-z0-9]+|[\p{IsCJKUnifiedIdeographs}]{1,24}|[^\s]")){
            if(y>6400){truncated=true;break;}
            if(match.Value=="\n"){x=28;y+=44;continue;}
            // Bound unusually long unbroken tokens without discarding their text.
            foreach(var part in match.Value.Chunk(48).Select(c=>new string(c))){
                float width=font.MeasureText(part);
                if(x+width>1250&&x>28){x=28;y+=44;}
                runs.Add((part,x,y));
                if(!string.IsNullOrWhiteSpace(part))blocks.Add(new("text-preview-"+blocks.Count,part,new Box(x,y,width,30),1,"纯文本（未运行OCR）","clipboard-text"));
                x+=width;maxX=Math.Max(maxX,x+28);
            }
        }
        int widthPx=(int)Math.Ceiling(maxX),heightPx=(int)Math.Ceiling(Math.Max(240,y+70));
        using var bitmap=new SKBitmap(widthPx,heightPx);using(var canvas=new SKCanvas(bitmap)){
            canvas.Clear(SKColors.White);using var ink=new SKPaint{Color=new SKColor(34,49,72),IsAntialias=true};
            foreach(var run in runs)canvas.DrawText(run.Text,run.X,run.Y+25,font,ink);
        }
        var doc=new OcrDocument(blocks,widthPx,heightPx,"纯文本（未运行OCR）","clipboard-text");
        var result=RemoteTextParser.Parse(text,doc);
        if(truncated&&result!=null)result=result with{Decision=ConnectionDecision.Manual,Reason="文本较长，请核对当前预览中的目标"};
        using var data=bitmap.Encode(SKEncodedImageFormat.Png,100);return new(data.ToArray(),result);
    }
}
