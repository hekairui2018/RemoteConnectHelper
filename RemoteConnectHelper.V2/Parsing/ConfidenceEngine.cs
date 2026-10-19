using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Parsing;

public sealed class ConfidenceEngine
{
    // Experimental rule thresholds, never interpreted as calibrated probabilities.
    public const float CodeConfidenceFloor = .75f;
    public const float CharacterConfidenceFloor = .50f;
    public const double CandidateMargin = 12;

    public static IReadOnlyList<string> Risks(DeviceCodeCandidate? code, IReadOnlyList<DeviceCodeCandidate> codes,
        PasswordCandidate? password)
    {
        var risks = new List<string>();
        if (code == null) return risks;
        if (code.HadCharacterConversion) risks.Add("设备码包含形近字转换，请核对原文");
        if (code.OcrConfidence is < CodeConfidenceFloor || code.MinimumOcrConfidence is < CharacterConfidenceFloor)
            risks.Add("设备码字符识别不可靠，请核对");
        if (codes.Any(c => (c.Value != code.Value || c.App != code.App) && code.RankingScore - c.RankingScore < CandidateMargin))
            risks.Add("存在得分接近的不同设备码，请选择目标");
        if (password?.HadCharacterConversion == true) risks.Add("密码包含字符转换，请核对原文");
        return risks;
    }

    public static bool NeedsPasswordCheck(PasswordCandidate? password) => password != null &&
        (password.OcrConfidence is < .75f || password.MinimumOcrConfidence is < .50f
            || password.HadCharacterConversion || password.Value.Any(c => c is 'O' or 'o' or 'I' or 'l' or '|'));

    public RecognitionResult Decide(OcrDocument document, IReadOnlyList<SoftwareCandidate> software,
        IReadOnlyList<DeviceCodeCandidate> codes, IReadOnlyList<PasswordCandidate> passwords)
    {
        var code = codes.FirstOrDefault();
        var password = passwords.Where(p => p.DeviceCandidateId == code?.Id).OrderByDescending(p => p.RuleScore).FirstOrDefault();
        var warnings = new List<string>();
        if (password == null)
            warnings.Add(document.Blocks.Any(b => b.Text.Contains('•') || b.Text.Contains('●'))
                ? "原图中的密码已隐藏，允许尝试连接，需要时补填" : "未读到可见密码，允许尝试连接，需要时补填");
        if (password != null && password.OcrConfidence is < .75f) warnings.Add("密码有模糊字符，失败后可对照原图修改");
        var risks = Risks(code, codes, password);
        warnings.AddRange(risks);
        bool automatic = code != null && ConnectionPolicy.CanConnect(code.App, code.Value) && risks.Count == 0;
        string reason = code == null ? "没有读到完整设备码，请手动输入"
            : code.App == RemoteApp.Unknown ? "读到了设备码，尚不能确定软件，请选择"
            : risks.Count > 0 ? string.Join("；", risks)
            : automatic ? "软件及设备码已确定，自动发起连接" : "设备码格式不符合软件要求";
        return new(document, software, codes, passwords, code, password,
            automatic ? ConnectionDecision.Automatic : ConnectionDecision.Manual, reason, warnings);
    }
}
