using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RemoteConnectHelper.Services;

/// <summary>
/// 从剪贴板取用户 Win+Shift+S 的截图。
/// 关键：用 Win32 直接把 CF_DIB 字节拷出来后【立即 CloseClipboard】，位图解码全部放到锁外，
/// 把占用剪贴板的时间从 WPF GetImage 的几百毫秒压到几十毫秒以内，避免客服截图后立刻
/// Ctrl+V 粘贴时，本工具正持有剪贴板导致“贴不出来”。本工具绝不写/清剪贴板（填码用 SendKeys）。
/// </summary>
public static class ClipboardImage
{
    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;
    private const int OpenRetries = 12;
    private const int OpenRetryDelayMs = 15;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint uFormat);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    /// <summary>取剪贴板位图，没有返回 null。返回的 BitmapSource 已冻结（Bgra32）。</summary>
    public static BitmapSource? TryGet()
    {
        try
        {
            byte[]? dib = ReadDibBytesLocked();
            if (dib != null)
            {
                var bmp = DecodeDib(dib);
                if (bmp != null) return bmp;
            }

            // 回退：PNG/JPEG 压缩 DIB 等少见格式交给 WPF（仍只读、不写）
            if (!System.Windows.Clipboard.ContainsImage()) return null;
            var src = System.Windows.Clipboard.GetImage();
            if (src == null || src.PixelWidth <= 0) return null;
            return ToBgra32(src);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开剪贴板（被占用时短暂重试），把 CF_DIB 整体拷成字节后立刻关闭，尽量缩短持锁时间。</summary>
    private static byte[]? ReadDibBytesLocked()
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
            uint fmt = IsClipboardFormatAvailable(CfDib) ? CfDib
                     : IsClipboardFormatAvailable(CfDibV5) ? CfDibV5 : 0u;
            if (fmt == 0) return null;

            IntPtr h = GetClipboardData(fmt);
            if (h == IntPtr.Zero) return null;
            int size = (int)GlobalSize(h).ToUInt64();
            if (size <= 0) return null;

            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                byte[] buf = new byte[size];
                Marshal.Copy(p, buf, 0, size);
                return buf;
            }
            finally
            {
                GlobalUnlock(h);
            }
        }
        finally
        {
            CloseClipboard();
        }
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

            // 只直接处理常见未压缩/位域格式；JPEG(6)/PNG(7) 等回退 WPF
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
