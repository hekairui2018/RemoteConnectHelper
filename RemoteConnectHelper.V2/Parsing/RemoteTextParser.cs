using System.Text.RegularExpressions;
using RemoteConnectHelper.V2.Domain;
namespace RemoteConnectHelper.V2.Parsing;
public static class RemoteTextParser
{
    public static bool Suspected(string text)=>text.Length<=20000
        && Regex.IsMatch(text,@"向日葵|sunlogin|awesun|(?<![a-z])(?:ToDesk|AnyDesk|GameViewer|RustDesk|TeamViewer|UltraViewer)(?![a-z])|UU远程|网易UU",RegexOptions.IgnoreCase)
        && Regex.IsMatch(text,@"(?<!\d)(?:\d[ \t-]*){7,10}(?!\d)");
    public static RecognitionResult? Parse(string text,OcrDocument? document=null)
    {
        if(!Suspected(text))return null;
        var lines=Regex.Split(text,@"\r?\n|[;；]").Where(x=>x.Trim().Length>0).ToArray();
        var blocks=new List<OcrTextBlock>();
        for(int i=0;i<lines.Length;i++)blocks.Add(new("text-"+i,lines[i],new Box(20,20+i*48,Math.Max(180,lines[i].Length*18),28),1,"纯文本（未运行OCR）","clipboard-text"));
        var result=new RecognitionEngine().Analyze(document??new OcrDocument(blocks,1400,Math.Max(300,lines.Length*48+40),"纯文本（未运行OCR）","clipboard-text"));
        // Explicit field structure, a single target and known app are required for text auto-connect.
        bool explicitField=Regex.IsMatch(text,@"设备(?:代?码)|识别码|远程码|设备ID|本设备ID|AnyDesk ID|RustDesk ID|本机ID|您的ID|伙伴ID",RegexOptions.IgnoreCase);
        return !explicitField?result with{Decision=ConnectionDecision.Manual,Reason="文本设备码缺少明确字段标签，请核对"}:result;
    }
}
