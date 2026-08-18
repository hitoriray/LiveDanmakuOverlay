using System.Text.Json;
using System.IO;

namespace LiveDanmakuOverlay;

public sealed class AppSettings
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveDanmakuOverlay");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public string Room { get; set; } = "";
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 620;
    public bool HasWindowPlacement { get; set; }
    public double FontSize { get; set; } = 18;
    public double BackgroundOpacity { get; set; } = 0.45;
    public double ScrollSpeedPercent { get; set; } = 50;
    public double TextOpacity { get; set; } = 1;
    public double DisplayAreaPercent { get; set; } = 100;
    public bool DanmakuEnabled { get; set; } = true;
    public double FreshnessSeconds { get; set; } = 1;
    public double DuplicateWindowSeconds { get; set; } = 2;
    public List<string> BlockedKeywords { get; set; } = [];
    public List<string> BlockedUsers { get; set; } = [];
    public List<SavedRoom> SavedRooms { get; set; } = [];
    public string RemoteSyncUrl { get; set; } = "";
    public string RemoteSyncUserName { get; set; } = "";
    public bool RemoteSyncEnabled { get; set; }
    public bool SyncDisplaySettings { get; set; } = true;
    public bool SyncStrategySettings { get; set; } = true;
    public bool SyncFilters { get; set; } = true;
    public bool SyncRooms { get; set; } = true;
    public bool SaveBlockedMessages { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;
    public bool IsLocked { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(nameof(ScrollSpeedPercent), out _) &&
                document.RootElement.TryGetProperty("ScrollSpeed", out var legacySpeed) &&
                legacySpeed.TryGetDouble(out var pixelsPerSecond))
                settings.ScrollSpeedPercent = ConvertLegacyScrollSpeed(pixelsPerSecond);
            return settings;
        }
        catch { return new AppSettings(); }
    }

    public static double ConvertLegacyScrollSpeed(double pixelsPerSecond)
    {
        var percent = pixelsPerSecond * 100.0 / BarrageRenderer.BaseScrollSpeed;
        double[] choices = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        return choices.MinBy(choice => Math.Abs(choice - percent));
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Settings failure should never stop the overlay. */ }
    }
}

public sealed record SavedRoom(string Name, string Room, bool IsPinned = false)
{
    public string DisplayName => string.Equals(Name, Room, StringComparison.OrdinalIgnoreCase)
        ? Room
        : $"{Name} · {Room}";
}
