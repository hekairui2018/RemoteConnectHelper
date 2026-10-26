using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;
namespace RemoteConnectHelper.V2.Infrastructure;
public sealed record ClipboardSnapshot(uint Sequence,byte[]? Image,string? Text);
public static class ClipboardReader
{
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern uint DragQueryFileW(IntPtr drop,uint index,StringBuilder? filename,uint size);
    public static bool IsImageFile(string path)=>new[]{".png",".jpg",".jpeg",".bmp",".webp",".gif"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase);
    private static string? ReadImageFileDropOnce()
    {
        if(!IsClipboardFormatAvailable(15)||!OpenClipboard(IntPtr.Zero))return null;
        try
        {
            var drop=GetClipboardData(15);if(drop==IntPtr.Zero||DragQueryFileW(drop,uint.MaxValue,null,0)!=1)return null;
            uint size=DragQueryFileW(drop,0,null,0);if(size==0||size>32760)return null;
            var path=new StringBuilder((int)size+1);DragQueryFileW(drop,0,path,(uint)path.Capacity);
            string file=path.ToString();return IsImageFile(file)?file:null;
        }finally{CloseClipboard();}
    }
    public static bool HasImage=>IsClipboardFormatAvailable(8)||IsClipboardFormatAvailable(17)||IsClipboardFormatAvailable(2)||IsClipboardFormatAvailable(RegisterClipboardFormat("PNG"));
    public static string? ReadTextOnce()
    {
        if(!IsClipboardFormatAvailable(13)||!OpenClipboard(IntPtr.Zero))return null;
        try
        {
            var memory=GetClipboardData(13);if(memory==IntPtr.Zero)return null;
            ulong count=GlobalSize(memory).ToUInt64();if(count==0||count>40002)return null;
            var ptr=GlobalLock(memory);if(ptr==IntPtr.Zero)return null;
            try{return Marshal.PtrToStringUni(ptr,(int)count/2)?.TrimEnd('\0');}finally{GlobalUnlock(memory);}
        }finally{CloseClipboard();}
    }
    public static async Task<ClipboardSnapshot?> ReadAsync(Func<bool> current,CancellationToken token)
    {
        uint seq=GetClipboardSequenceNumber();
        for(int retry=0;retry<4;retry++)
        {
            token.ThrowIfCancellationRequested();if(!current()||seq!=GetClipboardSequenceNumber())return null;
            if(HasImage)
            {
                // Legacy image reader only copies under the native lock and decodes after closing it.
                var bitmap=RemoteConnectHelper.Services.ClipboardImage.TryGet();
                if(bitmap!=null)
                {
                    using var stream=new MemoryStream();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));encoder.Save(stream);
                    if(seq!=GetClipboardSequenceNumber()||!current())return null;
                    return new(seq,stream.ToArray(),null);
                }
            }
            else
            {
                if(IsClipboardFormatAvailable(15))
                {
                    string? file=ReadImageFileDropOnce();
                    if(file==null){if(retry<3){await Task.Delay(75*(retry+1),token);continue;}return null;}
                    var info=new FileInfo(file);if(!info.Exists||info.Length>32*1024*1024)return null;
                    var bytes=await File.ReadAllBytesAsync(file,token);
                    return current()&&seq==GetClipboardSequenceNumber()?new(seq,bytes,null):null;
                }
                var text=ReadTextOnce();
                if(text!=null&&seq==GetClipboardSequenceNumber()&&current())return new(seq,null,text);
                if(!IsClipboardFormatAvailable(13))return null; // Files and arbitrary formats are ignored.
            }
            await Task.Delay(75*(retry+1),token);
        }
        return null;
    }
}
