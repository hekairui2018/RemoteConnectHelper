using System.Text;
using System.Text.RegularExpressions;

namespace RemoteConnectHelper.Recognition;

/// <summary>一次命中的设备码及其在截图中的位置</summary>
public sealed class CodeHit
{
    public string Code { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double CenterY => Y + H / 2.0;
    public double CenterX => X + W / 2.0;
    public double Bottom => Y + H;
}

/// <summary>按 Y 聚成的逻辑行（OCR 常把中文拆成单字词，行内拼接后才能匹配关键词）</summary>
public sealed class LogicalLine
{
    public List<OcrWord> Words { get; } = new();
    public string Text { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Right { get; set; }
    public double Bottom { get; set; }
    public double CenterY { get; set; }
}

/// <summary>关键词标签在某行中的位置锚点</summary>
public sealed class LabelAnchor
{
    public LogicalLine Line { get; init; } = null!;
    public double LabelX { get; init; }
    public double LabelRight { get; init; }
    public double LabelY { get; init; }
    public double LabelBottom { get; init; }
    public double CenterY { get; init; }
}

public static class CodeExtractor
{
    // 输入区（主动要连的目标码）标签；OCR 会认错字（如"设备"→"设番"），用多个容错片段
    public static readonly string[] InputLabelHints =
        { "远程控制设备", "控制设备", "控制设", "远程控制", "伙伴识别", "输入伙伴", "请输入伙伴", "请输入识别" };

    // 本机码标签
    public static readonly string[] LocalLabelHints =
        { "本机识别", "设备代码", "我的识别", "本机码", "本设备", "本机", "识别码" };

    // 向日葵把被控口令统称“验证码”（今日验证码/隐藏验证码/长期验证码/伙伴验证码…）
    public static readonly string[] SunloginPwdHints =
        { "今日验证码", "隐藏验证码", "长期验证码", "临时验证码", "伙伴验证码", "验证码", "安全码", "密码" };

    // ToDesk 用“密码/安全代码”体系（安全密码/临时密码/安全代码）；它不用“验证码”一词，不收，避免抢向日葵的码。
    public static readonly string[] ToDeskPwdHints =
        { "安全密码", "临时密码", "安全代码", "安全码", "密码" };

    // 聊天截图里常见的"设备码/账号"写法（界面输入区标签之外的补充，用于放宽碎短段拼接）
    public static readonly string[] ChatCodeLabelHints =
        { "识别码", "识别代号", "设备码", "设备代码", "代码", "账号", "帐号", "伙伴码", "伙伴识别" };

    // 联系方式行：这些标签旁边的数字是电话/QQ/微信号，绝不能当设备码
    public static readonly string[] ContactHints =
        { "电话", "手机", "联系", "拨打", "加我", "扣扣", "QQ", "qq", "微信", "威信", "vx", "VX", "Vx" };

    // 多码消歧时，与设备码一起给出的口令标签（向日葵+ToDesk 合并，仅用于定位口令，不判品牌）
    public static readonly string[] PasswordProximityHints =
        { "今日验证码", "隐藏验证码", "长期验证码", "临时验证码", "伙伴验证码", "验证码",
          "安全密码", "临时密码", "安全码", "安全代码", "密码" };

    // 弱锚点兜底时必须排除的品牌名（聊天/界面里出现的软件名绝不能当密码）
    private static readonly string[] PwdBrandBlock =
        { "todesk", "sunlogin", "awesun", "oray", "anydesk", "raylink" };

    // ---------- 行 ----------

    public static List<LogicalLine> BuildLines(OcrDoc doc)
    {
        var words = doc.Words
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderBy(w => w.CenterY).ThenBy(w => w.X)
            .ToList();

        var raw = new List<List<OcrWord>>();
        foreach (var w in words)
        {
            List<OcrWord>? target = null;
            foreach (var line in raw)
            {
                double avgH = line.Average(x => x.H);
                double tol = Math.Max(12, avgH * 0.6);
                if (Math.Abs(w.CenterY - line.Average(x => x.CenterY)) <= tol)
                {
                    target = line;
                    break;
                }
            }
            if (target != null) target.Add(w);
            else raw.Add(new List<OcrWord> { w });
        }

        var result = new List<LogicalLine>();
        foreach (var grp in raw)
        {
            var ordered = grp.OrderBy(w => w.X).ToList();
            var line = new LogicalLine();
            line.Words.AddRange(ordered);
            var sb = new StringBuilder();
            foreach (var w in ordered) sb.Append(w.Text);
            line.Text = sb.ToString();
            line.X = ordered.Min(w => w.X);
            line.Y = ordered.Min(w => w.Y);
            line.Right = ordered.Max(w => w.Right);
            line.Bottom = ordered.Max(w => w.Bottom);
            line.CenterY = ordered.Average(w => w.CenterY);
            result.Add(line);
        }
        return result.OrderBy(l => l.CenterY).ToList();
    }

    /// <summary>
    /// 噪声行：网址 / 下载地址行。向日葵邀请话术尾巴“向日葵下载地址:http://url.oray.com/tGJdas/”
    /// 里的 tGJdas 绝不能被当成密码；这类行整行不参与取码、取密码。
    /// </summary>
    public static bool IsNoiseLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (text.Contains("://") ||
            text.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("url.", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(text, @"[A-Za-z0-9-]+\.(com|cn|net|org|cc|top|xyz)\b", RegexOptions.IgnoreCase))
            return true;
        if ((text.Contains("下载") || text.Contains("网址")) &&
            (text.Contains("地址") || text.Contains("://") || text.Contains(".")))
            return true;
        return false;
    }

    // ---------- 标签 ----------

    /// <summary>找第一个（最靠上的）含任一关键词的行锚点。</summary>
    public static LabelAnchor? FindLabel(List<LogicalLine> lines, params string[] hints)
    {
        LabelAnchor? best = null;
        foreach (var line in lines)
        {
            foreach (var kw in hints)
            {
                int idx = line.Text.IndexOf(kw, StringComparison.Ordinal);
                if (idx < 0) continue;
                var anchor = MakeAnchor(line, idx, kw.Length);
                if (best == null || anchor.LabelY < best.LabelY) best = anchor;
            }
        }
        return best;
    }

    /// <summary>找所有含关键词的行锚点（密码标签可能多处）。</summary>
    public static List<LabelAnchor> FindAllLabels(List<LogicalLine> lines, string[] hints)
    {
        var list = new List<LabelAnchor>();
        foreach (var line in lines)
        {
            foreach (var kw in hints)
            {
                int idx = line.Text.IndexOf(kw, StringComparison.Ordinal);
                if (idx >= 0) { list.Add(MakeAnchor(line, idx, kw.Length)); break; }
            }
        }
        return list.OrderBy(a => a.LabelY).ToList();
    }

    private static LabelAnchor MakeAnchor(LogicalLine line, int charStart, int len)
    {
        // 映射字符索引到词
        int pos = 0;
        double? x = null, right = null;
        foreach (var w in line.Words)
        {
            int wStart = pos;
            int wEnd = pos + w.Text.Length;
            bool overlap = charStart < wEnd && charStart + len > wStart;
            if (overlap)
            {
                x = x == null ? w.X : Math.Min(x.Value, w.X);
                right = right == null ? w.Right : Math.Max(right.Value, w.Right);
            }
            pos = wEnd;
        }
        x ??= line.X;
        right ??= line.Right;
        return new LabelAnchor
        {
            Line = line,
            LabelX = x.Value,
            LabelRight = right.Value,
            LabelY = line.Y,
            LabelBottom = line.Bottom,
            CenterY = line.CenterY,
        };
    }

    // ---------- 设备码 ----------

    /// <summary>
    /// 设备码界面按 ### ### ###(####) 分段。只认真正的码段（整词纯数字、3~6 位）
    /// 并按 x 相邻组合成 9~10 位；单词本身就是 9/10 位也认。
    /// 长度 1~2 的杂散数字（图标角标、版本号）一律不参与，避免拼出假码。
    /// </summary>
    public static List<CodeHit> FindDeviceCodes(OcrDoc doc, int minLen, int maxLen)
    {
        var hits = new List<CodeHit>();
        var seen = new HashSet<string>();
        var lines = BuildLines(doc);

        foreach (var line in lines)
        {
            // 联系方式行（电话/手机/QQ/微信…）整行跳过：里面的长数字不是设备码
            if (ContactHints.Any(h => line.Text.Contains(h)))
                continue;
            // 网址/下载地址行整行跳过：话术尾巴的短码（如 tGJdas）不能当设备码/密码
            if (IsNoiseLine(line.Text))
                continue;

            // 明确写了"识别码/账号/设备码/远程控制设备"等标签的行，允许 OCR 切碎的 1~2 位短段参与拼接
            bool codeLabelLine =
                InputLabelHints.Any(h => line.Text.Contains(h)) ||
                LocalLabelHints.Any(h => line.Text.Contains(h)) ||
                ChatCodeLabelHints.Any(h => line.Text.Contains(h));

            var orderedWords = line.Words.OrderBy(w => w.X).ToList();

            // 1) 单词即完整码。设备码本应纯数字，OCR 偶尔把数字认成形似字母（0→O、1→I/l、5→S…），
            //    用 TryDigitLike 把"整词都能映射成数字"的词还原；真英文词含无法映射的字母，直接排除。
            foreach (var w in orderedWords)
            {
                if (TryDigitLike(w.Text, out string d) && d.Length >= minLen && d.Length <= maxLen)
                {
                    // 左侧紧贴一个"1"、拼成 1[3-9]xxxxxxxxx 的是 11 位手机号（OCR 常切成 1+10 位），排除
                    if (IsPhoneWithLeadingOne(orderedWords, w, d)) continue;
                    AddHit(hits, seen, d, w.X, w.Y, w.Right, w.Bottom);
                }
            }

            // 2) 相邻码段组合。普通行只收 3~6 位整词段防杂散数字；
            //    码标签行放宽到 1~6 位（聊天字体 OCR 会把 157/644 切碎），但拼出长度必须正好落在 9~10 位才采纳。
            //    段同样走形似字母→数字还原，救模糊图里某段被认成 O/I/S 的情况。
            string segPattern = codeLabelLine ? @"^\d{1,6}$" : @"^\d{3,6}$";
            var segs = new List<(OcrWord w, string ds)>();
            foreach (var w in orderedWords)
            {
                if (TryDigitLike(w.Text, out var ds0) && Regex.IsMatch(ds0, segPattern))
                    segs.Add((w, ds0));
            }

            for (int i = 0; i < segs.Count; i++)
            {
                var sb = new StringBuilder();
                double? x = null, y = null, rx = null, by = null;
                OcrWord? prev = null;
                for (int j = i; j < segs.Count; j++)
                {
                    var (w, ds) = segs[j];
                    if (prev != null)
                    {
                        double gap = w.X - prev.Right;
                        double sameLine = Math.Abs(w.CenterY - prev.CenterY);
                        // 码段间距小且水平对齐；离太远说明不是同一串码
                        if (gap > prev.H * 1.6 + 30 || sameLine > prev.H * 0.7) break;
                    }
                    sb.Append(ds);
                    x = x == null ? w.X : Math.Min(x.Value, w.X);
                    y = y == null ? w.Y : Math.Min(y.Value, w.Y);
                    rx = rx == null ? w.Right : Math.Max(rx.Value, w.Right);
                    by = by == null ? w.Bottom : Math.Max(by.Value, w.Bottom);
                    prev = w;

                    int L = sb.Length;
                    if (L >= minLen && L <= maxLen)
                        AddHit(hits, seen, sb.ToString(), x!.Value, y!.Value, rx!.Value, by!.Value);
                    if (L > maxLen) break;
                }
            }
        }
        return hits;
    }

    private static void AddHit(List<CodeHit> hits, HashSet<string> seen, string code,
                               double x, double y, double rx, double by)
    {
        if (!seen.Add(code)) return;
        hits.Add(new CodeHit { Code = code, X = x, Y = y, W = rx - x, H = by - y });
    }

    // 设备码强纠错：码段本应纯数字，OCR 常把数字认成形似字母（0→O/o、1→I/l、5→S、2→Z、8→B、
    // 6→G/b、9→g/q、3→E、7→T、4→A）。仅当整词每个字符都能映射成数字才采纳——真英文词
    // （ToDesk/ub14/desk 等）含无法映射的字母，会整词排除，不会被拼成假设备码。
    private static readonly Dictionary<char, char> DigitLikeMap = new()
    {
        ['0'] = '0', ['1'] = '1', ['2'] = '2', ['3'] = '3', ['4'] = '4',
        ['5'] = '5', ['6'] = '6', ['7'] = '7', ['8'] = '8', ['9'] = '9',
        ['O'] = '0', ['o'] = '0',
        ['I'] = '1', ['i'] = '1', ['l'] = '1', ['|'] = '1', ['L'] = '1',
        ['Z'] = '2', ['z'] = '2',
        ['E'] = '3',
        ['A'] = '4',
        ['S'] = '5', ['s'] = '5',
        ['G'] = '6', ['b'] = '6',
        ['T'] = '7',
        ['B'] = '8',
        ['g'] = '9', ['q'] = '9',
    };

    private static bool TryDigitLike(string word, out string digits)
    {
        var sb = new StringBuilder();
        foreach (char ch in word)
        {
            if (DigitLikeMap.TryGetValue(ch, out char d)) sb.Append(d);
            else { digits = ""; return false; }
        }
        digits = sb.ToString();
        return digits.Length > 0;
    }

    /// <summary>
    /// 数字词 d 左侧是否紧贴一个单独的"1"，且 "1"+d 拼成 11 位手机号（1[3-9] 开头）。
    /// OCR 常把手机号 13800138000 切成 "1" + "3800138000"，后者正好 10 位会被误当设备码。
    /// </summary>
    private static bool IsPhoneWithLeadingOne(List<OcrWord> ordered, OcrWord w, string d)
    {
        int idx = ordered.IndexOf(w);
        if (idx <= 0) return false;
        var prev = ordered[idx - 1];
        if (prev.Text != "1") return false;
        double gap = w.X - prev.Right;
        if (gap > w.H * 0.6) return false; // 必须几乎贴着（同一串数字被切开）
        return Regex.IsMatch("1" + d, @"^1[3-9]\d{9}$");
    }

    // ---------- 密码 ----------

    /// <summary>在密码标签同行右侧、下方或上方紧邻处找密码；找不到返回 ""（免密）。</summary>
    public static string FindPassword(OcrDoc doc, string[] pwdHints, IEnumerable<string> deviceCodes)
    {
        var devSet = new HashSet<string>(deviceCodes);
        var lines = BuildLines(doc)
            .Where(l => !IsNoiseLine(l.Text) && !ContactHints.Any(h => l.Text.Contains(h)))
            .ToList();
        var labels = FindAllLabels(lines, pwdHints);

        string? bestAlpha = null; double bestAlphaScore = double.MaxValue;  // 含字母，最可靠
        string? bestNum = null; double bestNumScore = double.MaxValue;      // 纯数字（ToDesk 安全密码）

        // 把一行内相邻的字母数字词按 x 聚簇（OCR 常把一串密码切成 s/×/618m 几段），逐簇算出候选。
        // 叉号/乘号在 NormalizePwd 里归一为 X（大写 X 常被认成 ×）。纯中文/标点词清洗后为空，会断簇且不当密码。
        List<(OcrWord Word, double Score)> Collect(IEnumerable<OcrWord> words,
                                                   double anchorX, double anchorCY,
                                                   double baseScore, bool sameLine)
        {
            var ret = new List<(OcrWord Word, double Score)>();
            var ws = words.Where(w => NormalizePwd(w.Text).Length > 0).OrderBy(w => w.X).ToList();
            var clusters = new List<List<OcrWord>>();
            foreach (var w in ws)
            {
                List<OcrWord>? cur = null;
                if (clusters.Count > 0)
                {
                    var last = clusters[^1][^1];
                    double cgap = w.X - last.Right;
                    if (cgap <= w.H * 0.9 + 14 && Math.Abs(w.CenterY - last.CenterY) <= w.H * 0.7)
                        cur = clusters[^1];
                }
                cur ??= new List<OcrWord>();
                if (!clusters.Contains(cur)) clusters.Add(cur);
                cur.Add(w);
            }
            foreach (var cl in clusters)
            {
                string joined = string.Concat(cl.Select(x => NormalizePwd(x.Text)));
                var merged = new OcrWord
                {
                    Text = joined,
                    X = cl[0].X,
                    Y = cl.Min(x => x.Y),
                    W = cl[^1].Right - cl[0].X,
                    H = cl.Max(x => x.Bottom) - cl.Min(x => x.Y),
                };
                double score = sameLine
                    ? (cl[0].X - anchorX) + Math.Abs(merged.CenterY - anchorCY)
                    : baseScore + Math.Abs(cl[0].X - anchorX) * 0.3;
                ret.Add((merged, score));
            }
            return ret;
        }

        var candidates = new List<(OcrWord Word, double Score)>();
        foreach (var lab in labels)
        {
            double avgH = lab.Line.Words.Average(x => x.H);

            // 1) 标签同行右侧（紧邻，不跨行抓远处的词）
            var rightWords = lab.Line.Words
                .Where(w => w.Right > lab.LabelRight - 6
                         && w.X - lab.LabelRight <= 300
                         && Math.Abs(w.CenterY - lab.CenterY) <= avgH * 0.8);
            candidates.AddRange(Collect(rightWords, lab.LabelRight, lab.CenterY, 0, true));

            // 2) 标签下方紧邻行（90px 内）与上方紧邻行（80px 内）。
            //    向日葵简约版密码在"隐藏验证码"开关旁、位于"每次被控后将自动更新验证码"说明文字上方，
            //    拍屏时灰色"隐藏验证码"标签还常漏识，故上方窗口必须兜住；设备码是 9/10 位纯数字，会被 Consider 排除。
            foreach (var line in lines)
            {
                double below = line.Y - lab.LabelBottom;
                if (below >= -6 && below <= 90)
                    candidates.AddRange(Collect(line.Words, lab.LabelX, lab.CenterY,
                                                Math.Max(below, 0) * 2 + 100, false));

                double above = lab.LabelY - line.Bottom;
                if (above >= -6 && above <= 80)
                    candidates.AddRange(Collect(line.Words, lab.LabelX, lab.CenterY,
                                                Math.Max(above, 0) * 2 + 120, false));
            }
        }

        foreach (var (word, score) in candidates)
            Consider(word, devSet, score, ref bestAlpha, ref bestAlphaScore, ref bestNum, ref bestNumScore);

        // 精确标签一个都没匹配上（拍屏模糊把“今日验证码”识成“夸日榨证”等）时，用单字“证/密”做弱锚点兜底。
        // 弱锚点只救含字母的密码（纯数字与设备码难区分，宁可漏为免密/手输也不乱填），并排除品牌名/设备码。
        if (bestAlpha == null && bestNum == null)
        {
            string? weak = FindPasswordByWeakAnchor(lines, devSet);
            if (!string.IsNullOrEmpty(weak)) return weak;
            // 聊天两条独立消息“设备码+密码”、无任何标签、顺序不固定：残差法兜底
            string? residual = FindPasswordByResidual(doc, devSet);
            if (!string.IsNullOrEmpty(residual)) return residual;
        }

        return bestAlpha ?? bestNum ?? "";
    }

    /// <summary>
    /// 弱锚点兜底：整词标签被 OCR 认错时，退而用单字“证/密”定位密码标签行，
    /// 在其同行右侧、上下紧邻行聚簇找密码。只采纳【含字母】的 4~20 位混合串，
    /// 且不得是品牌名、不得含 9 位以上连续数字（设备码），最大限度避免免密界面误抓。
    /// </summary>
    private static string? FindPasswordByWeakAnchor(List<LogicalLine> lines, HashSet<string> devSet)
    {
        string? best = null;
        double bestScore = double.MaxValue;

        foreach (var line in lines)
        {
            if (!line.Text.Contains('证') && !line.Text.Contains('密')) continue;

            // 行内最后一个“证/密”字所在词的右沿，作为同行右侧搜索的锚点
            double anchorX = line.X, anchorRight = line.X;
            foreach (var w in line.Words)
            {
                if (w.Text.Contains('证') || w.Text.Contains('密'))
                {
                    anchorX = w.X;
                    anchorRight = w.Right;
                }
            }
            double avgH = line.Words.Count > 0 ? line.Words.Average(x => x.H) : 20;

            // 1) 同行右侧
            var rightWords = line.Words
                .Where(w => w.Right > anchorRight - 6
                         && w.X - anchorRight <= 300
                         && Math.Abs(w.CenterY - line.CenterY) <= avgH * 0.8);
            foreach (var m in ClusterPasswordWords(rightWords))
            {
                double score = (m.X - anchorRight) + Math.Abs(m.CenterY - line.CenterY);
                WeakConsider(m, devSet, score, ref best, ref bestScore);
            }

            // 2) 下方 90px / 上方 80px 紧邻行
            foreach (var o in lines)
            {
                double below = o.Y - line.Bottom;
                if (below is >= -6 and <= 90)
                {
                    double baseScore = Math.Max(below, 0) * 2 + 100;
                    foreach (var m in ClusterPasswordWords(o.Words))
                        WeakConsider(m, devSet, baseScore + Math.Abs(m.X - anchorX) * 0.3, ref best, ref bestScore);
                }
                double above = line.Y - o.Bottom;
                if (above is >= -6 and <= 80)
                {
                    double baseScore = Math.Max(above, 0) * 2 + 120;
                    foreach (var m in ClusterPasswordWords(o.Words))
                        WeakConsider(m, devSet, baseScore + Math.Abs(m.X - anchorX) * 0.3, ref best, ref bestScore);
                }
            }
        }
        return best;
    }

    /// <summary>把一行内相邻的字母数字词按 x 聚簇成若干候选密码串（与 FindPassword 内聚簇规则一致）。</summary>
    private static List<OcrWord> ClusterPasswordWords(IEnumerable<OcrWord> words)
    {
        var ws = words.Where(w => NormalizePwd(w.Text).Length > 0).OrderBy(w => w.X).ToList();
        var clusters = new List<List<OcrWord>>();
        foreach (var w in ws)
        {
            List<OcrWord>? cur = null;
            if (clusters.Count > 0)
            {
                var last = clusters[^1][^1];
                double cgap = w.X - last.Right;
                if (cgap <= w.H * 0.9 + 14 && Math.Abs(w.CenterY - last.CenterY) <= w.H * 0.7)
                    cur = clusters[^1];
            }
            cur ??= new List<OcrWord>();
            if (!clusters.Contains(cur)) clusters.Add(cur);
            cur.Add(w);
        }
        var merged = new List<OcrWord>();
        foreach (var cl in clusters)
        {
            string joined = string.Concat(cl.Select(x => NormalizePwd(x.Text)));
            merged.Add(new OcrWord
            {
                Text = joined,
                X = cl[0].X,
                Y = cl.Min(x => x.Y),
                W = cl[^1].Right - cl[0].X,
                H = cl.Max(x => x.Bottom) - cl.Min(x => x.Y),
            });
        }
        return merged;
    }

    /// <summary>弱锚点候选筛选：必须含足够字母、4~20 位、非品牌名、形态不像设备码。</summary>
    private static void WeakConsider(OcrWord w, HashSet<string> devSet, double score,
                                     ref string? best, ref double bestScore)
    {
        string raw = NormalizePwd(w.Text);
        if (raw.Length is < 4 or > 20) return;
        if (!Regex.IsMatch(raw, @"^[A-Za-z0-9]+$")) return;
        int letters = raw.Count(char.IsLetter);
        if (letters == 0) return;                            // 弱锚点只收含字母的密码
        if (Regex.IsMatch(raw, @"\d{9,}")) return;           // 含设备码长数字串，不采
        // 重度模糊时设备码里的数字会被误识成字母（如 114509971→114S09971）。
        // 9~10 位且几乎全数字（字母≤1）、或字母占比不足 1/4 的，形态就是设备码，弱锚点不采。
        if (raw.Length is >= 9 and <= 10 && letters <= 1) return;
        if (letters * 4 < raw.Length) return;
        if (devSet.Contains(raw)) return;
        string low = raw.ToLowerInvariant();
        if (PwdBrandBlock.Any(b => low.Contains(b))) return;
        if (score < bestScore) { bestScore = score; best = raw; }
    }

    /// <summary>
    /// 残差兜底：没有任何密码标签时（典型＝聊天里两条独立消息“设备码”和“密码”，无品牌无标签、
    /// 谁在上不一定），从设备码之外的字母数字短串里找密码，天然与上下行顺序无关。
    /// 含字母的 4~16 位串优先（离设备码纵向近者胜）；纯数字 4~8 位仅在“全文只有一个长码、
    /// 该短数字独占一行、行内无中文/时间”时才采纳，避免把界面角标、版本号、时间、折扣当密码。
    /// 网址行、联系方式行、品牌名一律排除。
    /// </summary>
    private static string? FindPasswordByResidual(OcrDoc doc, HashSet<string> devSet)
    {
        var lines = BuildLines(doc)
            .Where(l => !IsNoiseLine(l.Text) && !ContactHints.Any(h => l.Text.Contains(h)))
            .ToList();

        // 全文 9 位及以上长数字串数量；纯数字短密码仅在长码唯一时采纳
        int longCodeCount = 0;
        foreach (var l in lines)
            foreach (Match dm in Regex.Matches(l.Text, @"\d+"))
                if (dm.Value.Length >= 9) longCodeCount++;

        double codeY = GuessCodeLineY(doc, devSet);
        string? alpha = null;
        double alphaScore = double.MaxValue;
        string? num = null;

        foreach (var line in lines)
        {
            var digitsInLine = Regex.Matches(line.Text, @"\d+");
            bool hasChinese = line.Text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
            bool isTime = Regex.IsMatch(line.Text, @"\d{1,2}[:：]\d{2}");

            foreach (Match tm in Regex.Matches(line.Text, @"[A-Za-z0-9]+"))
            {
                string norm = NormalizePwd(tm.Value);
                if (norm.Length is < 4 or > 16) continue;
                if (Regex.IsMatch(norm, @"\d{9,}")) continue;           // 长数字＝设备码
                if (devSet.Contains(norm)) continue;
                string low = norm.ToLowerInvariant();
                if (PwdBrandBlock.Any(b => low.Contains(b))) continue;  // 品牌名不当密码

                if (norm.Any(char.IsLetter))
                {
                    // 含字母：远程软件临时密码最常见形态，误报率低，离设备码越近越可信
                    double score = Math.Abs(line.CenterY - codeY);
                    if (norm.Length > 12) score += 500;
                    if (score < alphaScore) { alphaScore = score; alpha = norm; }
                }
                else if (norm.Length is >= 4 and <= 8)
                {
                    // 纯数字短码：必须独占一行（该行只有一个数字段、无中文、不是时间）、且全文长码唯一
                    bool alone = digitsInLine.Count == 1 && !hasChinese && !isTime;
                    if (alone && longCodeCount == 1) num ??= norm;
                }
            }
        }
        return alpha ?? num;
    }

    /// <summary>设备码所在行的纵向中心（残差法给密码候选按距离排序用）；找不到返回图中。</summary>
    private static double GuessCodeLineY(OcrDoc doc, HashSet<string> devSet)
    {
        foreach (var l in BuildLines(doc))
        {
            string digits = Regex.Replace(l.Text, @"\D", "");
            if (devSet.Contains(digits)) return l.CenterY;
            foreach (var c in devSet)
                if (l.Text.Replace(" ", "").Contains(c)) return l.CenterY;
        }
        return doc.ImgHeight / 2.0;
    }

    /// <summary>密码字符清洗：先把 OCR 易把大写 X 误认成的叉号/乘号归一为 X，再只保留字母数字。</summary>
    private static string NormalizePwd(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace('×', 'X').Replace('✕', 'X').Replace('✖', 'X')
                   .Replace('⨯', 'X').Replace('╳', 'X').Replace('✗', 'X');
        return Regex.Replace(text, @"[^A-Za-z0-9]", "");
    }

    private static void Consider(OcrWord w, HashSet<string> devSet, double score,
                                 ref string? bestAlpha, ref double bestAlphaScore,
                                 ref string? bestNum, ref double bestNumScore)
    {
        string raw = NormalizePwd(w.Text);
        if (raw.Length < 4 || raw.Length > 20) return;
        if (!Regex.IsMatch(raw, @"^[A-Za-z0-9]+$")) return;
        if (devSet.Contains(raw)) return;

        bool alpha = Regex.IsMatch(raw, "[A-Za-z]");
        if (alpha)
        {
            if (score < bestAlphaScore) { bestAlphaScore = score; bestAlpha = raw; }
        }
        else
        {
            // 纯数字：排除 9/10 位设备码，只当 4~8 位安全密码
            if (raw.Length is >= 4 and <= 8 && score < bestNumScore)
            {
                bestNumScore = score;
                bestNum = raw;
            }
        }
    }

    // ---------- 目标码选择 ----------

    public static CodeHit? PickTargetCode(OcrDoc doc, List<CodeHit> hits, string password,
                                          out List<string> candidates)
    {
        candidates = hits.Select(h => h.Code).Distinct().ToList();
        if (hits.Count == 0) return null;
        if (hits.Count == 1) return hits[0];

        var lines = BuildLines(doc);

        // 1) 密码锚定（最可靠）：密码紧邻哪个设备码，哪个就是要连的目标。
        //    客户首页截图里密码在【本机码】旁（输入框残留码无密码）；客服端已填的连接表单里
        //    密码在【输入框码】下方。两种情况都靠“离密码最近”正确区分，不依赖码在上方还是下方。
        double? pwdY = LocatePasswordY(doc, password);
        if (pwdY.HasValue)
        {
            var byPwd = hits.OrderBy(h => Math.Abs(h.CenterY - pwdY.Value)).FirstOrDefault();
            if (byPwd != null && Math.Abs(byPwd.CenterY - pwdY.Value) <= 220)
                return byPwd;
        }

        // 2) 无密码（免密）：客户首页截图要连的是客户【本机码】（此设备/允许控制本设备/设备代码区），
        //    “远程控制设备/请输入伙伴识别码”输入框里的码是客户去连别人的残留，不能当目标。
        var local = FindLabel(lines, LocalLabelHints);
        if (local != null)
        {
            var nearLocal = hits
                .OrderBy(h => Math.Abs(h.CenterY - local.CenterY)
                              + Math.Abs(h.CenterX - local.LabelX) * 0.2)
                .FirstOrDefault();
            return nearLocal;
        }

        // 3) 本机标签被 OCR 切碎时用几何：远程输入区标签【上方】的码即本机码（首页布局本机码在上、
        //    输入框在下），优先；只有上方确实没有码（纯连接窗）时才取标签正下方输入框里的码。
        var inputAnchors = FindAllLabels(lines, InputLabelHints);
        CodeHit? abovePick = null;
        double aboveDy = double.MaxValue;
        CodeHit? inputPick = null;
        double inputPickDy = double.MaxValue;
        foreach (var anc in inputAnchors)
        {
            foreach (var h in hits)
            {
                if (h.Bottom <= anc.LabelY - 6)
                {
                    double dy = anc.LabelY - h.Bottom; // 越贴标签上沿越像本机码
                    if (dy < aboveDy) { aboveDy = dy; abovePick = h; }
                }
                else
                {
                    double dy = h.CenterY - anc.CenterY;
                    if (dy >= -10 && dy <= 140 && dy < inputPickDy) { inputPickDy = dy; inputPick = h; }
                }
            }
        }
        if (abovePick != null) return abovePick;
        if (inputPick != null) return inputPick;

        // 4) 聊天截图兜底：无界面输入区/本机标签时，按"密码标签邻近/软件名同行"消歧
        var chat = PickChatCode(lines, hits);
        if (chat != null) return chat;

        // 多个码且无任何标签，无法区分 → 交人工选（候选已带全）
        return null;
    }

    /// <summary>
    /// 在 OCR 词里定位已识别密码串的纵向中心（密码可能由多个词拼成，取命中词中心的平均 Y）。
    /// 定位不到返回 null（免密图）。用于“密码紧邻哪个设备码”的锚定。
    /// </summary>
    private static double? LocatePasswordY(OcrDoc doc, string password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        double sum = 0;
        int n = 0;
        foreach (var w in doc.Words)
        {
            string norm = NormalizePwd(w.Text);
            if (norm.Length < 3) continue;
            bool eq = string.Equals(norm, password, StringComparison.OrdinalIgnoreCase);
            bool part = norm.Length >= 4 && password.Contains(norm, StringComparison.OrdinalIgnoreCase);
            if (eq || part)
            {
                sum += w.CenterY;
                n++;
            }
        }
        return n > 0 ? sum / n : null;
    }

    /// <summary>
    /// 聊天信息截图的多码消歧（界面截图走不到这里）：优先与密码标签同行/紧邻的码，
    /// 其次与软件名（向日葵/todesk）同一行的码；都没有才交人工。
    /// </summary>
    private static CodeHit? PickChatCode(List<LogicalLine> lines, List<CodeHit> hits)
    {
        var pwdLabels = FindAllLabels(lines, PasswordProximityHints);
        CodeHit? best = null; double bestScore = double.MaxValue;
        foreach (var a in pwdLabels)
        {
            double tol = a.Line.Words.Average(x => x.H) * 0.9;
            foreach (var h in hits)
            {
                double dySame = Math.Abs(h.CenterY - a.CenterY);
                if (dySame <= tol)
                {
                    double score = Math.Abs(h.CenterX - a.LabelX) * 0.2 + dySame;
                    if (score < bestScore) { bestScore = score; best = h; }
                    continue;
                }
                double above = a.LabelY - h.Bottom; // 码通常在密码标签上方
                if (above >= -6 && above <= 120)
                {
                    double score = above * 2 + 100 + Math.Abs(h.CenterX - a.LabelX) * 0.1;
                    if (score < bestScore) { bestScore = score; best = h; }
                }
            }
        }
        if (best != null) return best;

        string[] brand = { "向日葵", "sunlogin", "oray", "todesk", "贝锐" };
        foreach (var line in lines)
        {
            string low = line.Text.ToLowerInvariant();
            if (!brand.Any(b => low.Contains(b.ToLowerInvariant()))) continue;
            double tol = line.Words.Average(x => x.H) * 0.9;
            var onLine = hits
                .Where(h => Math.Abs(h.CenterY - line.CenterY) <= tol)
                .OrderBy(h => h.X)
                .FirstOrDefault();
            if (onLine != null) return onLine;
        }
        return null;
    }
}
