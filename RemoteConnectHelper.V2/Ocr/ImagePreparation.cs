using System.Runtime.InteropServices;
using RemoteConnectHelper.V2.Domain;
using SkiaSharp;

namespace RemoteConnectHelper.V2.Ocr;

public static class ImagePreparation
{
    public static SKBitmap Prepare(byte[] data, int rotation, bool contrast)
    {
        using var original = SKBitmap.Decode(data) ?? throw new InvalidDataException("图片无法解码");
        bool swap = rotation is 90 or 270;
        var result = new SKBitmap(swap ? original.Height : original.Width, swap ? original.Width : original.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.White);
            switch (rotation)
            {
                case 90: canvas.Translate(original.Height, 0); break;
                case 180: canvas.Translate(original.Width, original.Height); break;
                case 270: canvas.Translate(0, original.Width); break;
            }
            canvas.RotateDegrees(rotation);
            canvas.DrawBitmap(original, 0, 0);
        }
        if (contrast)
        {
            var pixels = new byte[result.ByteCount];
            Marshal.Copy(result.GetPixels(), pixels, 0, pixels.Length);
            for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
            {
                int p = y * result.RowBytes + x * 4;
                byte value = (pixels[p] * 114 + pixels[p + 1] * 587 + pixels[p + 2] * 299) / 1000 >= 180 ? (byte)255 : (byte)0;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = value; pixels[p + 3] = 255;
            }
            Marshal.Copy(pixels, 0, result.GetPixels(), pixels.Length);
        }
        return result;
    }

    public static Box ToOriginal(Box box, int rotation, int width, int height)
    {
        var corners = new[] { (box.X, box.Y), (box.Right, box.Y), (box.Right, box.Bottom), (box.X, box.Bottom) };
        var mapped = corners.Select(p => rotation switch
        {
            90 => (X: p.Item2, Y: height - p.Item1),
            180 => (X: width - p.Item1, Y: height - p.Item2),
            270 => (X: width - p.Item2, Y: p.Item1),
            _ => (X: p.Item1, Y: p.Item2)
        }).ToArray();
        double x = Math.Clamp(mapped.Min(p => p.X), 0, width), y = Math.Clamp(mapped.Min(p => p.Y), 0, height);
        return new(x, y, Math.Clamp(mapped.Max(p => p.X), x, width) - x, Math.Clamp(mapped.Max(p => p.Y), y, height) - y);
    }
}
