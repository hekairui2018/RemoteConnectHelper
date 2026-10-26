using System.IO;
using Microsoft.Win32;

namespace RemoteConnectHelper.Automation;

/// <summary>从注册表卸载项/常见路径发现远程软件 exe。</summary>
public static class AppPaths
{
    private static readonly string[] UninstallKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    };

    /// <summary>
    /// 名称匹配：英文关键词必须是“独立单词”，避免子串误命中。
    /// 关键坑："Autodesk"（Au-`todesk`）包含子串 "todesk"，裸 Contains 会把 AutoCAD 当成 ToDesk，
    /// 进而去启动 AutoCAD 的安装垫片弹“不支持的 16 位应用程序”。中文关键词仍用 Contains。
    /// </summary>
    private static bool NameMatches(string display, string keyword)
    {
        if (keyword.Any(ch => ch > 127))
            return display.Contains(keyword, StringComparison.Ordinal);

        int idx = 0;
        while ((idx = display.IndexOf(keyword, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool beforeOk = idx == 0 || !IsAsciiLetter(display[idx - 1]);
            int end = idx + keyword.Length;
            bool afterOk = end >= display.Length || !IsAsciiLetter(display[end]);
            if (beforeOk && afterOk) return true;
            idx++;
        }
        return false;
    }

    private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>
    /// 校验候选确实是目标主程序：文件存在、文件名等于 exeName、
    /// 且不是 Windows Installer 广告快捷方式垫片（C:\Windows\Installer\{GUID}\xxx_icon.exe，
    /// 多为 16 位 stub，启动会弹“不支持的 16 位应用程序”）。
    /// </summary>
    private static bool RealExe(string? path, string exeName)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim('"', ' ');
        try
        {
            if (!File.Exists(p)) return false;
            if (p.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase)) return false;
            if (p.EndsWith("_icon.exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(Path.GetFileName(p), exeName, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>按显示名关键字查安装目录下的 exe（严格词边界匹配 + 垫片过滤）。</summary>
    public static string? FindByDisplayName(string[] nameKeywords, string exeName)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var sub in UninstallKeys)
                    {
                        using var root = baseKey.OpenSubKey(sub);
                        if (root == null) continue;
                        foreach (var keyName in root.GetSubKeyNames())
                        {
                            try
                            {
                                using var k = root.OpenSubKey(keyName);
                                if (k == null) continue;
                                var display = k.GetValue("DisplayName")?.ToString() ?? "";
                                if (!nameKeywords.Any(kw => NameMatches(display, kw)))
                                    continue;

                                // DisplayIcon 常直接指向 exe，但必须文件名匹配且不是 Installer 垫片
                                var icon = k.GetValue("DisplayIcon")?.ToString() ?? "";
                                if (!string.IsNullOrWhiteSpace(icon))
                                {
                                    var p = icon.Split(',')[0];
                                    if (RealExe(p, exeName)) return p.Trim('"', ' ');
                                }
                                var loc = k.GetValue("InstallLocation")?.ToString() ?? "";
                                if (!string.IsNullOrWhiteSpace(loc))
                                {
                                    var candidate = Path.Combine(loc.Trim('"', ' '), exeName);
                                    if (RealExe(candidate, exeName)) return candidate;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>注册表与常见路径合并，取第一个通过校验的真实主程序。</summary>
    private static string? Pick(string? registryResult, string exeName, string[] common)
    {
        foreach (var c in new[] { registryResult ?? "" }.Concat(common))
            if (RealExe(c, exeName)) return c.Trim('"', ' ');
        return null;
    }

    public static string? FindSunlogin() => RemoteConnectHelper.V2.Discovery.RemoteSoftwareDiscovery.Find("sunlogin")?.Path;
    private static string? FindSunloginLegacy()
    {
        var reg = FindByDisplayName(new[] { "向日葵", "Sunlogin" }, "AweSun.exe");
        string[] common =
        {
            @"D:\AweSun\AweSun.exe",
            @"D:\AweSun\agent\AweSun.exe",
            @"C:\Program Files\Oray\SunLogin\SunloginClient\AweSun.exe",
            @"C:\Program Files (x86)\Oray\SunLogin\SunloginClient\AweSun.exe",
        };
        return Pick(reg, "AweSun.exe", common);
    }

    public static string? FindToDesk() => RemoteConnectHelper.V2.Discovery.RemoteSoftwareDiscovery.Find("todesk")?.Path;
    private static string? FindToDeskLegacy()
    {
        var reg = FindByDisplayName(new[] { "ToDesk" }, "ToDesk.exe");
        string[] common =
        {
            @"C:\Program Files\ToDesk\ToDesk.exe",
            @"C:\Program Files (x86)\ToDesk\ToDesk.exe",
            @"D:\ToDesk\ToDesk.exe",
        };
        return Pick(reg, "ToDesk.exe", common);
    }
}
