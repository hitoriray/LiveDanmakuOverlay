using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media.Animation;

namespace LiveDanmakuOverlay;

public sealed class BarrageRenderer : IDisposable
{
    private const double LaneGap = 28;
    private static readonly System.Windows.Media.FontFamily EmojiFont = new("Segoe UI Emoji");
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> ImageCache = new();
    private readonly Canvas _canvas;
    private readonly ConcurrentQueue<PendingMessage> _pending = new();
    private readonly ConcurrentDictionary<string, long> _recentMessages = new();
    private readonly List<LaneState> _lanes = [];
    private readonly List<ActiveBarrage> _active = [];
    private double _fontSize;
    private double _contentOpacity;
    private int _pendingCount;
    private int _lastReportedPendingCount = -1;
    private long _totalAccepted;
    private long _totalLaunched;
    private long _totalMerged;
    private long _totalExpired;
    private DateTime _lastDuplicateCleanup = DateTime.UtcNow;
    private TimeSpan _lastRenderingTime;
    private DateTime _lastStatisticsReport = DateTime.MinValue;
    private volatile bool _enabled = true;
    private volatile bool _disposed;

    public double ScrollSpeed { get; private set; }
    public int PendingCount => Volatile.Read(ref _pendingCount);
    public long TotalAccepted => Interlocked.Read(ref _totalAccepted);
    public long TotalLaunched => Interlocked.Read(ref _totalLaunched);
    public long TotalMerged => Interlocked.Read(ref _totalMerged);
    public long TotalExpired => Interlocked.Read(ref _totalExpired);
    public double FreshnessSeconds { get; set; } = 1;
    public double DuplicateWindowSeconds { get; set; } = 2;
    public event EventHandler<int>? PendingCountChanged;
    public event EventHandler<BarrageStatistics>? StatisticsChanged;

    public BarrageRenderer(Canvas canvas, double fontSize, double scrollSpeed, double contentOpacity = 1)
    {
        _canvas = canvas;
        _fontSize = fontSize;
        ScrollSpeed = scrollSpeed;
        _contentOpacity = contentOpacity;

        RefreshLanes();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    // May be called directly by the WebSocket thread. A concurrent queue prevents thousands of
    // Dispatcher operations from building up during a burst.
    public void Enqueue(DanmakuMessage message)
    {
        Interlocked.Increment(ref _totalAccepted);
        if (!_enabled)
        {
            Interlocked.Increment(ref _totalExpired);
            return;
        }
        var nowTicks = DateTime.UtcNow.Ticks;
        var duplicateKey = MessageFilter.Normalize(message.Text);
        if (duplicateKey.Length > 0 && _recentMessages.TryGetValue(duplicateKey, out var previousTicks) &&
            TimeSpan.FromTicks(nowTicks - previousTicks).TotalSeconds <= DuplicateWindowSeconds)
        {
            _recentMessages[duplicateKey] = nowTicks;
            Interlocked.Increment(ref _totalMerged);
            return;
        }
        if (duplicateKey.Length > 0) _recentMessages[duplicateKey] = nowTicks;
        _pending.Enqueue(new PendingMessage(message, DateTime.UtcNow));
        Interlocked.Increment(ref _pendingCount);
        if (_canvas.Dispatcher.CheckAccess()) TryLaunchPending();
    }

    public void SetFontSize(double fontSize)
    {
        _fontSize = fontSize;
        StopActiveAnimations();
        _canvas.Children.Clear();
        _active.Clear();
        _lanes.Clear();
        RefreshLanes();
    }

    public void SetScrollSpeed(double scrollSpeed)
    {
        if (Math.Abs(ScrollSpeed - scrollSpeed) < 0.1) return;
        ScrollSpeed = scrollSpeed;
        // Every item in one lane uses the same speed, so a later item cannot catch the item ahead.
        StopActiveAnimations();
        _canvas.Children.Clear();
        _active.Clear();
        _lanes.Clear();
        RefreshLanes();
        TryLaunchPending();
    }

    public void SetContentOpacity(double opacity)
    {
        _contentOpacity = Math.Clamp(opacity, 0.1, 1);
        foreach (UIElement element in _canvas.Children) element.Opacity = _contentOpacity;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            RefreshLanes();
            return;
        }

        StopActiveAnimations();
        _canvas.Children.Clear();
        _active.Clear();
        _lanes.Clear();
        while (_pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _totalExpired);
        }
        ReportPendingCount();
    }

    public void RefreshLanes()
    {
        if (_canvas.ActualHeight <= 0) return;
        var laneHeight = _fontSize + 10;
        var requiredCount = Math.Max(1, (int)Math.Floor(_canvas.ActualHeight / laneHeight));
        while (_lanes.Count < requiredCount) _lanes.Add(new LaneState());
        if (_lanes.Count > requiredCount) _lanes.RemoveRange(requiredCount, _lanes.Count - requiredCount);
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering) return;
        ProcessFrame(rendering.RenderingTime);
    }

    internal void ProcessFrame(TimeSpan renderingTime)
    {
        if (_lastRenderingTime == TimeSpan.Zero)
        {
            _lastRenderingTime = renderingTime;
            return;
        }
        var elapsedSeconds = (renderingTime - _lastRenderingTime).TotalSeconds;
        _lastRenderingTime = renderingTime;
        if (elapsedSeconds <= 0) return;
        // WPF input, resizing and DataGrid scrolling share the UI thread with rendering. Never
        // compensate a delayed frame with a large jump: a temporary slowdown is less disruptive.
        elapsedSeconds = Math.Min(elapsedSeconds, 1.0 / 30);

        CleanupDuplicateIndex();
        TryLaunchPending();
        if ((DateTime.UtcNow - _lastStatisticsReport).TotalMilliseconds >= 250)
        {
            _lastStatisticsReport = DateTime.UtcNow;
            ReportPendingCount();
        }
    }

    private void TryLaunchPending()
    {
        if (PendingCount == 0 || _canvas.ActualWidth <= 0 || _canvas.ActualHeight <= 0) return;
        RefreshLanes();

        while (PendingCount > 0)
        {
            var laneIndex = FindAvailableLane();
            if (!_pending.TryPeek(out var candidate)) break;
            if ((DateTime.UtcNow - candidate.ReceivedAt).TotalSeconds > FreshnessSeconds)
            {
                if (_pending.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    Interlocked.Increment(ref _totalExpired);
                }
                continue;
            }
            if (laneIndex < 0 || !_pending.TryDequeue(out candidate)) break;
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _totalLaunched);
            Launch(candidate.Message, laneIndex);
        }
    }

    private int FindAvailableLane()
    {
        for (var index = 0; index < _lanes.Count; index++)
        {
            var lane = _lanes[index];
            if (lane.LastElement is null || !_canvas.Children.Contains(lane.LastElement)) return index;

            var currentX = lane.Transform?.X ?? double.NaN;
            if (!double.IsNaN(currentX) && currentX + lane.LastElement.ActualWidth + LaneGap < _canvas.ActualWidth)
                return index;
        }
        return -1;
    }

    private void Launch(DanmakuMessage message, int laneIndex)
    {
        var element = CreateElement(message);
        element.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var contentWidth = Math.Max(1, element.DesiredSize.Width);
        element.Width = contentWidth;

        var start = _canvas.ActualWidth;
        var transform = new TranslateTransform(start, 0);
        element.RenderTransform = transform;

        Canvas.SetTop(element, laneIndex * (_fontSize + 10));
        Canvas.SetLeft(element, 0);
        _canvas.Children.Add(element);
        _lanes[laneIndex].LastElement = element;
        _lanes[laneIndex].Transform = transform;
        var active = new ActiveBarrage(element, transform, contentWidth, laneIndex);
        _active.Add(active);
        var animation = new DoubleAnimation
        {
            From = start,
            To = -contentWidth,
            Duration = TimeSpan.FromSeconds((start + contentWidth) / ScrollSpeed),
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) => CompleteBarrage(active);
        transform.BeginAnimation(TranslateTransform.XProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void CompleteBarrage(ActiveBarrage item)
    {
        if (!_active.Remove(item)) return;
        item.Transform.BeginAnimation(TranslateTransform.XProperty, null);
        _canvas.Children.Remove(item.Element);
        if (_lanes.Count > item.LaneIndex && ReferenceEquals(_lanes[item.LaneIndex].LastElement, item.Element))
        {
            _lanes[item.LaneIndex].LastElement = null;
            _lanes[item.LaneIndex].Transform = null;
        }
        TryLaunchPending();
    }

    private FrameworkElement CreateElement(DanmakuMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.EmoticonUrl)) return CreateEmoticonElement(message);
        return CreateTextElement(message.Text);
    }

    private TextBlock CreateTextElement(string text)
    {
        var block = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = _fontSize,
            FontWeight = System.Windows.FontWeights.SemiBold,
            TextWrapping = System.Windows.TextWrapping.NoWrap,
            Opacity = _contentOpacity,
        };
        AddTextWithColorEmoji(block, text);
        TextOptions.SetTextRenderingMode(block, TextRenderingMode.Auto);
        return block;
    }

    private void AddTextWithColorEmoji(TextBlock block, string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var buffer = new StringBuilder();
        bool? bufferIsEmoji = null;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var isEmoji = IsEmojiElement(element);
            if (bufferIsEmoji.HasValue && bufferIsEmoji.Value != isEmoji)
            {
                AddTextPart(block, buffer.ToString(), bufferIsEmoji.Value);
                buffer.Clear();
            }
            bufferIsEmoji = isEmoji;
            buffer.Append(element);
        }
        if (buffer.Length > 0) AddTextPart(block, buffer.ToString(), bufferIsEmoji == true);
    }

    private void AddTextPart(TextBlock block, string text, bool emoji)
    {
        if (!emoji)
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) AddEmojiInline(block, enumerator.GetTextElement());
    }

    private void AddEmojiInline(TextBlock block, string element)
    {
        var size = _fontSize + 4;
        var container = new Grid { Width = size, Height = size };
        var fallback = new TextBlock
        {
            Text = element,
            FontFamily = EmojiFont,
            FontWeight = FontWeights.Normal,
            FontSize = _fontSize,
            Foreground = System.Windows.Media.Brushes.White,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        WindowsEmojiRenderer.TryGetCached(element, _fontSize, out var source);
        var image = new System.Windows.Controls.Image
        {
            Stretch = Stretch.Uniform,
            Source = source,
            Visibility = source is null ? Visibility.Hidden : Visibility.Visible
        };
        fallback.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        container.Children.Add(fallback);
        container.Children.Add(image);
        block.Inlines.Add(new InlineUIContainer(container) { BaselineAlignment = BaselineAlignment.Center });
        if (source is null) _ = ApplyEmojiAsync(element, _fontSize, fallback, image);
    }

    private async Task ApplyEmojiAsync(string element, double fontSize, TextBlock fallback,
        System.Windows.Controls.Image image)
    {
        var source = await WindowsEmojiRenderer.GetOrRenderAsync(element, fontSize).ConfigureAwait(false);
        if (source is null || _disposed) return;
        try
        {
            await _canvas.Dispatcher.InvokeAsync(() =>
            {
                if (_disposed) return;
                image.Source = source;
                image.Visibility = Visibility.Visible;
                fallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) when (_disposed) { }
    }

    private static bool IsEmojiElement(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0xFE0F || value == 0x200D || value == 0x20E3 ||
                value is >= 0x1F000 and <= 0x1FAFF ||
                value is >= 0x2600 and <= 0x27BF ||
                value is >= 0x1F1E6 and <= 0x1F1FF ||
                value is >= 0x1F3FB and <= 0x1F3FF)
                return true;
        }
        return false;
    }

    private FrameworkElement CreateEmoticonElement(DanmakuMessage message)
    {
        var height = _fontSize + 6;
        var ratio = message.EmoticonWidth > 0 && message.EmoticonHeight > 0
            ? Math.Clamp((double)message.EmoticonWidth / message.EmoticonHeight, 0.5, 5)
            : 1;
        var grid = new Grid { Height = height, Width = height * ratio, Opacity = _contentOpacity };
        var fallback = CreateTextElement(message.Text);
        fallback.FontSize = Math.Min(_fontSize, height - 2);
        fallback.Opacity = 1;
        var image = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, Visibility = Visibility.Hidden };
        grid.Children.Add(fallback);
        grid.Children.Add(image);

        _ = LoadImageAsync(message.EmoticonUrl!).ContinueWith(task =>
        {
            if (task.Status != TaskStatus.RanToCompletion || task.Result is null) return;
            _canvas.Dispatcher.InvokeAsync(() =>
            {
                image.Source = task.Result;
                image.Visibility = Visibility.Visible;
                fallback.Visibility = Visibility.Collapsed;
            });
        }, TaskScheduler.Default);
        return grid;
    }

    private static Task<ImageSource?> LoadImageAsync(string url) =>
        ImageCache.GetOrAdd(url, static async imageUrl =>
        {
            try
            {
                var bytes = await ImageHttp.GetByteArrayAsync(imageUrl);
                using var stream = new MemoryStream(bytes);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        });

    private void ReportPendingCount()
    {
        var count = PendingCount;
        if (count != _lastReportedPendingCount)
        {
            _lastReportedPendingCount = count;
            PendingCountChanged?.Invoke(this, count);
        }
        StatisticsChanged?.Invoke(this, new BarrageStatistics(TotalAccepted, TotalLaunched, TotalMerged,
            TotalExpired, count, 1));
    }

    private void CleanupDuplicateIndex()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDuplicateCleanup).TotalSeconds < 5) return;
        _lastDuplicateCleanup = now;
        var cutoffTicks = now.AddSeconds(-Math.Max(5, DuplicateWindowSeconds * 2)).Ticks;
        foreach (var pair in _recentMessages)
        {
            if (pair.Value < cutoffTicks) _recentMessages.TryRemove(pair.Key, out _);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        StopActiveAnimations();
        _canvas.Children.Clear();
        _active.Clear();
    }

    private void StopActiveAnimations()
    {
        foreach (var item in _active)
            item.Transform.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private sealed class LaneState
    {
        public FrameworkElement? LastElement { get; set; }
        public TranslateTransform? Transform { get; set; }
    }

    private sealed record PendingMessage(DanmakuMessage Message, DateTime ReceivedAt);
    private sealed class ActiveBarrage(FrameworkElement element, TranslateTransform transform,
        double width, int laneIndex)
    {
        public FrameworkElement Element { get; } = element;
        public TranslateTransform Transform { get; } = transform;
        public double Width { get; } = width;
        public int LaneIndex { get; } = laneIndex;
    }
}

public sealed record BarrageStatistics(long Received, long Displayed, long Merged, long Expired,
    int Pending, double SpeedBoost);
