using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace RemoteConnectHelper.Automation;

/// <summary>一个远程软件的主窗口句柄 + UIA 根。</summary>
public sealed class AppWindow
{
    public Process Process { get; init; } = null!;
    public IntPtr Handle { get; init; }
    public AutomationElement Root => AutomationElement.FromHandle(Handle);

    public string ClassName => UIAFinder.GetClassName(Handle);

    public AutomationElement? FindEditByName(string name, bool contains = false)
        => UIAFinder.FindByControlAndName(Root, ControlType.Edit, name, contains);

    public AutomationElement? FindTextByName(string name, bool contains = false)
        => UIAFinder.FindByControlAndName(Root, ControlType.Text, name, contains);

    public AutomationElement? FindButtonByName(string name, bool contains = false)
        => UIAFinder.FindByControlAndName(Root, ControlType.Button, name, contains);

    public List<AutomationElement> FindAllEdits()
    {
        var list = Root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        return list.Cast<AutomationElement>().ToList();
    }

    public List<AutomationElement> FindAllText()
    {
        var list = Root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        return list.Cast<AutomationElement>().ToList();
    }

    public static Rect RectOf(AutomationElement el)
    {
        try { return el.Current.BoundingRectangle; }
        catch { return Rect.Empty; }
    }
}

public static class UIAFinder
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hWnd, sb, 256);
        return sb.ToString();
    }

    /// <summary>找已运行的、主窗口 ClassName 匹配任一候选的进程窗口。</summary>
    public static AppWindow? FindRunning(string[] processNames, string[] classNames)
    {
        foreach (var pname in processNames)
        {
            foreach (var p in Process.GetProcessesByName(pname))
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    string cls = GetClassName(p.MainWindowHandle);
                    if (classNames.Length == 0 || classNames.Any(c => cls.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                        return new AppWindow { Process = p, Handle = p.MainWindowHandle };
                }
                catch { }
            }
        }
        // 兜底：ClassName 不匹配也返回第一个有窗口的（软件改版）
        foreach (var pname in processNames)
        {
            foreach (var p in Process.GetProcessesByName(pname))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    return new AppWindow { Process = p, Handle = p.MainWindowHandle };
            }
        }
        return null;
    }

    /// <summary>启动 exe 并等待主窗口出现。</summary>
    public static AppWindow? LaunchAndWait(string exePath, string[] processNames,
                                           string[] classNames, int timeoutMs = 12000)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath),
            });
        }
        catch (Exception ex)
        {
            Services.Logger.Error($"启动失败 {exePath}", ex);
            return null;
        }

        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            Thread.Sleep(500);
            var w = FindRunning(processNames, classNames);
            if (w != null) return w;
        }
        return null;
    }

    /// <summary>已运行则复用，否则启动。</summary>
    public static AppWindow? EnsureRunning(string[] exePaths, string[] processNames, string[] classNames)
    {
        var existing = FindRunning(processNames, classNames);
        if (existing != null) return existing;

        foreach (var exe in exePaths)
        {
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var w = LaunchAndWait(exe, processNames, classNames);
                if (w != null) return w;
            }
        }
        return null;
    }

    public static AutomationElement? FindByControlAndName(AutomationElement root,
        ControlType type, string name, bool contains)
    {
        var els = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        foreach (AutomationElement el in els)
        {
            string n;
            try { n = el.Current.Name ?? ""; }
            catch { continue; }
            if (contains ? n.Contains(name) : n == name)
                return el;
        }
        return null;
    }

    /// <summary>等待某个具名元素出现（软件启动/界面切换有延迟）。</summary>
    public static AutomationElement? WaitFor(Func<AutomationElement?> finder, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            try
            {
                var el = finder();
                if (el != null) return el;
            }
            catch { }
            Thread.Sleep(300);
        }
        return null;
    }
}
