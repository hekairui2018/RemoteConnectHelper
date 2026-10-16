namespace RemoteConnectHelper.V2.Application;

/// <summary>替换请求先取消并等待前一请求彻底收尾，防止旧 OCR/旧输入覆盖新请求。</summary>
public sealed class OperationHost
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _source;
    private Task _active = Task.CompletedTask;

    public async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        Task running;
        await _gate.WaitAsync();
        try
        {
            _source?.Cancel();
            try { await _active; } catch (Exception) { /* 前一请求的错误由其调用者处理，新请求仍可重试。 */ }
            _source?.Dispose();
            _source = new();
            running = _active = ExecuteAsync(operation, _source.Token);
        }
        finally { _gate.Release(); }
        await running;
    }
    private static async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    { token.ThrowIfCancellationRequested(); await operation(token); }
    public void Cancel() { try { _source?.Cancel(); } catch (ObjectDisposedException) { } }
    public async Task StopAsync()
    {
        Cancel();
        await _gate.WaitAsync();
        try { Cancel(); try { await _active; } catch (Exception) { } }
        finally { _gate.Release(); }
    }
}
