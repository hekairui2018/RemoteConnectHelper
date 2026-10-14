using System.Text;

namespace RemoteConnectHelper.Recognition;

/// <summary>
/// 识别结果三问：Q1 设备码（9~10 位数字，界面“本机/此设备”区优先于输入框残留）；
/// Q2 密码（设备码之外含字母短串或 4~8 位纯数字，找不到=免密）；Q3 品牌（只看强信号，不看颜色）。
/// 三种结果：A 铁证+有码=弹气泡并自动直连（品牌铁证时不打断）；B 无品牌强信号+有码=弹软件选择气泡（默认向日葵，人工选定后点连接）；
/// C 无码=弹气泡手填。绝不因“证/密”等单字或界面颜色替客服赌是哪家。
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

    // ═════ 第一层·强证据（出现任一即百分百向日葵）═════
    // 官方（贝锐/向日葵官网/客服中心）确认的独有叫法：本机识别码/验证码/伙伴识别码体系；
    // ToDesk 官方术语全套是“设备代码/临时密码/安全密码/临时码”，绝无这些词。
    // 品牌名/logo 文字同样百分百（真机 logo 小字常漏识，仅作补充）。深色版左侧“多协议远控”为 T17 真机实证。
    // 英文版客户端/域名也收：Sunlogin ID / Verification Code —— OCR 对拉丁字母比对中文稳，
    //   旋转/模糊时更能还原，客户装英文版界面或聊天里的下载域名都能认。
    private static readonly string[] StrongTerms =
        { "识别码", "伙伴识别码", "验证码", "多协议远控", "可为空",
          "sunlogin id", "verification code" };
    private static readonly string[] StrongBrand =
        { "向日葵", "sunlogin", "oray", "贝锐", "awesun", "简约版" };

    // ═════ 第二层·弱证据权重（无强证据时多词累计打分）═════
    // 只有官方/真机实证过的词才给分；两家共有词一律低分，不能当铁证。
    // “允许控制本设备/控制本设备/远程控制设备”真机两家都有（ToDesk T14、向日葵 T17 同框实证）→ 仅低分。
    // 2026-10-09 补：向日葵全窗口真机实证栏目/营销词（超级会员/免费版/搜索·连接设备/下载企业版/向日葵MCP）。
    private static readonly (string Word, int W)[] WeakTerms =
    {
        ("远程协助", 15), ("远程文件", 12), ("手机投屏", 10), ("下载地址", 12), // 向日葵官网功能/栏目/邀请话术
        ("remote assistance", 12), // 向日葵英文版
        ("超级会员", 8), ("搜索/连接设备", 6), ("免费版", 6), ("下载企业版", 6), ("mcp", 5), // 真机实证（T18/T19/全窗口图）
        ("远程控制设", 4), ("允许控制本设备", 4), ("控制本设备", 3), ("设备列表", 3), ("远程桌面", 3), // 共有词
    };

    /// <summary>命中哪些强证据（百分百判定用），返回含“品牌:xx/术语:xx”的人话描述。</summary>
    public string[] StrongHitWords(string flat) =>
        StrongBrand.Where(flat.Contains).Select(w => "品牌:" + w)
        .Concat(StrongTerms.Where(flat.Contains).Select(w => "术语:" + w))
        .ToArray();

    /// <summary>弱证据累计：返回命中的弱词与总分（权重打分用）。</summary>
    public (List<string> Words, int Score) WeakScore(string flat)
    {
        var hits = WeakTerms.Where(t => flat.Contains(t.Word)).ToList();
        return (hits.Select(t => t.Word).ToList(), hits.Sum(t => t.W));
    }

    public RecognizedResult Extract(OcrDoc doc)
    {
        var hits = CodeExtractor.FindDeviceCodes(doc, 9, 10);
        var cand = hits.Select(h => h.Code).Distinct().ToList();
        // 先取密码，再用“密码紧邻哪个码”锚定目标设备码（兼容客服端已填表单 vs 客户本机首页）
        var pwd = CodeExtractor.FindPassword(doc, CodeExtractor.SunloginPwdHints, cand, out var pwdAlts, out var pwdNeedsReview);
        var hit = CodeExtractor.PickTargetCode(doc, hits, pwd, out var candidates);
        return new RecognizedResult
        {
            Kind = RemoteAppKind.Sunlogin,
            DeviceCode = hit?.Code ?? "",
            CandidateCodes = candidates,
            Password = pwd,
            PasswordNeedsReview = pwdNeedsReview,
            CandidatePasswords = pwdAlts,
            TargetCodeX = hit?.CenterX ?? -1,
            TargetCodeY = hit?.CenterY ?? -1
        };
    }
}

public class ToDeskRecognizer : IRecognizer
{
    public RemoteAppKind Kind => RemoteAppKind.ToDesk;

    // ═════ 第一层·强证据（出现任一即百分百 ToDesk）═════
    // ToDesk 官方（官网/帮助中心/新闻）确认的独有叫法：设备代码/临时密码/安全密码/安全代码/临时码；
    // 新版本机码区三个模式（连接/观看/协作模式）与二次验证、镜像屏、云电脑；官方邀请话术“邀请您进行远程控制”。
    // 向日葵对应叫“识别码/验证码/远程协助”，绝无这些词。品牌名 todesk、英文版 Device Code / Temporary Password
    // 同理（英文在旋转/模糊下比中文更易被 OCR 还原，英文版界面也能认）。
    private static readonly string[] StrongTerms =
        { "设备代码", "临时密码", "安全密码", "安全代码", "临时码", "连接模式", "观看模式", "协作模式", "二次验证", "镜像屏", "云电脑",
          "邀请您进行远程控制",
          "device code", "temporary password", "security password" };
    private static readonly string[] StrongBrand = { "todesk" };

    // ═════ 第二层·弱证据权重（无强证据时多词累计打分）═════
    // 只收官方/真机实证：ToDesk 界面功能名“文件传输”（向日葵叫“远程文件”）、宣传“端到端加密”、
    // 本机码区标题“此设备”（T14/T15 真机）、版本/节点“全球节点”、栏目“应用中心”（T15 真机）、英文“file transfer”。
    // 2026-10-09 补：ToDesk 全窗口真机实证顶栏/功能/状态词（投屏演示/互传文件/锁屏密码/已准备好连接/通用场景/返回旧版/快速上手/快速设置/学生特惠）。
    // 注意：屏幕墙在向日葵左侧导航也出现（高清双窗口真机实证），是共有词，不进 ToDesk 弱词。
    private static readonly (string Word, int W)[] WeakTerms =
    {
        ("文件传输", 12), ("端到端加密", 12), ("此设备", 10), ("全球节点", 8), ("应用中心", 8), ("file transfer", 12),
        ("投屏演示", 10), ("互传文件", 10), ("锁屏密码", 8), ("已准备好连接", 8),
        ("通用场景", 6), ("返回旧版", 6), ("快速上手", 6), ("快速设置", 6), ("学生特惠", 4),
        ("远程控制设", 4), ("允许控制本设备", 4), ("控制本设备", 3), ("设备列表", 3), ("远程桌面", 3), // 共有词
    };

    public string[] StrongHitWords(string flat) =>
        StrongBrand.Where(flat.Contains).Select(w => "品牌:" + w)
        .Concat(StrongTerms.Where(flat.Contains).Select(w => "术语:" + w))
        .ToArray();

    public (List<string> Words, int Score) WeakScore(string flat)
    {
        var hits = WeakTerms.Where(t => flat.Contains(t.Word)).ToList();
        return (hits.Select(t => t.Word).ToList(), hits.Sum(t => t.W));
    }

    public bool BrandNameHit(string flat) => StrongBrand.Any(flat.Contains);

    public RecognizedResult Extract(OcrDoc doc)
    {
        var hits = CodeExtractor.FindDeviceCodes(doc, 9, 9);
        var cand = hits.Select(h => h.Code).Distinct().ToList();
        var rawPwd = CodeExtractor.FindPassword(doc, CodeExtractor.ToDeskPwdHints, cand, out var rawPwdAlts, out var pwdNeedsReview);
        bool temporary = doc.FlatText.Contains("临时密码") || doc.FlatText.Contains("临时码")
            || doc.FlatText.Contains("temporarypassword", StringComparison.OrdinalIgnoreCase);
        string Normalize(string value) => temporary ? NormalizeTodeskTempPassword(value) : value;
        var pwd = Normalize(rawPwd);
        var hit = CodeExtractor.PickTargetCode(doc, hits, pwd, out var candidates);
        return new RecognizedResult
        {
            Kind = RemoteAppKind.ToDesk,
            DeviceCode = hit?.Code ?? "",
            CandidateCodes = candidates,
            Password = pwd,
            PasswordNeedsReview = pwdNeedsReview,
            TargetCodeX = hit?.CenterX ?? -1,
            TargetCodeY = hit?.CenterY ?? -1,
            // 候选密码同样按“临时密码无大写”归一（OCR 常把数字 0 认成大写 O）
            CandidatePasswords = rawPwdAlts
                .Select(Normalize)
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
}

/// <summary>
/// 品牌仲裁，两层：
///  第一层·强证据：官方确认的独有术语/品牌名，出现即百分百判定（如向日葵“识别码/验证码/伙伴识别码”，
///    ToDesk“设备代码/临时密码/安全密码/临时码/连接·观看·协作模式/二次验证”）。
///  第二层·权重打分：无强证据时把“远程协助/文件传输/此设备/允许控制本设备…”等较弱或共有词按权重累计，
///    分差≥20 才置信直连；否则定一个默认品牌但弹软件选择卡人工确认——绝不靠一个弱词就直连。
/// 两家强证据同屏（真机可能拍到两个软件窗口/聊天里的另一家）也一律弹卡，不赌。
/// </summary>
public class RecognizerEngine
{
    private readonly SunloginRecognizer _sun = new();
    private readonly ToDeskRecognizer _td = new();

    /// <summary>权重分差达到该值才允许“无强证据但置信直连”。单个弱词 8~15 分，两个强弱词累计约 25+。</summary>
    private const int ConfidentGap = 20;

    public RecognizedResult Run(OcrDoc doc)
    {
        // 品牌名统一转小写再匹配：OCR 常输出首字母大写的 “ToDesk”，Contains 必须大小写不敏感
        string f = doc.FlatText.ToLowerInvariant();

        var sunStrong = _sun.StrongHitWords(f);
        var tdStrong = _td.StrongHitWords(f);
        (var sunWeak, int sunScore) = _sun.WeakScore(f);
        (var tdWeak, int tdScore) = _td.WeakScore(f);

        // ① 只有一家强证据 → 百分百铁证直连
        if (sunStrong.Length > 0 && tdStrong.Length == 0)
        {
            var r = _sun.Extract(doc);
            r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
            r.BrandReason = "向日葵强证据[" + string.Join(",", sunStrong) + "]";
            return Finalize(r);
        }
        if (tdStrong.Length > 0 && sunStrong.Length == 0)
        {
            var r = _td.Extract(doc);
            r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
            r.BrandReason = "ToDesk强证据[" + string.Join(",", tdStrong) + "]";
            return Finalize(r);
        }

        // ② 两家强证据同屏：真实拍屏可能同时拍到两个软件窗口或聊天里的另一家 → 绝不赌，弹软件选择卡。
        //    用目标码周围的文字修正默认软件；局部证据不足时沿用全图默认，仍须人工确认。
        if (sunStrong.Length > 0 && tdStrong.Length > 0)
        {
            var amb = (sunStrong.Length >= tdStrong.Length ? (IRecognizer)_sun : (IRecognizer)_td).Extract(doc);
            string nearby = NeighborhoodBrand(doc, amb.TargetCodeX, amb.TargetCodeY);
            if (nearby == "ToDesk") amb = _td.Extract(doc);
            else if (nearby == "向日葵") amb = _sun.Extract(doc);
            amb.BrandConfident = false;
            amb.BrandReason = $"两家强证据同屏(向日葵[{string.Join(",", sunStrong)}] ToDesk[{string.Join(",", tdStrong)}]),人工选软件";
            return Finalize(amb);
        }

        // ③ 无强证据 → 多特征权重打分
        int diff = sunScore - tdScore;
        string weightDesc = $"权重分 向日葵+{sunScore}({string.Join(",", sunWeak)}) ToDesk+{tdScore}({string.Join(",", tdWeak)})";

        if (diff >= ConfidentGap)
        {
            var r = _sun.Extract(doc);
            r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
            r.BrandReason = $"向日葵权重[{weightDesc}]";
            return Finalize(r);
        }
        if (diff <= -ConfidentGap)
        {
            var r = _td.Extract(doc);
            r.BrandConfident = !string.IsNullOrEmpty(r.DeviceCode);
            r.BrandReason = $"ToDesk权重[{weightDesc}]";
            return Finalize(r);
        }

        // 分差不足：定一个默认品牌（分高者，同分默认向日葵）但弹软件选择卡人工确认
        var guessed = (diff >= 0 ? (IRecognizer)_sun : (IRecognizer)_td).Extract(doc);
        if (!string.IsNullOrEmpty(guessed.DeviceCode))
        {
            guessed.BrandConfident = false;
            guessed.BrandReason = $"品牌证据不足[{weightDesc}],默认{guessed.KindName}待人工确认";
            return Finalize(guessed);
        }

        // 连码都没有：弹卡手填
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

    /// <summary>
    /// 目标设备码“所在区域”的品牌判定（同屏冲突/证据不足时定默认软件）。
    /// 双窗口截图里两家软件并排：码在哪个窗口，就用那个窗口的证据定默认，避免拿 A 的码去连 B。
    /// 区域 = 码中心横/纵 ±180px 内的 OCR 词；同样先强证据、再权重，两者都无返回 ""（由调用方回退）。
    /// </summary>
    private static string NeighborhoodBrand(OcrDoc doc, double cx, double cy)
    {
        if (cx < 0 || cy < 0) return "";
        var nearbyWords = doc.Words.Where(w => Math.Abs(w.CenterY - cy) <= 180
            && w.CenterX >= cx - 180 && w.CenterX <= cx + 180);
        var lines = CodeExtractor.BuildLines(new OcrDoc { Words = nearbyWords.ToList() });
        var sb = new StringBuilder();
        foreach (var l in lines)
        {
            sb.Append(l.Text);
        }
        string nf = sb.ToString().ToLowerInvariant();
        var sun = new SunloginRecognizer();
        var td = new ToDeskRecognizer();
        var sStrong = sun.StrongHitWords(nf);
        var tStrong = td.StrongHitWords(nf);
        if (sStrong.Length > 0 && tStrong.Length == 0) return "向日葵";
        if (tStrong.Length > 0 && sStrong.Length == 0) return "ToDesk";
        (_, int sW) = sun.WeakScore(nf);
        (_, int tW) = td.WeakScore(nf);
        if (sW > tW + 5) return "向日葵";
        if (tW > sW + 5) return "ToDesk";
        return "";
    }
}
