using System.Text.RegularExpressions;
using RemoteConnectHelper.V2.Domain;

namespace RemoteConnectHelper.V2.Parsing;

/// <summary>无图像、无窗口依赖的规则解析器。文字置信度与规则得分分别保留。</summary>
public sealed class RecognitionEngine
{
    private static readonly string[] SunTerms = ["向日葵", "sunlogin", "awesun", "贝锐", "识别码", "验证码", "简约版", "多协议远控", "远程协助他人", "远程协助本机"];
    private static readonly string[] DeskTerms = ["todesk", "设备代码", "临时密码", "临时码", "安全密码", "devicecode", "temporarypassword"];
    private static readonly string[] PwdTerms = ["验证码", "密码", "安全代码", "临时码", "verificationcode", "password"];
    private static readonly string[] Brands = ["todesk", "sunlogin", "awesun", "sunflow", "windows", "oray", "download"];
    private sealed record Token(string Text, Box Layout, Box Original, OcrTextBlock Source);
    private sealed record Row(List<Token> Tokens)
    {
        public Box Bounds => Box.Union(Tokens.Select(t => t.Layout));
        public string Text => string.Join(" ", Tokens.OrderBy(t => t.Layout.X).Select(t => t.Text));
    }

    public RecognitionResult Analyze(OcrDocument document)
    {
        var tokens = Tokenize(document);
        var rows = Rows(tokens);
        var software = DetectSoftware(document, rows);
        var codes = ExtractCodes(rows, software, document);
        var allPasswords = codes.SelectMany(c => ExtractPasswords(tokens, rows, c, software)).ToList();
        // 密码是否与设备码成对，仅影响候选排序，不作为必须有密码才能连接的门槛。
        codes = codes.Select(c => c with { PairingScore = c.RuleScore + Math.Max(0,
            allPasswords.Where(p => p.DeviceCandidateId == c.Id).Select(p => p.RuleScore).DefaultIfEmpty(0).Max() - 60) * 1.2 })
            .OrderByDescending(c => c.RankingScore).ToList();
        return new ConfidenceEngine().Decide(document, software, codes, allPasswords);
    }

    private static List<Token> Tokenize(OcrDocument doc)
    {
        var tokens = new List<Token>();
        foreach (var block in doc.Blocks)
        {
            foreach (Match m in Regex.Matches(block.Text, @"[A-Za-z0-9|×]+|[\p{IsCJKUnifiedIdeographs}]+|[•●*]{3,}|[.:：/\\_-]"))
            {
                // 引擎提供词框时每块通常只有一词；单行块使用字符比例近似子串位置，并保留来源 ID。
                double a = (double)m.Index / Math.Max(1, block.Text.Length), b = (double)m.Length / Math.Max(1, block.Text.Length);
                var layout = block.Layout with { X = block.Layout.X + block.Layout.Width * a, Width = block.Layout.Width * b };
                var original = doc.Rotation == 0 ? block.Bounds with { X = block.Bounds.X + block.Bounds.Width * a, Width = block.Bounds.Width * b } : block.Bounds;
                tokens.Add(new(m.Value, layout, original, block));
            }
        }
        return tokens;
    }

    private static List<Row> Rows(List<Token> tokens)
    {
        var rows = new List<Row>();
        foreach (var token in tokens.OrderBy(t => t.Layout.CenterY))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r.Bounds.CenterY - token.Layout.CenterY)
                <= Math.Max(5, Math.Min(r.Bounds.Height, token.Layout.Height) * .65));
            if (row == null) rows.Add(new([token])); else row.Tokens.Add(token);
        }
        return rows;
    }

    private static List<SoftwareCandidate> DetectSoftware(OcrDocument doc, List<Row> rows)
    {
        var result = new List<SoftwareCandidate>();
        foreach (var row in rows)
        {
            // 只连接相邻文字，不能把两个窗口横跨几百像素的词拼成同一个标签。
            foreach (var group in Clusters(row.Tokens, 35))
            {
                string text = Flat(string.Concat(group.Select(t => t.Text)));
                foreach (var (app, terms) in new[] { (RemoteApp.Sunlogin, SunTerms), (RemoteApp.ToDesk, DeskTerms) })
                {
                    var hits = terms.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (hits.Length == 0) continue;
                    result.Add(new(app, 90, Box.Union(group.Select(t => t.Layout)), group.Select(t => t.Source.Id).Distinct().ToArray(), hits));
                }
            }
        }
        return result;
    }

    private static List<DeviceCodeCandidate> ExtractCodes(List<Row> rows, List<SoftwareCandidate> software, OcrDocument doc)
    {
        var result = new List<DeviceCodeCandidate>();
        foreach (var row in rows)
        {
            var ordered = row.Tokens.OrderBy(t => t.Layout.X).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (!TryDigits(ordered[i].Text, out var first)) continue;
                if (first.Length is < 1 or > 10) continue;
                var parts = new List<Token>(); string value = "";
                for (int j = i; j < ordered.Count && j < i + 10; j++)
                {
                    var current = ordered[j];
                    if (!TryDigits(current.Text, out string digits) || digits.Length > 10) break;
                    if (parts.Count > 0 && current.Layout.X - parts[^1].Layout.Right > Math.Max(35, current.Layout.Height * 1.8)) break;
                    parts.Add(current); value += digits;
                    if (value.Length > 10) break;
                    if (value.Length is not (9 or 10)) continue;
                    if (!parts.Any(t => t.Text.Any(char.IsDigit))) continue;
                    // 不能取手机号的末 9/10 位，也不能取一个未结束的数字串前缀。
                    if (i > 0 && TryDigits(ordered[i - 1].Text, out _) && ordered[i].Layout.X - ordered[i - 1].Layout.Right < ordered[i].Layout.Height * .5) continue;
                    if (j + 1 < ordered.Count && TryDigits(ordered[j + 1].Text, out _) && ordered[j + 1].Layout.X - current.Layout.Right < current.Layout.Height * .5) continue;
                    var bounds = Box.Union(parts.Select(t => t.Layout));
                    string near = NearbyText(rows, bounds, 90);
                    string sameRow = string.Concat(row.Tokens.Where(t => t.Layout.X >= bounds.X - 230 && t.Layout.X <= bounds.Right + 70).Select(t => t.Text));
                    if (Regex.IsMatch(sameRow, @"电话|手机号码|联系电话|QQ|订单|工单|单号|版本", RegexOptions.IgnoreCase)) continue;
                    var app = BindSoftware(bounds, software);
                    if (app == RemoteApp.ToDesk && value.Length != 9) continue;
                    double score = 45;
                    var evidence = new List<string> { "有效长度的数字串" };
                    if (Regex.IsMatch(near, "本机识别码|设备代码|识别码|设备码|账号|帐号")) { score += 28; evidence.Add("设备码标签邻近"); }
                    if (Regex.IsMatch(near, "本机识别码|允许控制本设备|此设备")) { score += 12; evidence.Add("客户本机区域"); }
                    if (Regex.IsMatch(near, "伙伴识别码|请输入设备|输入伙伴")) { score -= 18; evidence.Add("可能是历史输入框"); }
                    if (app != RemoteApp.Unknown) score += 8;
                    var confidence = Average(parts.Select(t => t.Source.Confidence));
                    if (confidence.HasValue) score += (confidence.Value - .5) * 10;
                    if (result.Any(c => c.Value == value && Math.Abs(c.ReadingBounds.CenterY - bounds.CenterY) < 10)) continue;
                    string rawText = string.Concat(parts.Select(t => t.Text));
                    bool converted = rawText != value;
                    if (converted) evidence.Add("包含形近字符转数字，需核对原文");
                    result.Add(new("code-" + result.Count, value, app, Box.Union(parts.Select(t => t.Original)), bounds,
                        confidence, Math.Clamp(score, 0, 100), parts.Select(t => t.Source.Id).Distinct().ToArray(), evidence.ToArray())
                    { RawText = rawText, HadCharacterConversion = converted, MinimumOcrConfidence = Minimum(parts.Select(t => t.Source.Confidence)) });
                }
            }
        }
        return result;
    }

    private static IEnumerable<PasswordCandidate> ExtractPasswords(List<Token> tokens, List<Row> rows,
        DeviceCodeCandidate code, List<SoftwareCandidate> software)
    {
        var found = new List<PasswordCandidate>();
        foreach (var row in rows)
        foreach (var group in Clusters(row.Tokens, 14))
        {
            // 标签不与英文数字拼成密码；密码分词可按实际水平间距重新聚合。
            var runs = new List<List<Token>>();
            foreach (var t in group)
            {
                if (!Regex.IsMatch(t.Text, @"^[A-Za-z0-9×]{1,20}$")) { if (runs.Count > 0 && runs[^1].Count == 0) continue; runs.Add([]); continue; }
                if (runs.Count == 0) runs.Add([]);
                runs[^1].Add(t);
            }
            foreach (var run in runs.Where(r => r.Count > 0))
            {
                string rawText = string.Concat(run.Select(t => t.Text));
                string value = rawText.Replace('×', 'X');
                if (value.Length is < 4 or > 20 || Brands.Any(b => value.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;
                if (value.Any(char.IsDigit) && TryDigits(value, out var number) && number.Length is 9 or 10) continue;
                if (Regex.IsMatch(value, @"\d{9,}")) continue;
                var bounds = Box.Union(run.Select(t => t.Layout));
                double dy = Math.Abs(bounds.CenterY - code.ReadingBounds.CenterY);
                if (dy > Math.Max(240, code.ReadingBounds.Height * 8)) continue;
                var regionApp = BindSoftware(bounds, software);
                if (code.App != RemoteApp.Unknown && regionApp != RemoteApp.Unknown && code.App != regionApp) continue;
                // 标签区域不能横跨整行，否则旁边资源管理器文件名也会被误当成密码。
                var pwdLabels = rows.SelectMany(r => Clusters(r.Tokens, 35))
                    .Where(g => PwdTerms.Any(h => Flat(string.Concat(g.Select(t => t.Text))).Contains(h, StringComparison.OrdinalIgnoreCase)))
                    .Select(g => Box.Union(g.Select(t => t.Layout))).ToList();
                double labelDistance = pwdLabels.Select(label => Distance(label, bounds)).DefaultIfEmpty(double.MaxValue).Min();
                bool anchored = labelDistance < 100;
                bool mixed = value.Any(char.IsDigit) && value.Any(char.IsLetter);
                bool numeric = value.All(char.IsDigit);
                if (numeric && (value.Length is < 4 or > 8)) continue;
                if (!mixed && !anchored) continue;
                double score = 25 - dy * .12;
                var evidence = new List<string>();
                if (mixed) { score += 15; evidence.Add("字母数字混合串"); }
                if (dy <= Math.Max(12, code.ReadingBounds.Height * .8) && bounds.X >= code.ReadingBounds.Right - 3)
                { score += 35; evidence.Add("设备码右侧同一行"); }
                if (Math.Abs(bounds.X - code.ReadingBounds.X) < Math.Max(60, code.ReadingBounds.Width * .5) && dy < 150)
                { score += 25; evidence.Add("与设备码同列相邻"); }
                if (anchored) { score += 40 - labelDistance * .12; evidence.Add("密码标签邻近"); }
                if (!anchored && bounds.X < code.ReadingBounds.X - 70) score -= 35;
                if (score < 20) continue;
                var confidence = Average(run.Select(t => t.Source.Confidence));
                if (confidence.HasValue) score += (confidence.Value - .5) * 10;
                found.Add(new(value, code.Id, code.App, Box.Union(run.Select(t => t.Original)), confidence,
                    Math.Clamp(score, 0, 100), run.Select(t => t.Source.Id).Distinct().ToArray(), evidence.ToArray())
                { RawText = rawText, HadCharacterConversion = rawText != value, MinimumOcrConfidence = Minimum(run.Select(t => t.Source.Confidence)) });
            }
        }
        return found.GroupBy(p => p.Value).Select(g => g.MaxBy(p => p.RuleScore)!).OrderByDescending(p => p.RuleScore);
    }

    private static IEnumerable<List<Token>> Clusters(IEnumerable<Token> tokens, double gap)
    {
        List<Token> current = [];
        foreach (var token in tokens.OrderBy(t => t.Layout.X))
        {
            if (current.Count > 0 && token.Layout.X - current[^1].Layout.Right > Math.Max(gap, token.Layout.Height * .85))
            { yield return current; current = []; }
            current.Add(token);
        }
        if (current.Count > 0) yield return current;
    }
    private static string NearbyText(List<Row> rows, Box bounds, double distance)
        => string.Join("", rows.Where(r => Math.Abs(r.Bounds.CenterY - bounds.CenterY) <= distance)
            .SelectMany(r => r.Tokens).Where(t => t.Layout.Right >= bounds.X - 220 && t.Layout.X <= bounds.Right + 100).Select(t => t.Text));
    private static double Distance(Box a, Box b)
        => Math.Abs(a.CenterY - b.CenterY) + Math.Max(0, Math.Abs(a.CenterX - b.CenterX) - (a.Width + b.Width) / 2) * .5;
    private static RemoteApp BindSoftware(Box bounds, List<SoftwareCandidate> candidates)
    {
        var apps = candidates.Select(s => s.App).Distinct().ToList();
        if (apps.Count == 1) return apps[0];
        if (apps.Count == 0) return RemoteApp.Unknown;
        var nearest = candidates.GroupBy(s => s.App).Select(g => (App: g.Key,
            Distance: g.Min(s => Math.Abs(s.Bounds.CenterY - bounds.CenterY) + Math.Abs(s.Bounds.CenterX - bounds.CenterX) * 1.6)))
            .OrderBy(s => s.Distance).ToList();
        return nearest[1].Distance - nearest[0].Distance >= 45 ? nearest[0].App : RemoteApp.Unknown;
    }
    private static float? Average(IEnumerable<float?> values)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return known.Length > 0 ? known.Average() : null;
    }
    private static float? Minimum(IEnumerable<float?> values)
    {
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return known.Length > 0 ? known.Min() : null;
    }
    private static string Flat(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static bool TryDigits(string text, out string value)
    {
        value = "";
        if (text.Length == 0) return false;
        foreach (char c in text)
        {
            char mapped = c switch { 'O' or 'o' => '0', 'I' or 'l' or '|' => '1', 'S' => '5', 'Z' => '2', 'B' => '8', _ => c };
            if (mapped is < '0' or > '9') { value = ""; return false; }
            value += mapped;
        }
        return true;
    }
}
