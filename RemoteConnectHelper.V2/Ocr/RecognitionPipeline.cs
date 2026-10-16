using System.Security.Cryptography;
using RemoteConnectHelper.V2.Domain;
using RemoteConnectHelper.V2.Parsing;

namespace RemoteConnectHelper.V2.Ocr;

public sealed class RecognitionPipeline : IDisposable
{
    private readonly IOcrProvider _primary;
    private readonly IOcrProvider _fallback;
    private readonly RecognitionEngine _parser = new();
    private string? _cacheKey;
    private RecognitionResult? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public RecognitionPipeline(IOcrProvider? primary = null, IOcrProvider? fallback = null)
    { _primary = primary ?? new RapidOcrProvider(); _fallback = fallback ?? new WindowsOcrProvider(); }

    public async Task<RecognitionResult> AnalyzeAsync(byte[] image, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        string key = Convert.ToHexString(SHA256.HashData(image));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (key == _cacheKey && _cached != null) { progress?.Invoke("读取同一张截图的识别结果"); return _cached; }
            var attempts = new List<RecognitionResult>();
            Exception? primaryFailure = null;
            foreach (int rotation in new[] { 0, 90, 270, 180 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(rotation == 0 ? "正在读取图片中的文字…" : "正在检查照片方向…");
                try
                {
                    var result = _parser.Analyze(await _primary.ReadAsync(image, rotation, false, cancellationToken));
                    attempts.Add(result);
                    if (result.SelectedCode != null && result.App != RemoteApp.Unknown) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { primaryFailure = ex; Infrastructure.Log.Write("主 OCR 不可用：" + ex.GetType().Name); break; }
            }
            var best = Best(attempts);
            if (best?.SelectedCode == null)
            {
                progress?.Invoke("正在增强模糊文字…");
                try { attempts.Add(_parser.Analyze(await _primary.ReadAsync(image, 0, true, cancellationToken))); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (primaryFailure != null) { }
                best = Best(attempts);
            }
            if (best?.SelectedCode == null || primaryFailure != null)
            {
                progress?.Invoke("正在用备用识别方式核对…");
                foreach (int rotation in new[] { 0, 90, 270, 180 })
                {
                    attempts.Add(_parser.Analyze(await _fallback.ReadAsync(image, rotation, false, cancellationToken)));
                    best = Best(attempts);
                    if (best?.SelectedCode != null && best.App != RemoteApp.Unknown) break;
                }
            }
            best = Best(attempts) ?? throw new InvalidOperationException("没有可用的 OCR 结果");
            cancellationToken.ThrowIfCancellationRequested();
            _cacheKey = key; _cached = best;
            return best;
        }
        finally { _gate.Release(); }
    }
    private static RecognitionResult? Best(IEnumerable<RecognitionResult> results)
        => results.OrderByDescending(r => (r.SelectedCode == null ? 0 : 1000) + (r.App == RemoteApp.Unknown ? 0 : 200)
            + (r.SelectedCode?.RuleScore ?? 0) + (r.SelectedPassword?.RuleScore ?? 0) * .25
            // 同等语义证据优先原方向，避免角度分类器翻转文字后词框顺序反转。
            + (r.Document.Rotation == 0 ? 20 : 0)).FirstOrDefault();
    public void Dispose() { _primary.Dispose(); _fallback.Dispose(); _gate.Dispose(); }
}
