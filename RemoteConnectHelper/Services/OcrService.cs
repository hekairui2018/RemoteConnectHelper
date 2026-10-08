using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using RemoteConnectHelper.Recognition;
// WinRT 与 WPF 存在同名类型（BitmapFrame/BitmapDecoder/OcrWord），这里显式别名到本次要用的实现：
using BitmapSource = System.Windows.Media.Imaging.BitmapSource;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using PngBitmapEncoder = System.Windows.Media.Imaging.PngBitmapEncoder;
using RenderTargetBitmap = System.Windows.Media.Imaging.RenderTargetBitmap;
using OcrWord = RemoteConnectHelper.Recognition.OcrWord;

namespace RemoteConnectHelper.Services;

/// <summary>
/// 调 Windows 自带离线 OCR（Windows.Media.Ocr，免费、离线、毫秒级）。
/// 小图（Win+Shift+S 局部框选常只有几百像素宽）直接识别会把“设备代码/临时密码”等
/// 关键词切碎，导致品牌误判；喂引擎前用 WPF 高质量 Fant 缩放按需放大，
/// 词坐标再按放大倍数还原回原图坐标系（上层相对位置/像素阈值语义不变）。
/// 全程不引第三方库、不打包模型，单 exe 体积几乎不变。
/// </summary>
public static class OcrService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly Lazy<OcrEngine?> _engine = new(TryCreateEngine);

    // 长边小于该值（px）才放大；放大目标长边、最大倍数（避免超大图 OCR 变慢）。
    private const double UpscaleMinLongEdge = 1400;
    private const double UpscaleTargetLongEdge = 1600;
    private const double UpscaleMaxScale = 3.0;

    private static OcrEngine? TryCreateEngine()
    {
        try
        {
            // 优先简体中文；缺中文包时回退系统首选语言（英文界面也能认数字/字母设备码）
            try
            {
                var zh = new Language("zh-Hans-CN");
                if (OcrEngine.IsLanguageSupported(zh))
                    return OcrEngine.TryCreateFromLanguage(zh);
            }
            catch { /* 继续走用户配置语言 */ }
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex)
        {
            Logger.Error("初始化 Windows OCR 引擎失败", ex);
            return null;
        }
    }

    /// <summary>描述本机 OCR 环境（引擎语言、系统可用语言数），写进启动日志便于跨电脑排查识别差异。</summary>
    public static string DescribeEngine()
    {
        try
        {
            int n = OcrEngine.AvailableRecognizerLanguages?.Count ?? 0;
            var eng = _engine.Value;
            string lang = eng?.RecognizerLanguage?.LanguageTag ?? "null(无可用OCR语言)";
            return $"OCR语言={lang},系统可用{n}种";
        }
        catch (Exception ex)
        {
            return $"OCR环境探测失败:{ex.Message}";
        }
    }

    /// <summary>小图高质量放大（Fant 双三次），返回（用于识别的位图，放大倍数）。坐标需按倍数还原。</summary>
    private static (BitmapSource bmp, double scale) MaybeUpscale(BitmapSource src)
    {
        double longEdge = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longEdge >= UpscaleMinLongEdge || longEdge <= 0)
            return (src, 1.0);

        double scale = Math.Min(UpscaleMaxScale, UpscaleTargetLongEdge / longEdge);
        if (scale <= 1.05)
            return (src, 1.0);

        int nw = Math.Max(1, (int)Math.Round(src.PixelWidth * scale));
        int nh = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));

        // WPF 没有 BitmapScaler；用 DrawingVisual + RenderTargetBitmap，并指定 Fant 高质量缩放
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(src, new Rect(0, 0, nw, nh));
        }
        var rtb = new RenderTargetBitmap(nw, nh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return (rtb, scale);
    }

    public static async Task<OcrDoc?> RecognizeAsync(BitmapSource bmp)
    {
        var engine = _engine.Value;
        if (engine == null) return null;

        double origW = bmp.PixelWidth;
        double origH = bmp.PixelHeight;
        var (ocrBmp, scale) = MaybeUpscale(bmp);

        await _gate.WaitAsync(); // Windows OCR 全局只允许一路在跑，串行化（截图预识别 vs 点球识别可能并发）
        try
        {
            // WPF 位图 → PNG 字节 → WinRT 随机访问流 → BitmapDecoder → SoftwareBitmap
            using var ms = new InMemoryRandomAccessStream();
            byte[] pngBytes;
            using (var stm = new MemoryStream())
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(ocrBmp));
                png.Save(stm);
                pngBytes = stm.ToArray();
            }
            using (var dw = new DataWriter(ms))
            {
                dw.WriteBytes(pngBytes);
                await dw.StoreAsync();
                dw.DetachStream();
            }

            // 此处 BitmapDecoder 唯一指向 WinRT 的 Windows.Graphics.Imaging.BitmapDecoder
            var decoder = await BitmapDecoder.CreateAsync(ms);
            using var sbmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                                                                  BitmapAlphaMode.Premultiplied);
            var res = await engine.RecognizeAsync(sbmp);

            var doc = new OcrDoc
            {
                ImgWidth = origW,
                ImgHeight = origH,
                ScaleApplied = scale
            };
            var sb = new StringBuilder();
            foreach (var line in res.Lines)
            {
                sb.AppendLine(line.Text);
                foreach (var word in line.Words) // word 为 WinRT OcrWord（var 推断，不与本地别名冲突）
                {
                    var r = word.BoundingRect;
                    // 放大图坐标 → 原图坐标，保证上层按原图尺寸做邻近/分区判定
                    doc.Words.Add(new OcrWord
                    {
                        Text = word.Text,
                        X = r.X / scale,
                        Y = r.Y / scale,
                        W = r.Width / scale,
                        H = r.Height / scale
                    });
                }
            }
            doc.FullText = sb.ToString();
            // 去掉所有空白得 FlatText（OCR 常把中文拆成单字词，关键词匹配要用它）
            doc.FlatText = string.Concat(doc.FullText.Where(static c => !char.IsWhiteSpace(c)));
            return doc;
        }
        catch (Exception ex)
        {
            Logger.Error("OCR 识别异常", ex);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
