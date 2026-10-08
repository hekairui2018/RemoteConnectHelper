using System.Text;

namespace RemoteConnectHelper.Recognition;

/// <summary>
/// 识别结果三问：Q1 设备码（9~10 位数字，界面“本机/此设备”区优先于输入框残留）；
/// Q2 密码（设备码之外含字母短串或 4~8 位纯数字，找不到=免密）；Q3 品牌（只看强信号，不看颜色）。
/// 三种结果：A 铁证+有码=直连（状态小条不打断）；B 无品牌强信号+有码=弹软件选择卡（默认向日葵，人工二选一）；
/// C 无码=弹卡手填。绝不因“证/密”等单字或界面颜色替客服赌是哪家。
/// </summary>
/// <summary>判断 OCR 文本是否呈“正立可读”方向，用于旋转（90/180/270）兜底时挑选正确方向。
/// 画面倒置/侧置时，中文会被 Windows OCR 读成形近乱码单字（真机实测“向日葵简约版/验证码”
/// 倒置后变成“币觏韭襖但刁山”这类孤立单字），不可能拼对下列品牌词或界面词；正立时至少命中其一。
/// 注意：纯数字/纯字母聊天截图本身没有中文，本信号为 false，由调用方回退尊重原图方向，不据此改判。</summary>
public static class OcrOrientation
{
    // 两家品牌词 + 远程软件常见中文界面词/邀请话术词；只用 2 字以上词，避免乱码单字偶然命中。
    private static readonly string[] Anchors =
    {
        "向日葵", "贝锐", "简约版", "sunlogin", "oray", "awesun", "todesk",
        "识别码", "验证码", "远程协助", "设备代码", "设备码", "安全密码", "安全代码", "临时密码",
        "临时码", "连接模式", "协作模式", "观看模式", "端到端加密", "二次验证", "允许控制",
        "投屏演示", "互传文件", "应用中心", "返回旧版", "团队版", "全球节点", "控制本设备",
        "远程控制", "邀请", "下载", "伙伴", "此设备", "连接", "设置", "密码", "设备", "远程"
    };

    public static bool HasUprightSignal(OcrDoc? doc)
    {
        if (doc == null || string.IsNullOrEmpty(doc.FlatText)) return false;
        var f = doc.FlatText;
        foreach (var a in Anchors)
            if (f.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public interface IRecognizer
{
    RemoteAppKind Kind { get; }
    RecognizedResult Extract(OcrDoc doc);
}

public class SunloginRecognizer : IRecognizer
{
    public RemoteAppKind Kind => RemoteAppKind.Sunlogin;

    // 品牌名/logo 文字（强信号）。“简约版”是向日葵白色简约客户端的标题字样。
    private static readonly string[] BrandStrong =
        { "向日葵", "sunlogin", "oray", "贝锐", "awesun", "简约版" };
    // 向日葵专属界面词（强信号）。
    private static readonly string[] UiHints = { "识别码", "验证码", "远程协助" };

    public RecognizedResult Extract(OcrDoc doc)
    {
        var hits = CodeExtractor.FindDeviceCodes(doc, 9, 10);
        var cand = hits.Select(h => h.Code).Distinct().ToList();
        // 先取密码，再用“密码紧邻哪个码”锚定目标设备码（兼容客服端已填表单 vs 客户本机首页）
        var pwd = CodeExtractor.FindPassword(doc, CodeExtractor.SunloginPwdHints, cand, out var pwdAlts);
        var hit = CodeExtractor.PickTargetCode(doc, hits, pwd, out var candidates);
        return new RecognizedResult
        {
            Kind = RemoteAppKind.Sunlogin,
            DeviceCode = hit?.Code ?? "",
            CandidateCodes = candidates,
            Password = pwd,
            CandidatePasswords = pwdAlts
        };
    }

    public IEnumerable<string> BrandHits(string flat) => BrandStrong.Where(flat.Contains);
    public IEnumerable<string> UiHits(string flat) => UiHints.Where(flat.Contains);
}

public class ToDeskRecognizer : IRecognizer
{
    public RemoteAppKind Kind => RemoteAppKind.ToDesk;

    // ToDesk 专属界面词（强信号）。ToDesk 客户端没有“验证码”说法，对应物叫安全/临时密码。
    // 新版客户端（深色/浅色）把临时密码叫“临时码”，并有连接模式/协作模式/观看模式/端到端加密/
    // 允许控制本设备/二次验证等专属界面词；左下角品牌 logo 小字常漏识，靠这些词同样能锁定 ToDesk。
    private static readonly string[] UiHints =
    {
        // 旧版/通用术语
        "设备代码", "安全密码", "安全代码", "临时密码",
        // 新版（V8 深色/浅色同款）本机码区与连接模式
        "临时码", "连接模式", "协作模式", "观看模式", "端到端加密", "二次验证", "允许控制本设备",
        // 新版左侧导航 / 版本 / 广告位专属词（向日葵主界面无这些字样）
        "投屏演示", "互传文件", "应用中心", "返回旧版", "团队版", "全球节点", "控制本设备"
    };

    public RecognizedResult Extract(OcrDoc doc)
    {
        var hits = CodeExtractor.FindDeviceCodes(doc, 9, 9);
        var cand = hits.Select(h => h.Code).Distinct().ToList();
        var rawPwd = CodeExtractor.FindPassword(doc, CodeExtractor.ToDeskPwdHints, cand, out var rawPwdAlts);
        var pwd = NormalizeTodeskTempPassword(rawPwd);
        var hit = CodeExtractor.PickTargetCode(doc, hits, pwd, out var candidates);
        return new RecognizedResult
        {
            Kind = RemoteAppKind.ToDesk,
            DeviceCode = hit?.Code ?? "",
            CandidateCodes = candidates,
            Password = pwd,
            // 候选密码同样按“临时密码无大写”归一（OCR 常把数字 0 认成大写 O）
            CandidatePasswords = rawPwdAlts
                .Select(NormalizeTodeskTempPassword)
                .Where(a => a.Length >= 4 && a != pwd)
                .Distinct()
                .Take(3)
                .ToList()
        };
    }

    // ToDesk 主界面能截到的密码只有系统随机【临时密码】（用户自设的安全密码不在主界面显示），
    // 真机样本（c25dzwm9 / ub14eq0r）均为 8 位小写字母+数字、不含大写。OCR 常把数字认成大写形似
    // 字母（数字 0→大写 O 最常见，真机 ub14eq0r 被认成 ub14eqOr 导致密码错误）。按"临时密码不该有
    // 大写"归并：大写形似字母→对应数字，其余大写→小写；小写 o/l 等真歧义保留不动，交给失败弹卡人工改。
    // 向日葵验证码本身含大小写，绝不能走这套，故只在 ToDesk 识别里应用。
    private static string NormalizeTodeskTempPassword(string? pwd)
    {
        if (string.IsNullOrEmpty(pwd)) return "";
        var sb = new StringBuilder();
        foreach (char ch in pwd)
        {
            if (char.IsUpper(ch))
            {
                char m = ch switch
                {
                    'O' => '0',
                    'I' or 'L' => '1',
                    'S' => '5',
                    'Z' => '2',
                    'B' => '8',
                    'G' => '6',
                    _ => char.ToLowerInvariant(ch)
                };
                sb.Append(m);
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    public bool BrandNameHit(string flat) => flat.Contains("todesk");
    public IEnumerable<string> UiHits(string flat) => UiHints.Where(flat.Contains);
}

/// <summary>
/// 仲裁：只认强信号（品牌名/logo 文字、各家专属界面词），不看界面深浅色（向日葵也有白色简约版）。
/// 两家强信号都没有时，哪怕有码也 BrandConfident=false → 弹软件选择卡，绝不直连。
/// </summary>
public class RecognizerEngine
{
    private readonly SunloginRecognizer _sun = new();
    private readonly ToDeskRecognizer _td = new();

    public RecognizedResult Run(OcrDoc doc)
    {
        // 品牌名统一转小写再匹配：OCR 常输出首字母大写的 “ToDesk”，Contains("todesk") 必须大小写不敏感
        string f = doc.FlatText.ToLowerInvariant();

        bool sunName = _sun.BrandHits(f).Any();
        bool tdName = _td.BrandNameHit(f);
        bool sunTerm = _sun.UiHits(f).Any();
        bool tdTerm = _td.UiHits(f).Any();

        var sunReasons = _sun.BrandHits(f).Select(w => $"品牌:{w}")
            .Concat(_sun.UiHits(f).Select(w => $"术语:{w}")).ToList();
        var tdReasons = (tdName ? new[] { "品牌:todesk" } : Array.Empty<string>())
            .Concat(_td.UiHits(f).Select(w => $"术语:{w}")).ToList();

        bool sunStrong = sunName || sunTerm;
        bool tdStrong = tdName || tdTerm;

        // ① 只有一家强信号 → 铁证直连
        if (sunStrong && !tdStrong)
        {
            var r = _sun.Extract(doc);
            r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
            r.BrandReason = "向日葵铁证[" + string.Join(",", sunReasons) + "]";
            return Finalize(r);
        }
        if (tdStrong && !sunStrong)
        {
            var r = _td.Extract(doc);
            r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
            r.BrandReason = "ToDesk铁证[" + string.Join(",", tdReasons) + "]";
            return Finalize(r);
        }

        // ② 两家强信号同屏：明文品牌名定胜负；都只是术语/都有品牌名 → 不赌，弹卡
        if (sunStrong && tdStrong)
        {
            if (sunName && !tdName)
            {
                var r = _sun.Extract(doc);
                r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
                r.BrandReason = $"同屏冲突,明文品牌向日葵(向日葵[{string.Join(",", sunReasons)}] ToDesk[{string.Join(",", tdReasons)}])";
                return Finalize(r);
            }
            if (tdName && !sunName)
            {
                var r = _td.Extract(doc);
                r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
                r.BrandReason = $"同屏冲突,明文品牌ToDesk(向日葵[{string.Join(",", sunReasons)}] ToDesk[{string.Join(",", tdReasons)}])";
                return Finalize(r);
            }
            var amb = _sun.Extract(doc);
            amb.BrandConfident = false;
            amb.BrandReason = $"两家信号同屏(向日葵[{string.Join(",", sunReasons)}] ToDesk[{string.Join(",", tdReasons)}]),人工选软件";
            return Finalize(amb);
        }

        // ③ 两家强信号都没有：哪怕认到码也不赌，默认向日葵、弹软件选择卡让客服点一下
        var guessed = _sun.Extract(doc);
        if (!string.IsNullOrEmpty(guessed.DeviceCode))
        {
            guessed.Kind = RemoteAppKind.Sunlogin; // 卡片默认高亮向日葵，回车即连
            guessed.BrandConfident = false;
            guessed.BrandReason = "无品牌强信号,默认向日葵待人工确认";
            return Finalize(guessed);
        }

        // ④ 连码都没有：弹卡手填
        return Finalize(new RecognizedResult
        {
            Kind = RemoteAppKind.Unknown,
            BrandConfident = false,
            Error = "没认出设备码，请在卡片上手动填写",
            BrandReason = "未识别到设备码"
        });
    }

    /// <summary>统一收尾：无码且未给错误说明时补一句；设备码/密码已在 Extract 阶段填好。</summary>
    private static RecognizedResult Finalize(RecognizedResult r)
    {
        if (string.IsNullOrEmpty(r.DeviceCode) && string.IsNullOrEmpty(r.Error))
            r.Error = "没认出设备码，请在卡片上手动填写";
        return r;
    }
}
