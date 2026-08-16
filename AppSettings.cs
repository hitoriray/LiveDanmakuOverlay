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
    public bool SaveBlockedMessages { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 30;
    public bool IsLocked { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { return new AppSettings(); }
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
