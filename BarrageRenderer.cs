using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DxgiFormat = Vortice.DXGI.Format;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace LiveDanmakuOverlay;

public sealed class BarrageRenderer : IDisposable
{
    public const double BaseScrollSpeed = 300;
    private readonly ConcurrentQueue<PendingMessage> _pending = new();
    private readonly ConcurrentDictionary<string, long> _recentMessages = new();
    private readonly NativeCompositionOverlay _overlay;
    private readonly DispatcherTimer _statisticsTimer;
    private int _pendingCount, _lastReportedPendingCount = -1;
    private long _totalAccepted, _totalLaunched, _totalMerged, _totalExpired;
    private DateTime _lastDuplicateCleanup = DateTime.UtcNow;
    private volatile bool _enabled = true;
    private bool _disposed;

    public static double PercentToPxPerSecond(double percent) => BaseScrollSpeed * Math.Clamp(percent, 5, 200) / 100.0;
    public double ScrollSpeed => _overlay.ScrollSpeed;
    public int PendingCount => Volatile.Read(ref _pendingCount);
    public long TotalAccepted => Interlocked.Read(ref _totalAccepted);
    public long TotalLaunched => Interlocked.Read(ref _totalLaunched);
    public long TotalMerged => Interlocked.Read(ref _totalMerged);
    public long TotalExpired => Interlocked.Read(ref _totalExpired);
    public double FreshnessSeconds { get; set; } = 1;
    public double DuplicateWindowSeconds { get; set; } = 2;
    internal int ActiveCount => _overlay.ActiveCount;
    internal bool UsesDirectComposition => true;
    internal bool OverlayAcceptsInput => _overlay.AcceptsInput;
    public event EventHandler<int>? PendingCountChanged;
    public event EventHandler<BarrageStatistics>? StatisticsChanged;

    public BarrageRenderer(double fontSize, double scrollSpeed, double contentOpacity = 1, Dispatcher? dispatcher = null)
    {
        var uiDispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _overlay = new NativeCompositionOverlay(TakePending, fontSize, scrollSpeed, contentOpacity, uiDispatcher,
            count => Interlocked.Add(ref _totalLaunched, count), count => Interlocked.Add(ref _totalExpired, count));
        _statisticsTimer = new DispatcherTimer(DispatcherPriority.Background, uiDispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _statisticsTimer.Tick += StatisticsTimer_Tick;
        _statisticsTimer.Start();
    }

    public void Enqueue(DanmakuMessage message)
    {
        Interlocked.Increment(ref _totalAccepted);
        if (!_enabled) { Interlocked.Increment(ref _totalExpired); return; }
        NativeCompositionOverlay.PrimeEmoticon(message.EmoticonUrl);
        var nowTicks = DateTime.UtcNow.Ticks;
        var key = MessageFilter.Normalize(message.Text);
        if (key.Length > 0 && _recentMessages.TryGetValue(key, out var previous) &&
            TimeSpan.FromTicks(nowTicks - previous).TotalSeconds <= DuplicateWindowSeconds)
        {
            _recentMessages[key] = nowTicks;
            Interlocked.Increment(ref _totalMerged);
            return;
        }
        if (key.Length > 0) _recentMessages[key] = nowTicks;
        _pending.Enqueue(new PendingMessage(message, DateTime.UtcNow));
        Interlocked.Increment(ref _pendingCount);
        _overlay.Wake();
    }

    public void SetBounds(int x, int y, int width, int height, bool visible) => _overlay.SetBounds(x, y, width, height, visible && _enabled);
    public void SetFontSize(double value) => _overlay.SetFontSize(value);
    public void SetScrollSpeed(double value) => _overlay.SetScrollSpeed(value);
    public void SetContentOpacity(double value) => _overlay.SetContentOpacity(value);
    public void SetDensity(DanmakuDensity value) => _overlay.SetDensity(value);
    public void RefreshLanes() => _overlay.RefreshLanes();
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _overlay.SetEnabled(enabled);
        if (enabled) return;
        while (_pending.TryDequeue(out _)) { Interlocked.Decrement(ref _pendingCount); Interlocked.Increment(ref _totalExpired); }
        ReportStatistics();
    }

    private IReadOnlyList<PendingMessage> TakePending(int maximum)
    {
        var result = new List<PendingMessage>(maximum);
        var now = DateTime.UtcNow;
        while (_pending.TryPeek(out var item))
        {
            if ((now - item.ReceivedAt).TotalSeconds > FreshnessSeconds)
            {
                if (_pending.TryDequeue(out _)) { Interlocked.Decrement(ref _pendingCount); Interlocked.Increment(ref _totalExpired); }
                continue;
            }
            if (result.Count >= maximum) break;
            if (!_pending.TryDequeue(out item)) break;
            Interlocked.Decrement(ref _pendingCount);
            result.Add(item);
        }
        return result;
    }

    private void StatisticsTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDuplicateCleanup).TotalSeconds >= 5)
        {
            _lastDuplicateCleanup = now;
            var cutoff = now.AddSeconds(-Math.Max(5, DuplicateWindowSeconds * 2)).Ticks;
            foreach (var pair in _recentMessages) if (pair.Value < cutoff) _recentMessages.TryRemove(pair.Key, out _);
        }
        ReportStatistics();
    }

    private void ReportStatistics()
    {
        var count = PendingCount;
        if (count != _lastReportedPendingCount) { _lastReportedPendingCount = count; PendingCountChanged?.Invoke(this, count); }
        StatisticsChanged?.Invoke(this, new BarrageStatistics(TotalAccepted, TotalLaunched, TotalMerged, TotalExpired,
            count, 1, _overlay.FramesPerSecond, _overlay.AverageDrawMilliseconds));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statisticsTimer.Stop();
        _statisticsTimer.Tick -= StatisticsTimer_Tick;
        _overlay.Dispose();
    }

    internal sealed record PendingMessage(DanmakuMessage Message, DateTime ReceivedAt);
}

internal sealed class NativeCompositionOverlay : IDisposable
{
    private const uint WsPopup = 0x80000000,
        WsExTopmost = 8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x00080000,
        WsExNoActivate = 0x08000000, WsExNoRedirectionBitmap = 0x00200000, SwpNoActivate = 0x10, SwpShowWindow = 0x40,
        PmRemove = 1;
    private const uint WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int SwHide = 0, SwShowNoActivate = 4;
    private const uint LwaAlpha = 0x00000002;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly SKTypeface TextTypeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Bold) ?? SKTypeface.Default;
    private static readonly SKTypeface EmojiTypeface = SKTypeface.FromFamilyName("Segoe UI Emoji") ?? SKTypeface.Default;
    private static readonly HttpClient EmoticonHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> EmoticonCache = new(StringComparer.Ordinal);
    private static readonly WndProc WindowProcedure = (hwnd, message, wParam, lParam) =>
        message == WmNcHitTest ? new IntPtr(HtTransparent) : DefWindowProc(hwnd, message, wParam, lParam);
    private readonly Func<int, IReadOnlyList<BarrageRenderer.PendingMessage>> _takePending;
    private readonly Action<int> _launched, _expired;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private readonly Thread _thread;
    private readonly Dispatcher _uiDispatcher;
    private readonly object _stateLock = new();
    private BoundsState _bounds;
    private bool _boundsDirty, _fontSizeDirty, _enabled = true, _disposed;
    private double _fontSize, _scrollSpeed, _opacity, _framesPerSecond, _averageDrawMilliseconds;
    private DanmakuDensity _density = DanmakuDensity.Standard;
    private int _activeCount;
    private IntPtr _windowHandle;
    private Exception? _startupError;
    public int ActiveCount => Volatile.Read(ref _activeCount);
    public double FramesPerSecond => Volatile.Read(ref _framesPerSecond);
    public double AverageDrawMilliseconds => Volatile.Read(ref _averageDrawMilliseconds);
    public double ScrollSpeed { get { lock (_stateLock) return _scrollSpeed; } }
    public bool AcceptsInput => _windowHandle != IntPtr.Zero && IsWindowEnabled(_windowHandle);

    public NativeCompositionOverlay(Func<int, IReadOnlyList<BarrageRenderer.PendingMessage>> takePending,
        double fontSize, double scrollSpeed, double opacity, Dispatcher uiDispatcher,
        Action<int> launched, Action<int> expired)
    {
        _takePending = takePending; _fontSize = fontSize; _scrollSpeed = scrollSpeed; _opacity = opacity;
        _uiDispatcher = uiDispatcher;
        _launched = launched; _expired = expired;
        _windowHandle = _uiDispatcher.CheckAccess()
            ? CreateOverlayWindow()
            : _uiDispatcher.Invoke(CreateOverlayWindow);
        _thread = new Thread(RenderThread) { IsBackground = true, Name = "DirectComposition Danmaku" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("DirectComposition 弹幕线程启动超时");
        if (_startupError is not null) throw new InvalidOperationException("DirectComposition 初始化失败", _startupError);
    }

    public void Wake() => _wake.Set();
    public static void PrimeEmoticon(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) _ = EmoticonCache.GetOrAdd(url, DownloadEmoticonAsync);
    }
    public void SetBounds(int x, int y, int width, int height, bool visible)
    {
        var requested = new BoundsState(x, y, Math.Max(1, width), Math.Max(1, height), visible);
        lock (_stateLock)
        {
            if (_bounds == requested) return;
            _bounds = requested;
            _boundsDirty = true;
        }
        _wake.Set();
    }
    public void SetFontSize(double value) { lock (_stateLock) { _fontSize = value; _fontSizeDirty = true; } _wake.Set(); }
    public void SetScrollSpeed(double value) { lock (_stateLock) _scrollSpeed = value; _wake.Set(); }
    public void SetContentOpacity(double value) { lock (_stateLock) _opacity = Math.Clamp(value, .1, 1); _wake.Set(); }
    public void SetDensity(DanmakuDensity value) { lock (_stateLock) _density = value; _wake.Set(); }
    public void RefreshLanes() => _wake.Set();
    public void SetEnabled(bool value) { lock (_stateLock) _enabled = value; _wake.Set(); }

    private void RenderThread()
    {
        var hwnd = _windowHandle;
        try
        {
            using var graphics = new CompositionGraphics();
            graphics.Initialize(hwnd, 1, 1);
            _started.Set();
            var active = new List<ActiveBarrage>();
            var lanes = new List<ActiveBarrage?>();
            BoundsState current = default;
            var lastFrame = Stopwatch.GetTimestamp();
            var statsStart = lastFrame;
            var frames = 0;
            long drawTicks = 0;
            while (!_disposed)
            {
                bool boundsDirty, fontSizeDirty, enabled; double fontSize, speed, opacity; DanmakuDensity density; BoundsState requested;
                lock (_stateLock)
                {
                    boundsDirty = _boundsDirty; fontSizeDirty = _fontSizeDirty; requested = _bounds; enabled = _enabled;
                    fontSize = _fontSize; speed = _scrollSpeed; opacity = _opacity; density = _density;
                    _boundsDirty = _fontSizeDirty = false;
                }
                if (boundsDirty)
                {
                    var sizeChanged = RequiresSurfaceResize(current.Width, current.Height,
                        requested.Width, requested.Height);
                    SetWindowPos(hwnd, HwndTopmost, requested.X, requested.Y, requested.Width, requested.Height,
                        SwpNoActivate | (requested.Visible ? SwpShowWindow : 0));
                    ShowWindow(hwnd, requested.Visible ? SwShowNoActivate : SwHide);
                    if (sizeChanged)
                        graphics.Resize(requested.Width, requested.Height);
                    current = requested;
                }
                if (fontSizeDirty) RebuildActiveTextures(active, graphics, fontSize);
                if (!enabled || !current.Visible)
                { if (active.Count > 0) Clear(active, lanes); Volatile.Write(ref _activeCount, 0); _wake.WaitOne(100); lastFrame = Stopwatch.GetTimestamp(); continue; }

                var now = Stopwatch.GetTimestamp();
                var delta = Math.Min(.1, Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds); lastFrame = now;
                var spacing = GetSpacing(density);
                for (var i = 0; i < active.Count; i++)
                {
                    var item = active[i];
                    item.X -= CalculateSpeed(current.Width, item.Width, speed) * delta;
                    if (item.Leader is not null && active.Contains(item.Leader))
                        item.X = Math.Max(item.X, item.Leader.X + item.Leader.Width + spacing.Horizontal);
                }
                for (var i = active.Count - 1; i >= 0; i--)
                {
                    var item = active[i];
                    if (item.X + item.Width >= 0) continue;
                    active.RemoveAt(i);
                    if (item.Lane < lanes.Count && ReferenceEquals(lanes[item.Lane], item)) lanes[item.Lane] = null;
                    item.Texture.Dispose();
                }
                foreach (var item in active)
                    TryUpgradeEmoticon(item, graphics, fontSize);
                var laneHeight = Math.Max(1, fontSize + spacing.Vertical);
                var required = Math.Max(1, (int)Math.Floor(current.Height / laneHeight));
                while (lanes.Count < required) lanes.Add(null);
                var launched = 0;
                while (launched < 4)
                {
                    var lane = FindLane(lanes, required, active, current.Width, spacing.Horizontal);
                    if (lane < 0) { _takePending(0); break; }
                    var next = _takePending(1);
                    if (next.Count == 0) break;
                    var pending = next[0];
                    using var pixels = CreateInitialTexture(pending.Message, fontSize, out var awaitingEmoticon);
                    var item = new ActiveBarrage(graphics.CreateSprite(pixels), pixels.Width, pixels.Height,
                        current.Width, lane, lanes[lane], pending.Message, awaitingEmoticon);
                    active.Add(item); lanes[lane] = item; launched++;
                }
                if (launched > 0) _launched(launched);
                Volatile.Write(ref _activeCount, active.Count);
                drawTicks += graphics.Draw(active, laneHeight, opacity); frames++;
                var elapsed = Stopwatch.GetElapsedTime(statsStart, Stopwatch.GetTimestamp());
                if (elapsed.TotalSeconds >= 1)
                {
                    Volatile.Write(ref _framesPerSecond, frames / elapsed.TotalSeconds);
                    Volatile.Write(ref _averageDrawMilliseconds, frames == 0 ? 0 : drawTicks * 1000.0 / Stopwatch.Frequency / frames);
                    statsStart = Stopwatch.GetTimestamp(); frames = 0; drawTicks = 0;
                }
            }
            Clear(active, lanes);
        }
        catch (Exception ex) { _startupError = ex; _started.Set(); }
        finally { }
    }

    internal static double CalculateSpeed(double viewportWidth, double textWidth, double selectedPixelsPerSecond)
    {
        var duration = 1920.0 / Math.Max(1, selectedPixelsPerSecond);
        return (Math.Max(1, viewportWidth) + Math.Max(1, textWidth)) / duration;
    }

    internal static bool RequiresSurfaceResize(int oldWidth, int oldHeight, int newWidth, int newHeight) =>
        oldWidth != newWidth || oldHeight != newHeight;

    private static DensitySpacing GetSpacing(DanmakuDensity density) => density switch
    {
        DanmakuDensity.Comfortable => new DensitySpacing(18, 72),
        DanmakuDensity.Dense => new DensitySpacing(7, 24),
        _ => new DensitySpacing(13, 48)
    };

    private static int FindLane(List<ActiveBarrage?> lanes, int availableLaneCount,
        List<ActiveBarrage> active, int width, double horizontalGap)
    { for (var i = 0; i < availableLaneCount; i++) { var last = lanes[i]; if (last is null || !active.Contains(last) || last.X + last.Width + horizontalGap < width) return i; } return -1; }

    private static SKBitmap RenderTextTexture(string text, double requestedSize)
    {
        var size = (float)requestedSize; var runs = BuildRuns(text, size);
        using var metricsPaint = CreatePaint(TextTypeface, size, SKPaintStyle.Fill, SKColors.White);
        var metrics = metricsPaint.FontMetrics;
        var bitmap = new SKBitmap(Math.Max(1, (int)Math.Ceiling(runs.Sum(x => x.Width) + 8)),
            Math.Max(1, (int)Math.Ceiling(metrics.Descent - metrics.Ascent + 8)), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent); var x = 4f; var baseline = 4f - metrics.Ascent;
        foreach (var run in runs)
        {
            using var shaper = new SKShaper(run.Typeface);
            using var outline = CreatePaint(run.Typeface, size, SKPaintStyle.Stroke, SKColors.Black); outline.StrokeWidth = 2.4f; outline.StrokeJoin = SKStrokeJoin.Round;
            using var fill = CreatePaint(run.Typeface, size, SKPaintStyle.Fill, SKColors.White);
            canvas.DrawShapedText(shaper, run.Text, x, baseline, outline); canvas.DrawShapedText(shaper, run.Text, x, baseline, fill); x += run.Width;
        }
        canvas.Flush(); return bitmap;
    }

    private static SKBitmap CreateInitialTexture(DanmakuMessage message, double fontSize, out bool awaitingEmoticon)
    {
        if (TryRenderEmoticon(message, fontSize, out var emoticon, out awaitingEmoticon)) return emoticon!;
        return RenderTextTexture(message.Text, fontSize);
    }

    private static void TryUpgradeEmoticon(ActiveBarrage item, CompositionGraphics graphics, double fontSize)
    {
        if (!item.AwaitingEmoticon) return;
        if (!TryRenderEmoticon(item.Message, fontSize, out var bitmap, out var awaiting))
        {
            item.AwaitingEmoticon = awaiting;
            return;
        }
        using (bitmap)
        {
            var replacement = graphics.CreateSprite(bitmap!);
            var previous = item.Texture;
            item.Texture = replacement;
            item.Width = bitmap!.Width;
            item.Height = bitmap.Height;
            item.AwaitingEmoticon = false;
            previous.Dispose();
        }
    }

    private static bool TryRenderEmoticon(DanmakuMessage message, double fontSize, out SKBitmap? result,
        out bool awaiting)
    {
        result = null;
        awaiting = false;
        if (string.IsNullOrWhiteSpace(message.EmoticonUrl)) return false;
        var task = EmoticonCache.GetOrAdd(message.EmoticonUrl, DownloadEmoticonAsync);
        if (!task.IsCompleted)
        {
            awaiting = true;
            return false;
        }
        byte[]? bytes;
        try { bytes = task.GetAwaiter().GetResult(); }
        catch { return false; }
        if (bytes is null) return false;
        result = RenderEmoticonBitmap(bytes, message.EmoticonWidth, message.EmoticonHeight, fontSize);
        return result is not null;
    }

    internal static SKBitmap? RenderEmoticonBitmap(byte[] bytes, int declaredWidth, int declaredHeight,
        double fontSize)
    {
        using var source = SKBitmap.Decode(bytes);
        if (source is null || source.Width <= 0 || source.Height <= 0) return null;
        var targetHeight = Math.Max(1, (int)Math.Ceiling(fontSize + 8));
        var ratio = declaredWidth > 0 && declaredHeight > 0
            ? Math.Clamp((double)declaredWidth / declaredHeight, .5, 5)
            : Math.Clamp((double)source.Width / source.Height, .5, 5);
        var targetWidth = Math.Max(1, (int)Math.Round(targetHeight * ratio));
        var result = new SKBitmap(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(source, new SKRect(0, 0, targetWidth, targetHeight), paint);
        canvas.Flush();
        return result;
    }

    private static async Task<byte[]?> DownloadEmoticonAsync(string url)
    {
        try { return await EmoticonHttp.GetByteArrayAsync(url).ConfigureAwait(false); }
        catch { return null; }
    }

    private static void RebuildActiveTextures(IEnumerable<ActiveBarrage> active, CompositionGraphics graphics,
        double fontSize)
    {
        foreach (var item in active)
        {
            using var bitmap = CreateInitialTexture(item.Message, fontSize, out var awaitingEmoticon);
            var replacement = graphics.CreateSprite(bitmap);
            var previous = item.Texture;
            item.Texture = replacement;
            item.Width = bitmap.Width;
            item.Height = bitmap.Height;
            item.AwaitingEmoticon = awaitingEmoticon;
            previous.Dispose();
        }
    }

    private static List<TextRun> BuildRuns(string text, float size)
    {
        var result = new List<TextRun>(); var enumerator = StringInfo.GetTextElementEnumerator(text); var buffer = new StringBuilder(); SKTypeface? current = null;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement(); var typeface = IsEmoji(element) ? EmojiTypeface : TextTypeface;
            if (current is not null && !ReferenceEquals(current, typeface)) { result.Add(CreateRun(buffer.ToString(), current, size)); buffer.Clear(); }
            current = typeface; buffer.Append(element);
        }
        if (buffer.Length > 0) result.Add(CreateRun(buffer.ToString(), current ?? TextTypeface, size)); return result;
    }
    private static TextRun CreateRun(string text, SKTypeface typeface, float size)
    { using var paint = CreatePaint(typeface, size, SKPaintStyle.Fill, SKColors.White); using var shaper = new SKShaper(typeface); return new(text, typeface, shaper.Shape(text, paint).Width); }
    private static SKPaint CreatePaint(SKTypeface face, float size, SKPaintStyle style, SKColor color) => new() { Typeface = face, TextSize = size, IsAntialias = true, Style = style, Color = color };
    private static bool IsEmoji(string element)
    { foreach (var rune in element.EnumerateRunes()) { var v = rune.Value; if (v == 0xFE0F || v == 0x200D || v == 0x20E3 || v is >= 0x1F000 and <= 0x1FAFF || v is >= 0x2600 and <= 0x27BF || v is >= 0x1F1E6 and <= 0x1F1FF || v is >= 0x1F3FB and <= 0x1F3FF) return true; } return false; }
    private static void Clear(List<ActiveBarrage> active, List<ActiveBarrage?> lanes)
    { foreach (var item in active) item.Texture.Dispose(); active.Clear(); lanes.Clear(); }

    private static IntPtr CreateOverlayWindow()
    {
        var name = $"LiveDanmakuDirectComposition_{Environment.ProcessId}"; var module = GetModuleHandle(null);
        var wc = new WndClassEx { Size = (uint)Marshal.SizeOf<WndClassEx>(), Instance = module, ClassName = name, WindowProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure) };
        if (RegisterClassEx(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
        var hwnd = CreateWindowEx(WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered |
            WsExNoActivate | WsExNoRedirectionBitmap,
            name, "LiveDanmakuOverlay.DirectComposition", WsPopup, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        if (!SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha))
            throw new System.ComponentModel.Win32Exception();
        return hwnd;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
        var hwnd = _windowHandle;
        _windowHandle = IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            if (_uiDispatcher.CheckAccess()) DestroyWindow(hwnd);
            else _uiDispatcher.Invoke(() => DestroyWindow(hwnd));
        }
        _wake.Dispose();
        _started.Dispose();
    }
    private sealed record TextRun(string Text, SKTypeface Typeface, float Width);
    private sealed class ActiveBarrage(ID2D1Bitmap1 texture, int width, int height, double x, int lane,
        ActiveBarrage? leader, DanmakuMessage message, bool awaitingEmoticon)
    {
        public ID2D1Bitmap1 Texture { get; set; } = texture;
        public int Width { get; set; } = width;
        public int Height { get; set; } = height;
        public double X { get; set; } = x;
        public int Lane { get; } = lane;
        public ActiveBarrage? Leader { get; } = leader;
        public DanmakuMessage Message { get; } = message;
        public bool AwaitingEmoticon { get; set; } = awaitingEmoticon;
    }
    private readonly record struct DensitySpacing(double Vertical, double Horizontal);
    private readonly record struct BoundsState(int X, int Y, int Width, int Height, bool Visible);

    private sealed class CompositionGraphics : IDisposable
    {
        private ID3D11Device? _device; private ID3D11DeviceContext? _context; private IDXGISwapChain1? _swapChain;
        private IDXGIFactory2? _factory; private IDXGIDevice? _dxgiDevice; private IDCompositionDevice? _compositionDevice;
        private IDCompositionTarget? _target; private IDCompositionVisual? _visual; private int _width, _height;
        private ID2D1Device? _d2dDevice; private ID2D1DeviceContext? _d2dContext;
        public void Initialize(IntPtr hwnd, int width, int height)
        {
            D3DFeatureLevel[] levels = [D3DFeatureLevel.Level_11_1, D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_1];
            _device = D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
            _context = _device.ImmediateContext;
            _dxgiDevice = _device.QueryInterface<IDXGIDevice>(); _factory = CreateDXGIFactory2<IDXGIFactory2>(false);
            _d2dDevice = D2D1.D2D1CreateDevice(_dxgiDevice);
            _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
            _compositionDevice.CreateTargetForHwnd(hwnd, true, out _target).CheckError(); _compositionDevice.CreateVisual(out _visual).CheckError();
            CreateSwapChain(width, height); _visual.SetContent(_swapChain).CheckError(); _target.SetRoot(_visual).CheckError(); _compositionDevice.Commit().CheckError();
        }
        private void CreateSwapChain(int width, int height)
        {
            _width = width; _height = height;
            var desc = new SwapChainDescription1 { Width = (uint)width, Height = (uint)height, Format = DxgiFormat.B8G8R8A8_UNorm,
                Stereo = false, SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2, Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied, Flags = SwapChainFlags.None };
            _swapChain = _factory!.CreateSwapChainForComposition(_device!, desc, null);
        }
        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _d2dContext!.Target = null;
            _swapChain!.ResizeBuffers(2, (uint)width, (uint)height, DxgiFormat.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            _width = width; _height = height;
        }
        public ID2D1Bitmap1 CreateSprite(SKBitmap bitmap)
        {
            var properties = new BitmapProperties1(
                new D2DPixelFormat(DxgiFormat.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
                96, 96, BitmapOptions.None);
            return _d2dContext!.CreateBitmap(new SizeI(bitmap.Width, bitmap.Height), bitmap.GetPixels(),
                (uint)bitmap.RowBytes, properties);
        }
        public long Draw(IReadOnlyList<ActiveBarrage> active, double laneHeight, double opacity)
        {
            var started = Stopwatch.GetTimestamp();
            using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
            var properties = new BitmapProperties1(
                new D2DPixelFormat(DxgiFormat.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
                96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw);
            using var targetBitmap = _d2dContext!.CreateBitmapFromDxgiSurface(surface, properties);
            _d2dContext.Target = targetBitmap;
            _d2dContext.BeginDraw();
            _d2dContext.Clear(new Color4(0, 0, 0, 0));
            foreach (var item in active)
            {
                var top = (float)(item.Lane * laneHeight);
                _d2dContext.DrawBitmap(item.Texture,
                    new Vortice.RawRectF((float)item.X, top, (float)item.X + item.Width, top + item.Height),
                    (float)opacity, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            }
            _d2dContext.EndDraw();
            _d2dContext.Target = null;
            var drawTicks = Stopwatch.GetTimestamp() - started;
            _swapChain.Present(1, PresentFlags.None).CheckError();
            return drawTicks;
        }
        public void Dispose()
        { _d2dContext?.Dispose(); _d2dDevice?.Dispose(); _visual?.Dispose(); _target?.Dispose(); _compositionDevice?.Dispose(); _swapChain?.Dispose(); _factory?.Dispose(); _dxgiDevice?.Dispose(); _context?.Dispose(); _device?.Dispose(); }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WndClassEx
    { public uint Size, Style; public IntPtr WindowProc; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName; [MarshalAs(UnmanagedType.LPWStr)] public string ClassName; public IntPtr IconSmall; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WndClassEx value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
}

public sealed record BarrageStatistics(long Received, long Displayed, long Merged, long Expired,
    int Pending, double SpeedBoost, double FramesPerSecond = 0, double AverageDrawMilliseconds = 0);
