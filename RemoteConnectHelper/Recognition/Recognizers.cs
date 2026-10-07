using System.Text.RegularExpressions;

namespace RemoteConnectHelper.Recognition;

public interface IRemoteAppRecognizer
{
    RemoteAppKind Kind { get; }
    bool Matches(OcrDoc doc);
    RecognizedResult Extract(OcrDoc doc);
}

/// <summary>向日葵：识别码 9~10 位，口令叫“验证码”（今日/隐藏/长期/伙伴验证码，可为空）。</summary>
public sealed class SunloginRecognizer : IRemoteAppRecognizer
{
    public RemoteAppKind Kind => RemoteAppKind.Sunlogin;

    // 品牌名（强）
    private static readonly string[] BrandStrong =
        { "向日葵", "sunlogin", "oray", "贝锐", "awesun", "简约版" };
    // 决定性术语：向日葵把被控口令叫“识别码 / 验证码”，这是与 ToDesk 的分水岭（不依赖颜色/布局）
    private static readonly string[] UiHints =
        { "识别码", "验证码", "远程协助" };

    public bool Matches(OcrDoc doc)
    {
        string t = doc.FlatText.ToLowerInvariant();
        if (BrandStrong.Any(h => t.Contains(h.ToLowerInvariant()))) return true;
        return UiHints.Any(h => doc.FlatText.Contains(h));
    }

    public RecognizedResult Extract(OcrDoc doc)
    {
        var r = new RecognizedResult { Kind = RemoteAppKind.Sunlogin };
        var hits = CodeExtractor.FindDeviceCodes(doc, 9, 10);
        var target = CodeExtractor.PickTargetCode(doc, hits, out var cands);
        r.CandidateCodes = cands;

        if (target == null)
        {
            r.Error = cands.Count > 1
                ? "识别到多个识别码，请在卡片上选择正确的一个"
                : "没识别到向日葵识别码（应为 9~10 位数字）";
            return r;
        }

        r.DeviceCode = target.Code;
        r.Password = CodeExtractor.FindPassword(doc, CodeExtractor.SunloginPwdHints, new[] { r.DeviceCode });
        return r;
    }
}

/// <summary>ToDesk：设备码 9 位，叫“设备代码”，口令叫“安全/临时密码、安全代码”，两步流程。</summary>
public sealed class ToDeskRecognizer : IRemoteAppRecognizer
{
    public RemoteAppKind Kind => RemoteAppKind.ToDesk;

    // 决定性术语：ToDesk 叫“设备代码 / 安全密码 / 安全代码 / 临时密码”。
    // 注意：“允许控制本设备 / 远程控制设备 / 远程桌面 / 远程文件”是两家共有中性词，不得用来定品牌；
    // “今日验证码 / 验证码（可为空）”是向日葵术语，也不能算 ToDesk。
    private static readonly string[] UiHints =
        { "设备代码", "安全密码", "安全代码", "临时密码" };

    public bool Matches(OcrDoc doc)
    {
        string t = doc.FlatText.ToLowerInvariant();
        if (t.Contains("todesk")) return true;
        return UiHints.Any(h => doc.FlatText.Contains(h));
    }

    public RecognizedResult Extract(OcrDoc doc)
    {
        var r = new RecognizedResult { Kind = RemoteAppKind.ToDesk };
        var hits = CodeExtractor.FindDeviceCodes(doc, 9, 9);
        var target = CodeExtractor.PickTargetCode(doc, hits, out var cands);
        r.CandidateCodes = cands;

        if (target == null)
        {
            r.Error = cands.Count > 1
                ? "识别到多个设备码，请在卡片上选择正确的一个"
                : "没识别到 ToDesk 设备码（应为 9 位数字）";
            return r;
        }

        r.DeviceCode = target.Code;
        r.Password = CodeExtractor.FindPassword(doc, CodeExtractor.ToDeskPwdHints, new[] { r.DeviceCode });
        return r;
    }
}

/// <summary>识别调度：用“品牌名 + 决定性术语”定品牌，不依赖界面颜色/深浅（向日葵也有白色简约版、ToDesk 也有白底）。</summary>
public sealed class RecognizerEngine
{
    private static readonly string[] SunBrandNames =
        { "向日葵", "sunlogin", "oray", "贝锐", "awesun", "简约版" };

    private readonly SunloginRecognizer _sun = new();
    private readonly ToDeskRecognizer _td = new();

    public RecognizedResult Run(OcrDoc doc)
    {
        string f = doc.FlatText;
        string t = f.ToLowerInvariant();

        // 明文品牌名
        bool tdName = t.Contains("todesk");
        bool sunName = SunBrandNames.Any(x => t.Contains(x.ToLowerInvariant()));

        // 决定性术语（互不重叠）
        bool tdTerm = f.Contains("设备代码") || f.Contains("安全密码")
                   || f.Contains("安全代码") || f.Contains("临时密码");
        bool sunTerm = f.Contains("识别码") || f.Contains("验证码") || f.Contains("远程协助");

        bool td = tdName || tdTerm;
        bool sun = sunName || sunTerm;

        // 1) 只有一家有决定性证据 → 直连
        if (td && !sun) return Confident(_td.Extract(doc), true);
        if (sun && !td) return Confident(_sun.Extract(doc), true);

        // 2) 两家决定性词同屏（罕见，如同时截到两个软件/聊天里两家都提）：明文品牌名定胜负
        if (td && sun)
        {
            if (tdName && !sunName) return Confident(_td.Extract(doc), true);
            if (sunName && !tdName) return Confident(_sun.Extract(doc), true);
            // 都无明文（或都有）：认到码也弹气泡，让客服人工定软件，绝不猜着直连
            return Confident(_sun.Extract(doc), false);
        }

        // 3) 两家决定性词都没识出来（拍屏摩尔纹把“识别码/设备代码”整词碎掉），
        //    只剩“允许控制本设备/远程控制设备”等中性词 + 码。售后绝大多数为向日葵，默认向日葵：
        var sr = _sun.Extract(doc);
        if (sr.Kind != RemoteAppKind.Unknown && !string.IsNullOrEmpty(sr.DeviceCode))
        {
            // 弱验证码残字信号：OCR 把“今日验证码/验证码（可为空）”碎成单字“证”，且配的是含字母验证码、
            // 同时全文无任何 ToDesk 决定性词 —— 足以排除 ToDesk（ToDesk 用“密码”不用“验证码”），直连向日葵。
            bool verifyRelic = f.Contains("证") && sr.HasPassword
                            && Regex.IsMatch(sr.Password ?? "", "[A-Za-z]");
            sr.BrandConfident = verifyRelic;
            return sr;
        }

        // 4) 连码都没有 → 手动
        return new RecognizedResult
        {
            Kind = RemoteAppKind.Unknown,
            BrandConfident = true,
            Error = "没识别到任何远程软件信息，请手动填写",
        };
    }

    private static RecognizedResult Confident(RecognizedResult r, bool confident)
    {
        r.BrandConfident = confident;
        return r;
    }
}
