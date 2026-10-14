using System.Threading;
using RemoteConnectHelper.Recognition;

namespace RemoteConnectHelper.Automation;

/// <summary>
/// 一次自动连接的上下文：承载取消令牌、是否已经点下"连接"（越过不可撤回点）。
/// 确认气泡卡上的「停止」在点连接前可干净中止；点下连接后语义变为"断开"（尽力关闭新弹出的会话窗）。
/// </summary>
public sealed class ConnectionContext
{
    public RemoteAppKind Kind { get; }
    public CancellationTokenSource Cts { get; } = new();
    public CancellationToken Token => Cts.Token;

    private volatile bool _connectClicked;
    /// <summary>是否已经点下远程软件的"连接"按钮（true 后取消=断开，而非中止）。</summary>
    public bool ConnectClicked => _connectClicked;

    private volatile bool _userCancelled;
    /// <summary>是否为用户在确认气泡卡上主动停止（区别于 35s 看门狗的超时取消）。</summary>
    public bool UserCancelled => _userCancelled;

    public ConnectionContext(RemoteAppKind kind) => Kind = kind;

    /// <summary>用户主动取消：打标记后再触发令牌。终态据此统一显示"已取消"，即使连接器返回了 Fail。</summary>
    public void CancelByUser()
    {
        _userCancelled = true;
        Cancel();
    }

    public void Cancel()
    {
        try { if (!Cts.IsCancellationRequested) Cts.Cancel(); } catch { /* disposed */ }
    }

    /// <summary>越过不可撤回点：调用后"取消"按断开处理。</summary>
    public void MarkConnectClicked() => _connectClicked = true;

    public void ThrowIfCancellationRequested() => Token.ThrowIfCancellationRequested();

    /// <summary>
    /// 可被取消即时中断的等待。返回 false 表示等待期间被取消；true 表示等满。
    /// 切成 100ms 小片，保证取消响应及时，不再有动辄数秒的死等。
    /// </summary>
    public bool Sleep(int ms)
    {
        if (Token.IsCancellationRequested) return false;
        int remaining = ms;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, 100);
            if (Token.WaitHandle.WaitOne(chunk)) return false;
            remaining -= chunk;
        }
        return !Token.IsCancellationRequested;
    }
}
