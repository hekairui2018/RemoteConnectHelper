using System.IO;
using System.Text;

namespace RemoteConnectHelper.Services;

/// <summary>
/// 简单文件日志，按天滚动到 %AppData%/RemoteConnectHelper/logs/
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RemoteConnectHelper", "logs");

    public static string LogDir => _dir;

    private static string CurrentFile =>
        Path.Combine(_dir, $"rc_{DateTime.Now:yyyyMMdd}.log");

    /// <summary>当天日志文件的完整路径（可能尚不存在）。</summary>
    public static string TodayLogPath => CurrentFile;

    /// <summary>当天日志是否已生成。</summary>
    public static bool TodayLogExists => File.Exists(CurrentFile);

    /// <summary>
    /// 一键导出当天日志到桌面，命名“远程连接助手日志_yyyyMMdd.log”（同名覆盖）。
    /// 返回写出的完整路径；当天还没有日志返回 null；桌面不可写时抛异常，由调用方改弹“另存为”。
    /// </summary>
    public static string? ExportTodayToDesktop()
    {
        string src = CurrentFile;
        if (!File.Exists(src)) return null;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(desktop);
        string dst = Path.Combine(desktop, $"远程连接助手日志_{DateTime.Now:yyyyMMdd}.log");
        lock (_lock)
        {
            File.Copy(src, dst, true);
        }
        return dst;
    }

    /// <summary>日志保留天数：保留含今天在内最近 7 个自然日的 rc_yyyyMMdd.log，更早的启动时清理。</summary>
    private const int RetentionDays = 7;

    static Logger()
    {
        try { Directory.CreateDirectory(_dir); } catch { /* ignore */ }
        try { PurgeOldLogs(); } catch { /* 清理失败不影响主流程 */ }
    }

    /// <summary>按文件名里的 yyyyMMdd 删除超过保留期的日志；只认 rc_yyyyMMdd.log，避免误删其它文件。</summary>
    private static void PurgeOldLogs()
    {
        var cutoff = DateTime.Today.AddDays(-(RetentionDays - 1)); // 早于该日的删除（含今天共留 7 天）
        foreach (var f in Directory.EnumerateFiles(_dir, "rc_*.log"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(f); // rc_yyyyMMdd
                if (name.Length < 12) continue;
                var datePart = name.Substring(3, 8);
                if (!DateTime.TryParseExact(datePart, "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                    continue;
                if (d.Date < cutoff) File.Delete(f);
            }
            catch { /* 单个文件删不掉就跳过 */ }
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null)
        => Write("ERROR", ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(CurrentFile,
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}\n", Encoding.UTF8);
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
