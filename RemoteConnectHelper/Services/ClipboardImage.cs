using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RemoteConnectHelper.Services;

/// <summary>
/// 从剪贴板取用户 Win+Shift+S 的截图、或企微/微信/网页里“右键→复制图片”的图。
/// 通道优先级：CF_DIB 位图（Win+Shift+S）→ PNG/JPEG 内存流（IM 软件右键复制图片常见）→
/// CF_HDROP 文件拖放（复制的是图片文件）→ WPF 位图兜底。
/// 关键：用 Win32 打开剪贴板后，只在锁内把各格式字节/文件路径【拷出来就立即 CloseClipboard】，
/// 位图解码全部放到锁外，把占锁时间压到几十毫秒内，避免客服截图后立刻 Ctrl+V 粘贴时“贴不出来”。
/// 本工具绝不写/清剪贴板（填码用 SendKeys）。
/// </summary>
public static class ClipboardImage
{
    private const uint CfDib = 8;
    private const uint CfHdrop = 15;
    private const uint CfDibV5 = 17;
    private const int OpenRetries = 12;
    private const int OpenRetryDelayMs = 15;

    // 注册剪贴板格式（无需打开剪贴板即可注册，进程内缓存其 ID）
    private static readonly uint PfPng = RegisterClipboardFormat("PNG");
    private static readonly uint PfJfif = RegisterClipboardFormat("JFIF");
    private static readonly uint PfJpeg = RegisterClipboardFormat("JPEG");
    private static readonly uint PfGif = RegisterClipboardFormat("GIF");

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint uFormat);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatNameW(uint format, StringBuilder lpszFormatName, int cchMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

    /// <summary>一次开剪贴板拷回的原始内容（解码全部放到锁外）。</summary>
    private sealed class ClipSnapshot
    {
        public byte[]? Dib;
        public byte[]? Png;
        public byte[]? Jfif;
        public readonly List<string> Files = new();
        public readonly List<string> Formats = new();
    }

    /// <summary>取剪贴板位图，没有返回 null。返回的 BitmapSource 已冻结（Bgra32）。</summary>
    public static BitmapSource? TryGet()
    {
        ClipSnapshot? snap;
        try
        {
            snap = ReadClipboardLocked();
        }
        catch (Exception ex)
        {
            Logger.Warn("读取剪贴板异常：" + ex.Message);
            return TryWpfImage();
        }
        if (snap == null) return null;

        try
        {
            // 1) CF_DIB（Win+Shift+S 最常见，成功不记日志省刷屏）
            if (snap.Dib is { } dib)
            {
                var b = DecodeDib(dib);
                if (b != null) return b;
            }
            // 2) PNG 内存流（企微/微信右键复制图片）
            if (snap.Png is { } png && LooksImageBytes(png))
            {
                var b = DecodeImageBytes(png);
                if (b != null) { Logger.Info("剪贴板取图通道=PNG内存流"); return b; }
            }
            // 3) JPEG/JFIF/GIF 内存流
            if (snap.Jfif is { } jfif && LooksImageBytes(jfif))
            {
                var b = DecodeImageBytes(jfif);
                if (b != null) { Logger.Info("剪贴板取图通道=JPEG内存流"); return b; }
            }
            // 4) 复制的是图片文件
            foreach (var file in snap.Files)
            {
                if (!IsImageExtension(file)) continue;
                var b = DecodeImageFile(file);
                if (b != null) { Logger.Info($"剪贴板取图通道=文件 {Path.GetFileName(file)}"); return b; }
            }
            // 5) WPF 位图兜底（仍只读、不写）
            var wpf = TryWpfImage();
            if (wpf != null) { Logger.Info("剪贴板取图通道=WPF位图"); return wpf; }

            // 全部失败但剪贴板里确有图片线索（常见于 IM 私有格式）：记一条便于排查；纯文字/非图片文件不记
            if (LooksLikeImage(snap))
                Logger.Warn("剪贴板含图片线索但未能解码，格式=[" + string.Join(",", snap.Formats) +
                           "] 文件=[" + string.Join(",", snap.Files.Select(Path.GetFileName)) + "]");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Warn("剪贴板图片解码异常：" + ex.Message);
            return null;
        }
    }

    /// <summary>打开剪贴板（被占用时短暂重试），锁内只拷字节/路径后立即关闭。</summary>
    private static ClipSnapshot? ReadClipboardLocked()
    {
        bool opened = false;
        for (int i = 0; i < OpenRetries; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) { opened = true; break; }
            System.Threading.Thread.Sleep(OpenRetryDelayMs);
        }
        if (!opened) return null;

        try
        {
            var s = new ClipSnapshot();

            // 枚举本次剪贴板全部格式名（排查私有格式用）
            uint f = 0;
            while (true)
            {
                f = EnumClipboardFormats(f);
                if (f == 0) break;
                s.Formats.Add(FormatName(f));
            }

            if (IsClipboardFormatAvailable(CfDib))
                s.Dib = ReadGlobalBytes(GetClipboardData(CfDib));
            else if (IsClipboardFormatAvailable(CfDibV5))
                s.Dib = ReadGlobalBytes(GetClipboardData(CfDibV5));

            if (PfPng != 0 && IsClipboardFormatAvailable(PfPng))
                s.Png = ReadGlobalBytes(GetClipboardData(PfPng));
            if (PfJfif != 0 && IsClipboardFormatAvailable(PfJfif))
                s.Jfif = ReadGlobalBytes(GetClipboardData(PfJfif));
            else if (PfJpeg != 0 && IsClipboardFormatAvailable(PfJpeg))
                s.Jfif = ReadGlobalBytes(GetClipboardData(PfJpeg));
            else if (PfGif != 0 && IsClipboardFormatAvailable(PfGif))
                s.Jfif = ReadGlobalBytes(GetClipboardData(PfGif));

            if (IsClipboardFormatAvailable(CfHdrop))
                ReadDropFiles(GetClipboardData(CfHdrop), s);

            return s;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>锁内：把一个全局内存句柄整体拷成字节。</summary>
    private static byte[]? ReadGlobalBytes(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        int size = (int)GlobalSize(h).ToUInt64();
        if (size <= 0) return null;
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var buf = new byte[size];
            Marshal.Copy(p, buf, 0, size);
            return buf;
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    /// <summary>锁内：从 CF_HDROP 拷出文件路径列表（只拷字符串，很快；不调 DragFinish）。</summary>
    private static void ReadDropFiles(IntPtr hMem, ClipSnapshot s)
    {
        if (hMem == IntPtr.Zero) return;
        IntPtr hDrop = GlobalLock(hMem);
        if (hDrop == IntPtr.Zero) return;
        try
        {
            uint n = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < n; i++)
            {
                uint len = DragQueryFileW(hDrop, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                DragQueryFileW(hDrop, i, sb, sb.Capacity);
                if (sb.Length > 0) s.Files.Add(sb.ToString());
            }
        }
        finally
        {
            GlobalUnlock(hMem);
        }
    }

    private static string FormatName(uint id)
    {
        string? std = id switch
        {
            1 => "CF_TEXT", 2 => "CF_BITMAP", 7 => "CF_OEMTEXT", 8 => "CF_DIB",
            13 => "CF_UNICODETEXT", 14 => "CF_ENHMETAFILE", 15 => "CF_HDROP",
            17 => "CF_DIBV5", _ => null
        };
        if (std != null) return std;
        var sb = new StringBuilder(256);
        int got = GetClipboardFormatNameW(id, sb, sb.Capacity);
        return got > 0 ? sb.ToString() : $"fmt{id}";
    }

    private static bool IsImageExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";
    }

    /// <summary>按魔数判断字节流是不是常见图片（PNG/JPEG/BMP/GIF）。</summary>
    private static bool LooksImageBytes(byte[] d)
    {
        if (d.Length < 4) return false;
        if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true; // PNG
        if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                 // JPEG
        if (d[0] == (byte)'B' && d[1] == (byte)'M') return true;                       // BMP
        if (d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F') return true;  // GIF
        return false;
    }

    private static bool LooksLikeImage(ClipSnapshot s)
    {
        if (s.Files.Any(IsImageExtension)) return true;
        return s.Formats.Any(n =>
            n.Contains("PNG", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("JFIF", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("JPEG", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("GIF", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("DIB", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Bitmap", StringComparison.OrdinalIgnoreCase));
    }

    private static BitmapSource? DecodeImageBytes(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return ToBgra32(frame);
        }
        catch { return null; }
    }

    private static BitmapSource? DecodeImageFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapFrame.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return ToBgra32(frame);
        }
        catch { return null; }
    }

    private static BitmapSource? TryWpfImage()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage()) return null;
            var src = System.Windows.Clipboard.GetImage();
            if (src == null || src.PixelWidth <= 0) return null;
            return ToBgra32(src);
        }
        catch { return null; }
    }

    /// <summary>锁外：给 CF_DIB 补 14 字节 BITMAPFILEHEADER 成完整 BMP 流再解码（不碰剪贴板）。</summary>
    private static BitmapSource? DecodeDib(byte[] dib)
    {
        try
        {
            if (dib.Length < 40) return null;
            int biSize = BitConverter.ToInt32(dib, 0);
            if (biSize < 40 || biSize > dib.Length) return null;
            short biBitCount = BitConverter.ToInt16(dib, 14);
            int biCompression = BitConverter.ToInt32(dib, 16);
            int biClrUsed = BitConverter.ToInt32(dib, 32);

            // 只直接处理常见未压缩/位域格式；JPEG(6)/PNG(7) 压缩 DIB 交给后续通道/WPF
            if (biCompression != 0 && biCompression != 3) return null;

            int palette = biClrUsed > 0 ? biClrUsed : (biBitCount <= 8 ? 1 << biBitCount : 0);
            int maskExtra = (biCompression == 3 && biSize < 124) ? 12 : 0; // BITMAPINFOHEADER 后的 3 个 DWORD 掩码
            int offBits = 14 + biSize + palette * 4 + maskExtra;
            if (offBits >= dib.Length + 14) return null;

            using var ms = new MemoryStream(14 + dib.Length);
            using var w = new BinaryWriter(ms);
            w.Write((byte)'B');
            w.Write((byte)'M');
            w.Write((int)(14 + dib.Length)); // bfSize
            w.Write((short)0);               // bfReserved1
            w.Write((short)0);               // bfReserved2
            w.Write((int)offBits);           // bfOffBits
            w.Write(dib);
            w.Flush();
            ms.Position = 0;

            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return ToBgra32(frame);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource ToBgra32(BitmapSource src)
    {
        if (src.Format == System.Windows.Media.PixelFormats.Bgra32)
        {
            if (!src.IsFrozen) src.Freeze();
            return src;
        }
        var fb = new FormatConvertedBitmap();
        fb.BeginInit();
        fb.Source = src;
        fb.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
        fb.EndInit();
        fb.Freeze();
        return fb;
    }
}
