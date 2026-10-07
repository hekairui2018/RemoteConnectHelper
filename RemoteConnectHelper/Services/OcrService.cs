using System.Threading;
using System.IO;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using RemoteConnectHelper.Recognition;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinBitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;
using WinBitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;

namespace RemoteConnectHelper.Services;

/// <summary>Windows 自带离线 OCR（zh-Hans-CN），把截图转成带坐标的词列表。</summary>
public static class OcrService
{
    private static readonly Lazy<OcrEngine?> _engine = new(() =>
    {
        try
        {
            // 优先简体中文；没有中文语言包则退回用户配置语言
            var zh = new Language("zh-Hans-CN");
            if (OcrEngine.IsLanguageSupported(zh))
                return OcrEngine.TryCreateFromLanguage(zh);
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
    });

    private static readonly SemaphoreSlim _gate = new(1, 1);

    public static async Task<OcrDoc?> RecognizeAsync(BitmapSource bmp)
    {
        var engine = _engine.Value;
        if (engine == null) return null;

        // 同一个 OcrEngine 同一时刻只允许一个 RecognizeAsync。识别入口有多个
        // （热键 / 双击桌面图标 / 剪贴板自动预检 / 向日葵连接中定位），连点或截图时会并发。
        // 用一把异步锁把所有 OCR 全局串行化，彻底杜绝
        // “Another RecognizeAsync operation is already running / 已中止操作”。
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // WPF BitmapSource -> PNG -> SoftwareBitmap
            using var ms = new MemoryStream();
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(WpfBitmapFrame.Create(bmp));
            enc.Save(ms);
            ms.Position = 0;
            using var ras = ms.AsRandomAccessStream();

            var decoder = await WinBitmapDecoder.CreateAsync(ras);
            using var sbmp = await decoder.GetSoftwareBitmapAsync(WinBitmapPixelFormat.Bgra8,
                                                                 WinBitmapAlphaMode.Premultiplied);
            var res = await engine.RecognizeAsync(sbmp);

            var doc = new OcrDoc
            {
                ImgWidth = sbmp.PixelWidth,
                ImgHeight = sbmp.PixelHeight,
            };
            var sb = new System.Text.StringBuilder();
            foreach (var line in res.Lines)
            {
                foreach (var w in line.Words)
                {
                    var r = w.BoundingRect;
                    doc.Words.Add(new RemoteConnectHelper.Recognition.OcrWord
                    {
                        Text = w.Text,
                        X = r.X, Y = r.Y, W = r.Width, H = r.Height,
                    });
                    sb.Append(w.Text).Append(' ');
                }
                sb.AppendLine();
            }
            doc.FullText = sb.ToString();
            doc.FlatText = System.Text.RegularExpressions.Regex.Replace(doc.FullText, @"\s+", "");
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
