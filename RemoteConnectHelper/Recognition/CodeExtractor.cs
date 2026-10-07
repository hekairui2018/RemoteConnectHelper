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

            // 明确写了"识别码/账号/设备码/远程控制设备"等标签的行，允许 OCR 切碎的 1~2 位短段参与拼接
            bool codeLabelLine =
                InputLabelHints.Any(h => line.Text.Contains(h)) ||
                LocalLabelHints.Any(h => line.Text.Contains(h)) ||
                ChatCodeLabelHints.Any(h => line.Text.Contains(h));

            var orderedWords = line.Words.OrderBy(w => w.X).ToList();

            // 1) 单词即完整码
            foreach (var w in orderedWords)
            {
                string d = Regex.Replace(w.Text, @"[^\d]", "");
                if (Regex.IsMatch(w.Text, @"^\d+$") && d.Length >= minLen && d.Length <= maxLen)
                {
                    // 左侧紧贴一个"1"、拼成 1[3-9]xxxxxxxxx 的是 11 位手机号（OCR 常切成 1+10 位），排除
                    if (IsPhoneWithLeadingOne(orderedWords, w, d)) continue;
                    AddHit(hits, seen, d, w.X, w.Y, w.Right, w.Bottom);
                }
            }

            // 2) 相邻码段组合。普通行只收 3~6 位整词段防杂散数字；
            //    码标签行放宽到 1~6 位（聊天字体 OCR 会把 157/644 切碎），但拼出长度必须正好落在 9~10 位才采纳。
            string segPattern = codeLabelLine ? @"^\d{1,6}$" : @"^\d{3,6}$";
            var segs = orderedWords
                .Where(w => Regex.IsMatch(w.Text, segPattern))
                .ToList();

            for (int i = 0; i < segs.Count; i++)
            {
                var sb = new StringBuilder();
                double? x = null, y = null, rx = null, by = null;
                OcrWord? prev = null;
                for (int j = i; j < segs.Count; j++)
                {
                    var w = segs[j];
                    if (prev != null)
                    {
                        double gap = w.X - prev.Right;
                        double sameLine = Math.Abs(w.CenterY - prev.CenterY);
                        // 码段间距小且水平对齐；离太远说明不是同一串码
                        if (gap > prev.H * 1.6 + 30 || sameLine > prev.H * 0.7) break;
                    }
                    sb.Append(w.Text);
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
        var lines = BuildLines(doc);
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

    public static CodeHit? PickTargetCode(OcrDoc doc, List<CodeHit> hits,
                                          out List<string> candidates)
    {
        candidates = hits.Select(h => h.Code).Distinct().ToList();
        if (hits.Count == 0) return null;
        if (hits.Count == 1) return hits[0];

        var lines = BuildLines(doc);

        // 优先：输入区标签。界面可能有多处含"远程控制"字样（如顶部菜单），
        // 故遍历所有命中标签，只选"正下方 0~130px 内确实有码"的那个，取垂直距离最近的。
        var inputAnchors = FindAllLabels(lines, InputLabelHints);
        CodeHit? inputPick = null;
        double inputPickDy = double.MaxValue;
        foreach (var anc in inputAnchors)
        {
            foreach (var h in hits)
            {
                if (h.CenterY < anc.LabelY - 10) continue;
                double dy = h.CenterY - anc.CenterY;
                if (dy > 140) continue;
                if (dy < inputPickDy) { inputPickDy = dy; inputPick = h; }
            }
        }
        if (inputPick != null) return inputPick;

        // 其次：本机码标签旁边的码
        var local = FindLabel(lines, LocalLabelHints);
        if (local != null)
        {
            var near = hits
                .OrderBy(h => Math.Abs(h.CenterY - local.CenterY)
                              + Math.Abs(h.CenterX - local.LabelX) * 0.2)
                .FirstOrDefault();
            return near;
        }

        // 聊天截图兜底：无界面输入区/本机标签时，按"密码标签邻近/软件名同行"消歧
        var chat = PickChatCode(lines, hits);
        if (chat != null) return chat;

        // 多个码且无任何标签，无法区分 → 交人工选
        return null;
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
