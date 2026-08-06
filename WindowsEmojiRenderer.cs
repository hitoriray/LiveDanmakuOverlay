using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace LiveDanmakuOverlay;

public static class WindowsEmojiRenderer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> Cache = new();
    private static readonly SKTypeface EmojiTypeface =
        SKTypeface.FromFamilyName("Segoe UI Emoji") ?? SKTypeface.Default;

    public static ImageSource? Render(string emoji, double fontSize)
    {
        var key = $"{fontSize:F1}\0{emoji}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var rendered = RenderCore(emoji, fontSize);
        if (rendered is not null) Cache.TryAdd(key, rendered);
        return rendered;
    }

    private static ImageSource? RenderCore(string emoji, double fontSize)
    {
        try
        {
            var scale = 2f;
            var logicalSize = (float)fontSize + 4;
            var pixels = Math.Max(8, (int)Math.Ceiling(logicalSize * scale));
            using var bitmap = new SKBitmap(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint
            {
                Typeface = EmojiTypeface,
                TextSize = (float)fontSize * scale,
                IsAntialias = true,
                Color = SKColors.White
            };
            using var shaper = new SKShaper(EmojiTypeface);
            var result = shaper.Shape(emoji, paint);
            var x = Math.Max(0, (pixels - result.Width) / 2f);
            var metrics = paint.FontMetrics;
            var y = (pixels - (metrics.Descent + metrics.Ascent)) / 2f;
            canvas.DrawShapedText(shaper, emoji, x, y, paint);
            canvas.Flush();

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(encoded.ToArray());
            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.StreamSource = stream;
            source.DecodePixelWidth = pixels;
            source.EndInit();
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    public static int CountOpaqueColors(string emoji, double fontSize)
    {
        using var bitmap = RenderBitmap(emoji, fontSize);
        if (bitmap is null) return 0;
        var colors = new HashSet<uint>();
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.Alpha > 32) colors.Add(((uint)color.Red << 24) | ((uint)color.Green << 16) |
                                              ((uint)color.Blue << 8) | 255u);
        }
        return colors.Count;
    }

    private static SKBitmap? RenderBitmap(string emoji, double fontSize)
    {
        try
        {
            var pixels = Math.Max(8, (int)Math.Ceiling((fontSize + 4) * 2));
            var bitmap = new SKBitmap(pixels, pixels, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Typeface = EmojiTypeface, TextSize = (float)fontSize * 2, IsAntialias = true, Color = SKColors.White };
            using var shaper = new SKShaper(EmojiTypeface);
            var result = shaper.Shape(emoji, paint);
            var metrics = paint.FontMetrics;
            canvas.DrawShapedText(shaper, emoji, Math.Max(0, (pixels - result.Width) / 2f),
                (pixels - (metrics.Descent + metrics.Ascent)) / 2f, paint);
            canvas.Flush();
            return bitmap;
        }
        catch { return null; }
    }
}
