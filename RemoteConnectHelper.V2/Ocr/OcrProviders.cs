using System.Diagnostics;
using System.Windows.Media.Imaging;
using RapidOcrNet;
using RemoteConnectHelper.V2.Domain;
using SkiaSharp;

namespace RemoteConnectHelper.V2.Ocr;

public interface IOcrProvider : IDisposable
{
    string Name { get; }
    Task<OcrDocument> ReadAsync(byte[] image, int rotation, bool contrast, CancellationToken cancellationToken);
}

public sealed class RapidOcrProvider : IOcrProvider
{
    private readonly Lazy<RapidOcr> _engine = new(() =>
    {
        var engine = new RapidOcr();
        string root = AppContext.BaseDirectory;
        try
        {
            engine.InitModels(Path.Combine(root, "models/v5/ch_PP-OCRv5_mobile_det.onnx"),
                Path.Combine(root, "models/v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
                Path.Combine(root, "Models/ch_PP-OCRv5_rec_mobile.onnx"), Path.Combine(root, "Models/ppocrv5_dict.txt"),
                numThread: Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            return engine;
        }
        catch { engine.Dispose(); throw; }
    });
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Name => "RapidOCR · PP-OCRv5 中文";
    public async Task<OcrDocument> ReadAsync(byte[] image, int rotation, bool contrast, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                using var original = SKBitmap.Decode(image) ?? throw new InvalidDataException("图片无法解码");
                using var bitmap = ImagePreparation.Prepare(image, rotation, contrast);
                var options = RapidOcrOptions.Default with { ReturnWordBox = true, TextScore = .35f, ImgResize = 1600, MaxSideLen = 2400 };
                var result = _engine.Value.Detect(bitmap, options, cancellationToken);
                var blocks = new List<OcrTextBlock>();
                string variant = (contrast ? "高对比" : "原图") + "/" + rotation;
                foreach (var line in result.TextBlocks)
                {
                    if (line.WordResults is { Length: > 0 })
                    {
                        foreach (var word in line.WordResults)
                            Add(word.Text, word.BoxPoints, word.Score);
                    }
                    else Add(line.Text, line.BoxPoints, line.CharScores is { Length: > 0 } ? line.CharScores.Average() : null);
                }
                void Add(string text, SKPointI[] polygon, float? confidence)
                {
                    if (string.IsNullOrWhiteSpace(text) || polygon.Length == 0) return;
                    var box = new Box(polygon.Min(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.X) - polygon.Min(p => p.X), polygon.Max(p => p.Y) - polygon.Min(p => p.Y));
                    blocks.Add(new("rapid-" + blocks.Count, text, ImagePreparation.ToOriginal(box, rotation, original.Width, original.Height),
                        confidence, Name, variant, box));
                }
                return new OcrDocument(blocks, original.Width, original.Height, Name, variant, rotation, timer.ElapsedMilliseconds);
            }, cancellationToken);
        }
        finally { _gate.Release(); }
    }
    public void Dispose() { if (_engine.IsValueCreated) _engine.Value.Dispose(); _gate.Dispose(); }
}

public sealed class WindowsOcrProvider : IOcrProvider
{
    public string Name => "Windows OCR（兜底）";
    public async Task<OcrDocument> ReadAsync(byte[] image, int rotation, bool contrast, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var timer = Stopwatch.StartNew();
            using var prepared = ImagePreparation.Prepare(image, rotation, contrast);
            using var encoded = prepared.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream; source.EndInit(); source.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RemoteConnectHelper.Services.OcrService.RecognizeAsync(source);
            cancellationToken.ThrowIfCancellationRequested();
            using var original = SKBitmap.Decode(image);
            string variant = (contrast ? "高对比" : "原图") + "/" + rotation;
            var blocks = result?.Words.Select((w, i) =>
            {
                var box = new Box(w.X, w.Y, w.W, w.H);
                return new OcrTextBlock("windows-" + i, w.Text, ImagePreparation.ToOriginal(box, rotation, original.Width, original.Height), null, Name, variant, box);
            }).ToArray() ?? [];
            return new OcrDocument(blocks, original.Width, original.Height, Name, variant, rotation, timer.ElapsedMilliseconds);
        }, cancellationToken);
    }
    public void Dispose() { }
}
