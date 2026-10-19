using System.Security.Cryptography;
using System.Text.Json;
using RemoteConnectHelper.V2.Ocr;

namespace RemoteConnectHelper.V2.Diagnostics;

public static class Benchmark
{
    public sealed record Expected(string Image, string Sha256, string App, string Code, string Password, string Decision);
    public sealed record Manifest(string FrozenAt, string Notes, Expected[] Cases);
    public sealed record Actual(string App, string Code, string Password, string Decision, string Engine, string Variant)
    {
        public int Attempts { get; init; }
        public float? CodeConfidence { get; init; }
        public float? MinimumCodeConfidence { get; init; }
        public bool ConvertedCode { get; init; }
        public double? CandidateGap { get; init; }
        public IReadOnlyList<string> Risks { get; init; } = [];
    }
    public sealed record Row(Expected Expected, Actual? Actual, bool Passed, long Ms, string? Error);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task<int> RunAsync(string folder, string manifestPath, string output)
    {
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes) ?? throw new InvalidDataException("Empty manifest");
        var files = Directory.GetFiles(folder).Where(f => new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (manifest.Cases.Length == 0 || manifest.Cases.Select(c => c.Image).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Cases.Length
            || !files.SetEquals(manifest.Cases.Select(c => c.Image))) throw new InvalidDataException("Manifest must cover every image exactly once");
        foreach (var c in manifest.Cases)
        {
            if (Path.GetFileName(c.Image) != c.Image || c.App is not ("向日葵" or "ToDesk" or "请选择软件")
                || c.Code == null || c.Password == null || c.Decision is not ("Automatic" or "Manual")) throw new InvalidDataException("Incomplete expected case");
            if (!Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(folder, c.Image)))).Equals(c.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Changed image: " + c.Image);
        }
        Directory.CreateDirectory(output);
        using var pipeline = new RecognitionPipeline();
        var rows = new List<Row>();
        foreach (var c in manifest.Cases.OrderBy(c => c.Image, StringComparer.Ordinal))
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var r = await pipeline.AnalyzeAsync(await File.ReadAllBytesAsync(Path.Combine(folder, c.Image)), CancellationToken.None);
                var next = r.DeviceCodes.FirstOrDefault(c => c.Value != r.DeviceCode || c.App != r.App);
                var a = new Actual(r.AppName, r.DeviceCode, r.Password, r.Decision.ToString(), r.Document.Engine, r.Document.Variant)
                {
                    Attempts = r.Attempts.Count, CodeConfidence = r.SelectedCode?.OcrConfidence,
                    MinimumCodeConfidence = r.SelectedCode?.MinimumOcrConfidence, ConvertedCode = r.SelectedCode?.HadCharacterConversion ?? false,
                    CandidateGap = next != null && r.SelectedCode != null ? r.SelectedCode.RankingScore - next.RankingScore : null,
                    Risks = r.Warnings
                };
                bool passed = c.App == a.App && c.Code == a.Code && c.Password == a.Password && c.Decision == a.Decision;
                rows.Add(new(c, a, passed, timer.ElapsedMilliseconds, null));
                Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {c.Image} ({timer.ElapsedMilliseconds}ms)");
                // Full local diagnostic evidence, never copied to public source/release automatically.
                await File.WriteAllTextAsync(Path.Combine(output, c.Image + ".details.json"), JsonSerializer.Serialize(r, Json));
            }
            catch (Exception ex) { rows.Add(new(c, null, false, timer.ElapsedMilliseconds, ex.ToString())); Console.WriteLine("ERROR " + c.Image); }
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, Json));
        }
        int passedCount = rows.Count(r => r.Passed);
        var eligible = rows.Where(r => r.Expected.Decision == "Automatic").ToArray();
        double rate = 100.0 * passedCount / rows.Count;
        var summary = new
        {
            CompletedAt = DateTimeOffset.Now, manifest.FrozenAt, manifest.Notes,
            ManifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)),
            Total = rows.Count, Passed = passedCount, Failed = rows.Count - passedCount, Percent = rate,
            AutomaticEligible = eligible.Length, AutomaticPassed = eligible.Count(r => r.Passed),
            AutomaticPercent = 100.0 * eligible.Count(r => r.Passed) / Math.Max(1, eligible.Length),
            Failures = rows.Where(r => !r.Passed).Select(r => r.Expected.Image).ToArray(),
            ThresholdMet = rate >= 80, Scope = "Offline regression recognition, not live connection success or blind validation"
        };
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, Json));
        await File.WriteAllBytesAsync(Path.Combine(output, "frozen-manifest.json"), manifestBytes);
        Console.WriteLine($"RESULT {passedCount}/{rows.Count} = {rate:F2}%; automatic eligible {eligible.Count(r => r.Passed)}/{eligible.Length}");
        return rate >= 80 ? 0 : 1;
    }
}
