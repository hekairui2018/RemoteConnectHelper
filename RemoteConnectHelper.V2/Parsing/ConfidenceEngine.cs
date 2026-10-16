using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Parsing;

public sealed class ConfidenceEngine
{
    public RecognitionResult Decide(OcrDocument document, IReadOnlyList<SoftwareCandidate> software,
        IReadOnlyList<DeviceCodeCandidate> codes, IReadOnlyList<PasswordCandidate> passwords)
    {
        var code = codes.FirstOrDefault();
        var password = passwords.Where(p => p.DeviceCandidateId == code?.Id).OrderByDescending(p => p.RuleScore).FirstOrDefault();
        var warnings = new List<string>();
        if (password == null && document.Blocks.Any(b => b.Text.Contains('•') || b.Text.Contains('●')))
            warnings.Add("原图中的密码已隐藏，连接后可能需要补填");
        if (password != null && password.OcrConfidence is < .75f) warnings.Add("密码有模糊字符，失败后可对照原图修改");
        bool automatic = code != null && ConnectionPolicy.CanConnect(code.App, code.Value);
        string reason = code == null ? "没有读到完整设备码，请手动输入"
            : code.App == RemoteApp.Unknown ? "读到了设备码，尚不能确定软件，请选择"
            : automatic ? "软件及设备码已确定，自动发起连接" : "设备码格式不符合软件要求";
        return new(document, software, codes, passwords, code, password,
            automatic ? ConnectionDecision.Automatic : ConnectionDecision.Manual, reason, warnings);
    }
}
