using System.Windows;
using System.Windows.Controls;
using System.IO;
using LiveDanmakuOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        TestConnectionAsync(args.FirstOrDefault() ?? "6").GetAwaiter().GetResult();
        TestBarrageRenderer();
        TestHistoryAndFilterAsync().GetAwaiter().GetResult();
        Console.WriteLine("SMOKE_TEST_OK");
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
        Thread.Sleep(30);
        renderer.ProcessFrame(TimeSpan.FromSeconds(3));
        if (renderer.TotalAccepted != 1000 ||
            renderer.TotalAccepted != renderer.TotalLaunched + renderer.TotalMerged + renderer.TotalExpired + renderer.PendingCount)
            throw new InvalidOperationException("实时调度统计不守恒");
        if (renderer.PendingCount != 0 || renderer.TotalExpired == 0)
            throw new InvalidOperationException("过期弹幕仍在队列中造成延迟");
        if (canvas.Children.OfType<TextBlock>().Any(block => block.Text.Contains("用户名", StringComparison.Ordinal)))
            throw new InvalidOperationException("滚动区域仍然显示了用户名");
        if (canvas.Children.Cast<FrameworkElement>().Any(element => Math.Abs(element.Opacity - 0.65) > 0.01))
            throw new InvalidOperationException("弹幕透明度没有生效");
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

        await using (var history = new HistoryStore(Path.Combine(testDirectory, "history.db")))
        {
            history.Record("6", new DanmakuMessage("测试用户", "可搜索正文"), false, null);
        }
        await using (var history = new HistoryStore(Path.Combine(testDirectory, "history.db")))
        {
            var byUser = await history.SearchAsync("测试用户");
            var byText = await history.SearchAsync("可搜索正文");
            if (byUser.Count != 1 || byText.Count != 1)
                throw new InvalidOperationException("历史记录未保存用户名或正文搜索失败");
        }
        Directory.Delete(testDirectory, recursive: true);
        Console.WriteLine("HISTORY_FILTER_OK");
    }
}
