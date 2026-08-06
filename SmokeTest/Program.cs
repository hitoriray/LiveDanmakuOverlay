using System.Windows;
using System.Windows.Controls;
using System.IO;
using LiveDanmakuOverlay;
using Microsoft.Data.Sqlite;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        TestConnectionAsync(args.FirstOrDefault() ?? "6").GetAwaiter().GetResult();
        TestQrLoginAsync().GetAwaiter().GetResult();
        TestBarrageRenderer();
        TestHistoryAndFilterAsync().GetAwaiter().GetResult();
        Console.WriteLine("SMOKE_TEST_OK");
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
        emojiRenderer.Enqueue(new DanmakuMessage("用户", "中文😀👨‍👩‍👧‍👦"));
        var emojiBlock = emojiCanvas.Children.OfType<TextBlock>().Single();
        if (emojiBlock.Inlines.OfType<System.Windows.Documents.InlineUIContainer>().Count() != 2)
            throw new InvalidOperationException("标准 Emoji 未转换为 Windows 彩色图片");
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
}
