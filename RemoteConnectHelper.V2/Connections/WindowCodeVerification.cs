using System.Text.RegularExpressions;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.Recognition;
using RemoteConnectHelper.V2.Infrastructure;
using SD=System.Drawing;

namespace RemoteConnectHelper.V2.Connections;

internal static class WindowCodeVerification
{
    // Re-locate the remote target row in the whole window. Local IDs and history are not evidence.
    internal static CodeReadback Evaluate(OcrDoc doc,string expected,double fieldY)
    {
        var lines=OcrGeo.BuildLines(doc);
        var heading=lines.Where(l=>Regex.IsMatch(Regex.Replace(l.Text,@"\s+",""),@"远程控制设|控制设备|远程协助他人"))
            .OrderBy(l=>Math.Abs(l.Y+55-fieldY)).FirstOrDefault();
        if(heading==null)return CodeReadback.Unreadable;
        var rows=lines.Where(l=>l.Y>heading.Y+18&&l.Y<heading.Y+140).OrderBy(l=>l.Y);
        foreach(var row in rows){
            if(Regex.IsMatch(row.Text,@"历史|最近连接|远程文件|观看模式|协作模式|远程桌面"))break;
            // Match complete numeric tokens, including split 3-digit groups across the full row.
            var groups=new List<List<OcrWord>>();
            foreach(var word in row.Words){
                if(!Regex.IsMatch(word.Text.Trim(),@"^[0-9][0-9\s-]*$"))continue;
                var previous=groups.LastOrDefault()?.LastOrDefault();
                if(previous==null||word.X-previous.Right>Math.Max(24,Math.Max(word.H,previous.H)*1.5))groups.Add(new());
                groups[^1].Add(word);
            }
            var numbers=groups.Select(g=>Regex.Replace(string.Concat(g.Select(w=>w.Text)),@"[\s-]", ""))
                .Where(n=>n.Length>=6&&n.Length<=13).ToArray();
            if(numbers.Length==0)continue;
            // Sunlogin puts a separate password field on the same row. The leftmost numeric group is the device field.
            return numbers[0]==expected?CodeReadback.Matched:CodeReadback.Mismatch;
        }
        return CodeReadback.Unreadable;
    }

    internal static async Task<CodeReadback> ReadAsync(SD.Bitmap image,string expected,double fieldY,CancellationToken token,Func<OcrWord,bool>? visible=null)
    {
        CodeReadback primary=CodeReadback.Unreadable;
        try{
            using var stream=new MemoryStream();image.Save(stream,SD.Imaging.ImageFormat.Png);
            var document=await NegativeClientStatus.StatusOcr.ReadAsync(stream.ToArray(),0,false,token);
            var doc=new OcrDoc{ImgWidth=document.Width,ImgHeight=document.Height,
                Words=document.Blocks.Select(b=>new OcrWord{Text=b.Text,X=b.Layout.X,Y=b.Layout.Y,W=b.Layout.Width,H=b.Layout.Height}).ToList()};
            if(visible!=null)doc.Words.RemoveAll(w=>!visible(w));
            primary=Evaluate(doc,expected,fieldY);
            Log.Write("整窗设备码回读 engine=PP-OCR result="+primary);
            if(primary==CodeReadback.Matched)return primary;
        }catch(OperationCanceledException){throw;}
        catch(Exception ex){Log.Write("整窗设备码PP-OCR不可用 "+ex.GetType().Name);}
        token.ThrowIfCancellationRequested();
        return primary;
    }
}
