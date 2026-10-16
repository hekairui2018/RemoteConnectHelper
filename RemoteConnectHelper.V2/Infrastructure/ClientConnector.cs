using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemoteConnectHelper.Automation;
using RemoteConnectHelper.V2.Domain;
using LegacyKind = RemoteConnectHelper.Recognition.RemoteAppKind;

namespace RemoteConnectHelper.V2.Infrastructure;

public sealed class ClientConnector
{
    public async Task<ConnectResult> ConnectAsync(RemoteApp app, string code, string password, CancellationToken token)
    {
        if (!ConnectionPolicy.ValidCode(app, code)) return ConnectResult.Fail("请填写有效的设备码");
        token.ThrowIfCancellationRequested();
        var kind = app == RemoteApp.Sunlogin ? LegacyKind.Sunlogin : LegacyKind.ToDesk;
        var context = new ConnectionContext(kind);
        var before = SessionWindows(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var registration = linked.Token.Register(() => { if (token.IsCancellationRequested) context.CancelByUser(); else context.Cancel(); });
        try
        {
            IRemoteConnector connector = app == RemoteApp.Sunlogin ? new SunloginConnector() : new ToDeskConnector();
            var result = await connector.ConnectAsync(code.Replace(" ", ""), password, context);
            if (token.IsCancellationRequested) return ConnectResult.Cancel("已停止");
            if (timeout.IsCancellationRequested) return ConnectResult.Fail("连接等待超时，请检查远程软件后重试");
            return result;
        }
        catch (OperationCanceledException)
        { return token.IsCancellationRequested ? ConnectResult.Cancel("已停止") : ConnectResult.Fail("连接超时，请检查后重试"); }
        finally
        {
            if (linked.IsCancellationRequested && context.ConnectClicked)
            {
                // 只关闭这次操作新增的会话/弹窗，不向其他前台程序发 Esc，不关闭原有会话。
                for (int i = 0; i < 3; i++)
                {
                    foreach (var window in SessionWindows(app).Except(before)) PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(180);
                }
            }
            context.Cts.Dispose();
        }
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static HashSet<IntPtr> SessionWindows(RemoteApp app)
    {
        var ids = Process.GetProcessesByName(app == RemoteApp.Sunlogin ? "AweSun" : "ToDesk").Select(p => p.Id).ToHashSet();
        var result = new HashSet<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint id);
            if (!ids.Contains((int)id) || !IsWindowVisible(window)) return true;
            var cls = new StringBuilder(256); GetClassName(window, cls, cls.Capacity);
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            bool session = app == RemoteApp.Sunlogin ? cls.ToString().Contains("FlutterMultiWindow")
                : cls.ToString().Contains("H-SMILE") && !title.ToString().Trim().Equals("ToDesk", StringComparison.OrdinalIgnoreCase);
            if (session) result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
