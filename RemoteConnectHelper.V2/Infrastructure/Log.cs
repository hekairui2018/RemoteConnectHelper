using System.Text.RegularExpressions;

namespace RemoteConnectHelper.V2.Infrastructure
{
    public static class Log
    {
        private static readonly object Gate = new();
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrayDeskV2");
        public static string FilePath => Path.Combine(Root, "logs", $"helper-{DateTime.Now:yyyyMMdd}.log");
        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    message = Regex.Replace(message, @"\d{9,}", "[设备码]");
                    System.IO.File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
                }
            }
            catch { }
        }
    }
}
namespace RemoteConnectHelper.Services
{
    // 客户端兼容层统一使用二代的本地日志，不共享一代配置或日志目录。
    public static class Logger
    {
        public static void Info(string text) => V2.Infrastructure.Log.Write(text);
        public static void Warn(string text) => V2.Infrastructure.Log.Write("WARN " + text);
        public static void Error(string text, Exception? ex = null) => V2.Infrastructure.Log.Write("ERROR " + text + " " + ex?.GetType().Name);
    }
}
