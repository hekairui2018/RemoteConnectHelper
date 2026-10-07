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

    static Logger()
    {
        try { Directory.CreateDirectory(_dir); } catch { /* ignore */ }
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
