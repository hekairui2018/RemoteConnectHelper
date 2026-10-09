using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteConnectHelper.Services;

/// <summary>
/// 异常截图留存：只在一次连接“没成功”时把客户原图存成 JPG；确认进入会话(成功)不留、用户主动取消不留。
/// 存到 %AppData%/RemoteConnectHelper/shots，随日志保留 7 天、总量上限 50MB(超了删最旧)。
/// 右键“导出当天日志”时，若当天有留存截图，会把日志和截图一起打成 zip 发回排查。
/// </summary>
public static class SnapshotStore
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteConnectHelper", "shots");

    private const int RetentionDays = 7;
    private const long MaxBytes = 50L * 1024 * 1024;
    private const int JpegQuality = 85;
    private static readonly object _lock = new();

    public static string ShotsDir => _dir;

    static SnapshotStore()
    {
        try { Directory.CreateDirectory(_dir); Purge(); }
        catch { /* 留存目录初始化失败不影响主流程 */ }
    }

    /// <summary>留存一张异常截图。
    /// kind：nocode(未认出码) / pickbrand(软件待确认) / needpwd(要密码或密码未通过) / failed(连接失败) / unconfirmed(已发起未确认进会话)。
    /// code 为设备码(可空)。返回写出的文件路径；失败返回 null。</summary>
    public static string? Save(string kind, string? code, BitmapSource? bmp)
    {
        if (bmp == null) return null;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                string safeCode = Sanitize(code);
                string name = $"shot_{DateTime.Now:yyyyMMdd_HHmmss}_{kind}"
                              + (string.IsNullOrEmpty(safeCode) ? "" : "_" + safeCode) + ".jpg";
                string path = Path.Combine(_dir, name);
                EncodeJpeg(bmp, path);
                Purge();
                Logger.Info($"异常截图已留存：{name}（{KindText(kind)}）");
                return path;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("留存异常截图失败：" + ex.Message);
            return null;
        }
    }

    public static string KindText(string k) => k switch
    {
        "nocode" => "未认出码",
        "pickbrand" => "软件待确认",
        "needpwd" => "需要密码/密码未通过",
        "failed" => "连接失败",
        "unconfirmed" => "已发起未确认进会话",
        _ => k
    };

    private static void EncodeJpeg(BitmapSource bmp, string path)
    {
        // JPEG 不支持透明通道/非 24bpp，统一转 Bgr24；传入位图应已 Freeze 以便跨线程。
        BitmapSource src = bmp;
        if (src.Format != PixelFormats.Bgr24 && src.Format != PixelFormats.Bgr32)
            src = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        if (src.CanFreeze && !src.IsFrozen) src.Freeze();
        var enc = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Open(path, FileMode.Create, FileAccess.Write);
        enc.Save(fs);
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s.Trim().Replace(" ", ""))
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        string r = sb.ToString();
        return r.Length > 20 ? r[..20] : r;
    }

    /// <summary>保留含今天在内最近 7 天；再按最旧优先删到总量 ≤ 50MB。</summary>
    private static void Purge()
    {
        var cutoff = DateTime.Today.AddDays(-(RetentionDays - 1));
        foreach (var f in Directory.EnumerateFiles(_dir, "shot_*.jpg"))
        {
            try { if (File.GetLastWriteTime(f).Date < cutoff) File.Delete(f); }
            catch { /* 单个删不掉就跳过 */ }
        }
        var files = Directory.EnumerateFiles(_dir, "shot_*.jpg")
            .Select(p => new FileInfo(p))
            .OrderBy(f => f.LastWriteTime)
            .ToList();
        long total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= MaxBytes) break;
            try { total -= f.Length; f.Delete(); }
            catch { /* 占用中跳过 */ }
        }
    }

    /// <summary>今天留存的截图（按文件名时间升序）。</summary>
    public static List<string> TodayFiles()
    {
        try
        {
            string prefix = $"shot_{DateTime.Now:yyyyMMdd}";
            return Directory.EnumerateFiles(_dir, prefix + "*.jpg")
                .OrderBy(p => p).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>把当天日志(.log)与当天异常截图(若有)打成一个 zip 到指定 zipPath；始终包含当天日志，
    /// 当天有异常截图时一并放进 shots/。成功返回 zipPath，失败返回 null。</summary>
    public static string? ExportZip(string logSourcePath, string dateStr, string zipPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(zipPath)) File.Delete(zipPath);
            var shots = TodayFiles();
            using (var fs = File.Open(zipPath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(logSourcePath, $"远程连接助手日志_{dateStr}.log");
                foreach (var s in shots)
                    archive.CreateEntryFromFile(s, "shots/" + Path.GetFileName(s));
            }
            Logger.Info($"当天日志包已导出：{zipPath}（含异常截图 {shots.Count} 张）");
            return zipPath;
        }
        catch (Exception ex)
        {
            Logger.Warn("打包当天日志zip失败：" + ex.Message);
            return null;
        }
    }
}
