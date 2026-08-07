using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace LiveDanmakuOverlay;

public static class WindowsEmojiRenderer
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> Cache = new();
    private static readonly SemaphoreSlim RenderSlots = new(2, 2);
    private static readonly SemaphoreSlim RenderQueueSlots = new(64, 64);
    private static readonly SKTypeface EmojiTypeface =
        SKTypeface.FromFamilyName("Segoe UI Emoji") ?? SKTypeface.Default;

    public static ImageSource? Render(string emoji, double fontSize)
    {
        return GetOrRenderAsync(emoji, fontSize).GetAwaiter().GetResult();
    }

    public static Task<ImageSource?> GetOrRenderAsync(string emoji, double fontSize)
    {
        var key = CacheKey(emoji, fontSize);
        Lazy<Task<ImageSource?>>? candidate = null;
        candidate = new Lazy<Task<ImageSource?>>(
            () => RenderAndCacheAsync(key, emoji, fontSize, candidate!), LazyThreadSafetyMode.ExecutionAndPublication);
        return Cache.GetOrAdd(key, candidate).Value;
    }

    internal static bool TryGetCached(string emoji, double fontSize, out ImageSource? source)
    {
        if (Cache.TryGetValue(CacheKey(emoji, fontSize), out var entry) && entry.IsValueCreated &&
            entry.Value.IsCompletedSuccessfully && entry.Value.Result is { } cached)
        {
            source = cached;
            return true;
        }
        source = null;
        return false;
    }

    internal static void Invalidate(string emoji, double fontSize) =>
        Cache.TryRemove(CacheKey(emoji, fontSize), out _);

    private static string CacheKey(string emoji, double fontSize) => $"{fontSize:F1}\0{emoji}";

    private static async Task<ImageSource?> RenderAndCacheAsync(string key, string emoji, double fontSize,
        Lazy<Task<ImageSource?>> owner)
    {
        if (!RenderQueueSlots.Wait(0))
        {
            RemoveIfOwner(key, owner);
            return null;
        }
        try
        {
            await RenderSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                var rendered = await Task.Run(() => RenderCore(emoji, fontSize)).ConfigureAwait(false);
                if (rendered is null) RemoveIfOwner(key, owner);
                return rendered;
            }
            finally
            {
                RenderSlots.Release();
            }
        }
        catch
        {
            RemoveIfOwner(key, owner);
            return null;
        }
        finally
        {
            RenderQueueSlots.Release();
        }
    }

    private static void RemoveIfOwner(string key, Lazy<Task<ImageSource?>> owner) =>
        ((ICollection<KeyValuePair<string, Lazy<Task<ImageSource?>>>>)Cache)
        .Remove(new KeyValuePair<string, Lazy<Task<ImageSource?>>>(key, owner));

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
