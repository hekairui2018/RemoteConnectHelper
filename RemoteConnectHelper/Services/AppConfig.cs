using System.IO;
using System.Text.Json;

namespace RemoteConnectHelper.Services;

/// <summary>应用配置（快捷键、倒计时、开机自启），存 %AppData%/RemoteConnectHelper/config.json</summary>
public class AppConfig
{
    /// <summary>热键修饰符，默认 Ctrl+Alt</summary>
    public uint Modifiers { get; set; } = 0x0002 | 0x0001; // MOD_CONTROL | MOD_ALT
    /// <summary>热键虚拟键码，默认 'R' = 0x52</summary>
    public uint Key { get; set; } = 0x52;
    /// <summary>确认卡片倒计时秒数</summary>
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; } = false;
    /// <summary>
    /// 剪贴板出现新截图时是否“认出远程码即自动连”（零按键）。默认 false：
    /// 截图后只在后台预识别并把结果记住，等客服点一下悬浮球 / 按 Ctrl+Alt+R 才连接（点时命中缓存秒连）；
    /// 置 true 时回到“截图后约 1 秒认出即自动连”的全自动模式。无论开关，每张截图都会先预识别缓存。
    /// </summary>
    public bool AutoConnectOnClipboard { get; set; } = false;
    /// <summary>北斗悬浮球位置（WPF DIP）；null = 首次使用默认放在右下角（现场记者灵眸球上方）。</summary>
    public double? BallLeft { get; set; }
    public double? BallTop { get; set; }

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "RemoteConnectHelper");
    private static string PathFile => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpt = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(PathFile))
            {
                var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(PathFile));
                if (c != null) return c;
            }
        }
        catch
        {
            // 配置损坏/非法 JSON：回退默认并把干净的默认配置写回，避免每次启动都吞异常
            var fallback = new AppConfig();
            try { fallback.Save(); } catch { /* 写回失败也不影响使用 */ }
            return fallback;
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFile, JsonSerializer.Serialize(this, JsonOpt));
        }
        catch { /* ignore */ }
    }
}
