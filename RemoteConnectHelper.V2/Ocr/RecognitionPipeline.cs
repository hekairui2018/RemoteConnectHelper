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
    { _primary = primary ?? new IsolatedRapidOcrProvider(); _fallback = fallback ?? new WindowsOcrProvider(); }

    public Task<int> DetectOrientationAsync(byte[] image,CancellationToken token)
        => _primary switch{IsolatedRapidOcrProvider isolated=>isolated.DetectOrientationAsync(image,token),RapidOcrProvider rapid=>rapid.DetectOrientationAsync(image,token),_=>Task.FromResult(0)};

    public async Task<RecognitionResult> AnalyzeAsync(byte[] image, CancellationToken cancellationToken,
        Action<string>? progress = null, bool remoteOnly = false)
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
                    if(remoteOnly && rotation==0 && !LooksRemote(result))
                        return result with{Decision=ConnectionDecision.Manual,Reason="未发现明确远程连接内容"};
                    if (result.SelectedCode != null && result.App != RemoteApp.Unknown) break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { primaryFailure = ex; Infrastructure.Log.Write("主 OCR 不可用：" + ex.GetType().Name); break; }
            }
            var best = Best(attempts);
            bool risky = best?.SelectedCode != null && (best.App == RemoteApp.Unknown
                || ConfidenceEngine.Risks(best.SelectedCode, best.DeviceCodes, best.SelectedPassword).Count > 0
                || ConfidenceEngine.NeedsPasswordCheck(best.SelectedPassword));
            var crossCheckIssues = new List<string>();
            if (best?.SelectedCode == null || risky)
            {
                progress?.Invoke("正在增强模糊文字…");
                try { attempts.Add(_parser.Analyze(await _primary.ReadAsync(image, best?.Document.Rotation ?? 0, true, cancellationToken))); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { crossCheckIssues.Add("增强识别不可用：" + ex.GetType().Name); }
                best = Best(attempts);
            }
            if (best?.SelectedCode == null || primaryFailure != null || risky)
            {
                progress?.Invoke("正在用备用识别方式核对…");
                // 已有目标时只核对相同方向；无目标时保留原有 Windows 多方向兜底。
                var rotations = best?.SelectedCode != null ? new[] { best.Document.Rotation } : new[] { 0, 90, 270, 180 };
                foreach (int rotation in rotations)
                {
                    try { attempts.Add(_parser.Analyze(await _fallback.ReadAsync(image, rotation, false, cancellationToken))); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { crossCheckIssues.Add("备用识别不可用：" + ex.GetType().Name); break; }
                    best = Best(attempts);
                    if (best?.SelectedCode != null && best.App != RemoteApp.Unknown) break;
                }
            }
            best = Best(attempts) ?? throw new InvalidOperationException("没有可用的 OCR 结果");
            var validAttempts = attempts.Where(r => r.SelectedCode != null).ToList();
            // 两种引擎对同一位置读到完全一致的字段，可作为人工核对首选；不是按分数猜字。
            // 保留完整原始尝试；下面即使有共识也不会取消矛盾导致的人工确认。
            var consensus = validAttempts.Where(r => r.Password.Length > 0 && r.App != RemoteApp.Unknown)
                .GroupBy(r => (r.App, r.DeviceCode, r.Password))
                .Where(g => g.Select(r => r.Document.Engine).Distinct().Count() >= 2
                    && g.Any(a => g.Any(b => a.Document.Engine != b.Document.Engine && Overlaps(a.SelectedCode!.Bounds, b.SelectedCode!.Bounds))))
                .ToArray();
            bool selectedByAgreement = consensus.Length == 1;
            if (selectedByAgreement) best = consensus[0].First();
            var competing = validAttempts.Where(r => !IsUnlabelledRotationBackground(r, best)).ToList();
            if (competing.Any(r => r.DeviceCode != best.DeviceCode)) crossCheckIssues.Add("多次 OCR 的目标设备码不一致，请核对原图");
            if (validAttempts.Any(r => r.DeviceCode == best.DeviceCode && ConfidenceEngine.Risks(r.SelectedCode, r.DeviceCodes, r.SelectedPassword)
                .Any(s => s.Contains("得分接近")))) crossCheckIssues.Add("原始 OCR 存在接近的竞争目标，仍需选择设备");
            if (validAttempts.Any(r => r.DeviceCode == best.DeviceCode && r.App != RemoteApp.Unknown && best.App != RemoteApp.Unknown && r.App != best.App))
                crossCheckIssues.Add("多次 OCR 的软件判断不一致，请选择软件");
            if (validAttempts.Any(r => r.DeviceCode == best.DeviceCode && r.Password.Length > 0 && best.Password.Length > 0 && r.Password != best.Password))
                crossCheckIssues.Add("多次 OCR 的密码不一致，请核对原图");
            bool independentPasswordConfirmation = validAttempts.Any(r => r.Document.Engine != best.Document.Engine
                && r.DeviceCode == best.DeviceCode && r.Password == best.Password && best.Password.Length > 0);
            if (ConfidenceEngine.NeedsPasswordCheck(best.SelectedPassword) && !independentPasswordConfirmation)
                crossCheckIssues.Add("密码包含易混或低置信度字符，备用 OCR 未能独立确认，请核对");
            var decisionIssues = crossCheckIssues.Where(s => !s.Contains("不可用")).ToList();
            best = best with
            {
                Attempts = attempts.Select(r => new OcrAttempt(r.Document, r.DeviceCodes, r.Passwords, r.SelectedCode, r.SelectedPassword)).ToArray(),
                CrossCheckIssues = crossCheckIssues,
                Warnings = best.Warnings.Concat(crossCheckIssues)
                    .Concat(selectedByAgreement ? new[] { "当前字段来自两种引擎一致的原始候选；存在其他矛盾时仍须人工核对" } : [])
                    .Concat(competing.Count != validAttempts.Count ? new[] { "转向 OCR 在其他区域读到无软件或标签依据的数字，保留为背景证据" } : []).ToArray(),
                Decision = decisionIssues.Count > 0 ? ConnectionDecision.Manual : best.Decision,
                Reason = decisionIssues.Count > 0 ? string.Join("；", decisionIssues) : best.Reason
            };
            cancellationToken.ThrowIfCancellationRequested();
            _cacheKey = key; _cached = best;
            return best;
        }
        finally { _gate.Release(); }
    }
    private static RecognitionResult? Best(IEnumerable<RecognitionResult> results)
        // 不跨 OCR 按规则分数覆盖原始识别。优先有软件依据的结果，再保持尝试顺序。
        // 即使选择了较完整的结果，后面的矛盾检查仍会强制人工确认。
        => results.OrderByDescending(r => r.SelectedCode == null ? 0 : r.App == RemoteApp.Unknown ? 1 : 2).FirstOrDefault();
    public static bool LooksRemote(RecognitionResult result)
    {
        string words=string.Concat(result.Document.Blocks.Select(b=>b.Text));
        bool brand=System.Text.RegularExpressions.Regex.IsMatch(words,@"向日葵|Sunlogin|AweSun|(?<![a-z])ToDesk(?![a-z])",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool label=System.Text.RegularExpressions.Regex.IsMatch(words,@"本机识别码|设备代码|设备码|识别码|验证码|临时密码");
        return result.SelectedCode!=null && result.App!=RemoteApp.Unknown
            || result.SelectedCode!=null && brand
            || brand && label;
    }
    private static bool Overlaps(Box a, Box b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
    private static bool IsUnlabelledRotationBackground(RecognitionResult other, RecognitionResult selected)
        => other.SelectedCode != null && selected.SelectedCode != null
            && other.Document.Engine == selected.Document.Engine && other.Document.Rotation != selected.Document.Rotation
            && other.App == RemoteApp.Unknown && selected.App != RemoteApp.Unknown
            && !other.SelectedCode.Evidence.Any(e => e.Contains("标签") || e.Contains("客户本机"))
            && !Overlaps(other.SelectedCode.Bounds, selected.SelectedCode.Bounds);
    public void Dispose() { _primary.Dispose(); _fallback.Dispose(); _gate.Dispose(); }
}
