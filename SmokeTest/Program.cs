using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Threading;
using System.IO;
using LiveDanmakuOverlay;
using Microsoft.Data.Sqlite;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        TestSettingsInitialization();
        TestWindowPlacement();
        TestWindowDragPolicy();
        TestAsyncEmojiRendering().GetAwaiter().GetResult();
        TestConnectionAsync(args.FirstOrDefault() ?? "6").GetAwaiter().GetResult();
        TestQrLoginAsync().GetAwaiter().GetResult();
        TestBarrageRenderer();
        TestHistoryAndFilterAsync().GetAwaiter().GetResult();
        TestSyncMerge();
        Console.WriteLine("SMOKE_TEST_OK");
    }

    private static void TestSettingsInitialization()
    {
        if (AppSettings.ConvertLegacyScrollSpeed(70) != 20 ||
            AppSettings.ConvertLegacyScrollSpeed(110) != 40 ||
            AppSettings.ConvertLegacyScrollSpeed(170) != 60)
            throw new InvalidOperationException("旧版本地滚动速度未正确迁移为百分比");

        var settings = new AppSettings
        {
            BackgroundOpacity = 0.2,
            TextOpacity = 0.4,
            FontSize = 24,
            ScrollSpeedPercent = 50,
            DisplayAreaPercent = 25
        };

        _ = new MainWindow(settings);

        if (settings.BackgroundOpacity != 0.2 || settings.TextOpacity != 0.4 ||
            settings.FontSize != 24 || settings.ScrollSpeedPercent != 50 || settings.DisplayAreaPercent != 25)
            throw new InvalidOperationException("窗口初始化覆盖了已加载的用户设置");
        Console.WriteLine("SETTINGS_INITIALIZATION_OK");
    }

    private static void TestWindowPlacement()
    {
        var position = WindowPlacement.TopRight(new Rect(0, 0, 1920, 1080), 420, 620, 24);
        if (position != new Point(1476, 24))
            throw new InvalidOperationException($"默认右上角位置错误：{position}");

        var oversized = WindowPlacement.TopRight(new Rect(100, 50, 300, 200), 500, 400, 24);
        if (oversized.X < 100 || oversized.Y < 50)
            throw new InvalidOperationException("超大窗口默认位置越过工作区左上边界");

        if (WindowPlacement.IsVisible(new Point(double.NaN, 20), new Rect(0, 0, 1920, 1080)))
            throw new InvalidOperationException("无效保存坐标被误判为可恢复位置");
        Console.WriteLine("WINDOW_PLACEMENT_OK");
    }

    private static void TestWindowDragPolicy()
    {
        if (!WindowDragPolicy.CanStart(new Grid(), false, WindowState.Normal, MouseButtonState.Pressed) ||
            !WindowDragPolicy.CanStart(new TextBlock(), false, WindowState.Normal, MouseButtonState.Pressed))
            throw new InvalidOperationException("普通背景或文字区域不能开始拖动");

        DependencyObject[] controls = [new Button(), new TextBox(), new ComboBox(), new Slider(), new Thumb()];
        if (controls.Any(control => WindowDragPolicy.CanStart(control, false, WindowState.Normal, MouseButtonState.Pressed)))
            throw new InvalidOperationException("交互控件被误判为可拖动区域");

        var child = new Border();
        var parentButton = new Button { Content = child };
        if (WindowDragPolicy.CanStart(child, false, WindowState.Normal, MouseButtonState.Pressed))
            throw new InvalidOperationException("交互控件的子元素被误判为可拖动区域");
        GC.KeepAlive(parentButton);

        if (WindowDragPolicy.CanStart(new Grid(), true, WindowState.Normal, MouseButtonState.Pressed) ||
            WindowDragPolicy.CanStart(new Grid(), false, WindowState.Maximized, MouseButtonState.Pressed) ||
            WindowDragPolicy.CanStart(new Grid(), false, WindowState.Normal, MouseButtonState.Released))
            throw new InvalidOperationException("锁定、最大化或未按下左键时仍允许拖动");
        Console.WriteLine("WINDOW_DRAG_POLICY_OK");
    }

    private static async Task TestAsyncEmojiRendering()
    {
        const string emoji = "🛸";
        WindowsEmojiRenderer.Invalidate(emoji, 23);
        var first = WindowsEmojiRenderer.GetOrRenderAsync(emoji, 23);
        var second = WindowsEmojiRenderer.GetOrRenderAsync(emoji, 23);
        if (!ReferenceEquals(first, second))
            throw new InvalidOperationException("同一 Emoji 创建了重复后台渲染任务");

        var source = await first;
        if (source is null || !source.IsFrozen)
            throw new InvalidOperationException("异步 Emoji 渲染没有返回冻结图像");
        if (!WindowsEmojiRenderer.TryGetCached(emoji, 23, out var cached) || !ReferenceEquals(source, cached))
            throw new InvalidOperationException("异步 Emoji 渲染结果未进入缓存");
        Console.WriteLine("ASYNC_EMOJI_RENDERING_OK");
    }

    private static async Task TestQrLoginAsync()
    {
        using var account = new BilibiliAccountProvider();
        var session = await account.BeginQrLoginAsync();
        if (string.IsNullOrWhiteSpace(session.Key) || !session.Url.StartsWith("https://", StringComparison.Ordinal) ||
            session.PngBytes.Length < 100)
            throw new InvalidOperationException("B站二维码登录初始化失败");
        var poll = await account.PollQrLoginAsync(session.Key);
        if (poll.State is not (QrLoginState.WaitingForScan or QrLoginState.WaitingForConfirmation))
            throw new InvalidOperationException($"二维码初始状态异常：{poll.State}");
        Console.WriteLine("BILIBILI_QR_LOGIN_OK");
    }

    private static async Task TestConnectionAsync(string room)
    {
        await using var client = new BilibiliDanmakuClient();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, status) =>
        {
            Console.WriteLine(status);
            if (status.StartsWith("已连接", StringComparison.Ordinal)) connected.TrySetResult();
        };
        await client.ConnectAsync(room);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void TestBarrageRenderer()
    {
        var emojiColorCount = WindowsEmojiRenderer.CountOpaqueColors("😀", 24);
        Console.WriteLine($"WINDOWS_EMOJI_COLORS={emojiColorCount}");
        if (emojiColorCount < 8)
            throw new InvalidOperationException("Windows Emoji 渲染结果不是彩色图层");
        if (WindowsEmojiRenderer.CountOpaqueColors("👨‍👩‍👧‍👦", 24) < 8)
            throw new InvalidOperationException("Windows 组合 Emoji 渲染失败");
        var canvas = new Canvas { Width = 600, Height = 120, ClipToBounds = true };
        canvas.Measure(new Size(600, 120));
        canvas.Arrange(new Rect(0, 0, 600, 120));
        using var renderer = new BarrageRenderer(canvas, 18, 100, 0.65) { FreshnessSeconds = 0.01 };
        for (var index = 0; index < 1000; index++)
            renderer.Enqueue(new DanmakuMessage($"不应显示的用户名{index}", $"滚动弹幕测试 {index}"));
        if (canvas.Children.Count < 2)
            throw new InvalidOperationException("滚动弹幕没有填充分轨");
        if (!canvas.ClipToBounds)
            throw new InvalidOperationException("弹幕区域没有启用边界裁剪");
        renderer.ProcessFrame(TimeSpan.FromSeconds(1));
        var movingElement = canvas.Children.Cast<FrameworkElement>().First();
        var transform = (System.Windows.Media.TranslateTransform)movingElement.RenderTransform;
        if (!transform.HasAnimatedProperties)
            throw new InvalidOperationException("弹幕没有使用独立合成动画");
        var beforeStall = transform.X;
        renderer.ProcessFrame(TimeSpan.FromSeconds(3));
        if (beforeStall - transform.X > 3.5)
            throw new InvalidOperationException("UI 卡顿后弹幕发生补偿性加速");
        Thread.Sleep(30);
        renderer.ProcessFrame(TimeSpan.FromSeconds(3.05));
        if (renderer.TotalAccepted != 1000 ||
            renderer.TotalAccepted != renderer.TotalLaunched + renderer.TotalMerged + renderer.TotalExpired + renderer.PendingCount)
            throw new InvalidOperationException("实时调度统计不守恒");
        if (renderer.PendingCount != 0 || renderer.TotalExpired == 0)
            throw new InvalidOperationException("过期弹幕仍在队列中造成延迟");
        if (canvas.Children.OfType<TextBlock>().Any(block => block.Text.Contains("用户名", StringComparison.Ordinal)))
            throw new InvalidOperationException("滚动区域仍然显示了用户名");
        if (canvas.Children.Cast<FrameworkElement>().Any(element => Math.Abs(element.Opacity - 0.65) > 0.01))
            throw new InvalidOperationException("弹幕透明度没有生效");
        var emojiCanvas = new Canvas { Width = 600, Height = 80, ClipToBounds = true };
        emojiCanvas.Measure(new Size(600, 80));
        emojiCanvas.Arrange(new Rect(0, 0, 600, 80));
        using var emojiRenderer = new BarrageRenderer(emojiCanvas, 18, 100, 1);
        emojiRenderer.Enqueue(new DanmakuMessage("用户", "中文🧩👨‍👩‍👧‍👦"));
        var emojiBlock = emojiCanvas.Children.OfType<TextBlock>().Single();
        var emojiContainers = emojiBlock.Inlines.OfType<InlineUIContainer>()
            .Select(inline => (Grid)inline.Child).ToArray();
        if (emojiContainers.Length != 2)
            throw new InvalidOperationException("标准 Emoji 未转换为 Windows 彩色图片");
        if (emojiContainers.Any(container => container.Children.OfType<System.Windows.Controls.Image>().Single().Source is not null ||
                                             container.Children.OfType<TextBlock>().Single().Visibility != Visibility.Visible))
            throw new InvalidOperationException("未缓存 Emoji 没有先显示字体回退");

        WindowsEmojiRenderer.GetOrRenderAsync("🧩", 18).GetAwaiter().GetResult();
        WindowsEmojiRenderer.GetOrRenderAsync("👨‍👩‍👧‍👦", 18).GetAwaiter().GetResult();
        PumpDispatcherUntil(() => emojiContainers.All(container =>
            container.Children.OfType<System.Windows.Controls.Image>().Single().Visibility == Visibility.Visible));
        if (emojiContainers.Any(container => container.Children.OfType<TextBlock>().Single().Visibility != Visibility.Collapsed))
            throw new InvalidOperationException("彩色 Emoji 完成后没有隐藏字体回退");
        emojiRenderer.SetEnabled(false);
        emojiRenderer.Enqueue(new DanmakuMessage("用户", "关闭期间不应显示"));
        if (emojiCanvas.Children.Count != 0 || emojiRenderer.TotalExpired == 0)
            throw new InvalidOperationException("关闭弹幕后仍显示或堆积弹幕");
        emojiRenderer.SetEnabled(true);
        emojiRenderer.Enqueue(new DanmakuMessage("用户", "重新开启后显示"));
        if (emojiCanvas.Children.Count == 0)
            throw new InvalidOperationException("重新开启弹幕后未恢复显示");
        Console.WriteLine($"BARRAGE_RENDERER_OK · accepted={renderer.TotalAccepted} · displayed={renderer.TotalLaunched} · expired={renderer.TotalExpired} · pending={renderer.PendingCount}");
    }

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        if (!condition()) throw new InvalidOperationException("异步 Emoji 未能更新到界面");
    }

    private static async Task TestHistoryAndFilterAsync()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "LiveDanmakuOverlay-SmokeTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var settings = new AppSettings { BlockedKeywords = ["独轮车"] };
        var filter = new MessageFilter(settings);
        if (!filter.IsBlocked("独 轮-车广告", out _))
            throw new InvalidOperationException("屏蔽词未能忽略空格和标点");
        if (!filter.AddUser("测试黑名单用户") ||
            !filter.IsBlocked(new DanmakuMessage("测试黑名单用户", "普通内容"), out var userReason) ||
            userReason != "用户：测试黑名单用户")
            throw new InvalidOperationException("用户黑名单未生效");
        if (filter.AddUser("***"))
            throw new InvalidOperationException("允许屏蔽脱敏用户名会造成误伤");

        var databasePath = Path.Combine(testDirectory, "history.db");
        await using (var history = new HistoryStore(databasePath))
        {
            history.Record("6", new DanmakuMessage("测试用户", "可搜索正文"), false, null);
            history.Record("6", new DanmakuMessage("屏蔽用户", "被屏蔽正文"), true, "测试词");
        }
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO messages(timestamp, room, username, text, was_blocked, was_displayed, block_reason)
                VALUES($timestamp, '6', '过期用户', '过期正文', 0, 1, NULL);
                """;
            command.Parameters.AddWithValue("$timestamp", DateTimeOffset.Now.AddDays(-31).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await using (var history = new HistoryStore(databasePath))
        {
            var byUser = await history.SearchAsync("测试用户");
            var byText = await history.SearchAsync("可搜索正文");
            if (byUser.Count != 1 || byText.Count != 1)
                throw new InvalidOperationException("历史记录未保存用户名或正文搜索失败");
            var onlyBlocked = await history.SearchAsync("", blocked: true);
            var onlyNormal = await history.SearchAsync("", blocked: false);
            if (onlyBlocked.Count != 1 || onlyNormal.Count != 2)
                throw new InvalidOperationException("历史屏蔽状态筛选失败");
            var before = await history.GetStatisticsAsync();
            var deleted = await history.CleanupOlderThanAsync(30);
            var after = await history.GetStatisticsAsync();
            if (before.RecordCount != 3 || deleted != 1 || after.RecordCount != 2)
                throw new InvalidOperationException("历史保留期限清理失败");
            if (await history.ClearAsync() != 2 || (await history.GetStatisticsAsync()).RecordCount != 0)
                throw new InvalidOperationException("清空全部历史失败");
        }
        Directory.Delete(testDirectory, recursive: true);
        Console.WriteLine("HISTORY_FILTER_OK");
    }

    private static void TestSyncMerge()
    {
        var baseSettings = new AppSettings
        {
            FontSize = 18,
            BackgroundOpacity = 0.5,
            BlockedKeywords = ["广告", "刷屏"],
            BlockedUsers = ["用户甲"],
            SavedRooms = [new SavedRoom("房间一", "1")]
        };
        var @base = SyncPayloadConverter.FromSettings(baseSettings);
        var legacy = SyncPayloadConverter.Normalize(@base with
        {
            SchemaVersion = 1,
            Display = @base.Display with { ScrollSpeedPercent = 110 }
        });
        if (legacy.SchemaVersion != SyncPayloadConverter.CurrentSchemaVersion ||
            legacy.Display.ScrollSpeedPercent != 40)
            throw new InvalidOperationException("旧版同步滚动速度未正确迁移为百分比");

        var localSettings = new AppSettings
        {
            FontSize = 24,
            BackgroundOpacity = 0.5,
            BlockedKeywords = ["广告", "本地新增"],
            BlockedUsers = ["用户甲"],
            SavedRooms = [new SavedRoom("房间一", "1"), new SavedRoom("本地房间", "2")]
        };
        var remoteSettings = new AppSettings
        {
            FontSize = 18,
            BackgroundOpacity = 0.8,
            BlockedKeywords = ["广告", "刷屏", "远程新增"],
            BlockedUsers = [],
            SavedRooms = [new SavedRoom("房间一", "1"), new SavedRoom("远程房间", "3")]
        };
        var merged = SyncPayloadMerger.Merge(@base,
            SyncPayloadConverter.FromSettings(localSettings), SyncPayloadConverter.FromSettings(remoteSettings));
        if (merged.HasConflicts || merged.Payload is null || merged.Payload.Display.FontSize != 24 ||
            Math.Abs(merged.Payload.Display.BackgroundOpacity - 0.8) > 0.001 ||
            merged.Payload.Filters.BlockedKeywords.Contains("刷屏") ||
            !merged.Payload.Filters.BlockedKeywords.Contains("本地新增") ||
            !merged.Payload.Filters.BlockedKeywords.Contains("远程新增") ||
            merged.Payload.Filters.BlockedUsers.Count != 0 || merged.Payload.Rooms.SavedRooms.Count != 3)
            throw new InvalidOperationException("同步三方合并未正确处理独立修改、新增或删除");

        remoteSettings.FontSize = 14;
        var conflict = SyncPayloadMerger.Merge(@base,
            SyncPayloadConverter.FromSettings(localSettings), SyncPayloadConverter.FromSettings(remoteSettings));
        if (!conflict.HasConflicts || !conflict.Conflicts.Contains("字号"))
            throw new InvalidOperationException("同步没有检测到同一设置项的真实冲突");
        Console.WriteLine("SYNC_MERGE_OK");
    }
}
