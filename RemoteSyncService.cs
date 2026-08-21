using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace LiveDanmakuOverlay;

public sealed record DisplaySyncSettings(
    double FontSize, double BackgroundOpacity,
    [property: JsonPropertyName("scrollSpeed")] double ScrollSpeedPercent, double TextOpacity,
    double DisplayAreaPercent, bool DanmakuEnabled, DanmakuDensity Density = DanmakuDensity.Standard);

public sealed record StrategySyncSettings(
    double FreshnessSeconds, double DuplicateWindowSeconds, bool SaveBlockedMessages,
    int HistoryRetentionDays);

public sealed record FilterSyncSettings(List<string> BlockedKeywords, List<string> BlockedUsers);
public sealed record RoomSyncSettings(List<SavedRoom> SavedRooms);

public sealed record SyncPayload(
    int SchemaVersion,
    DisplaySyncSettings Display,
    StrategySyncSettings Strategy,
    FilterSyncSettings Filters,
    RoomSyncSettings Rooms);

public sealed record RemoteSyncDocument(long Revision, DateTimeOffset UpdatedAt, SyncPayload Payload);
public sealed record SyncUploadRequest(long BaseRevision, SyncPayload Payload);
public sealed record SyncMergeResult(SyncPayload? Payload, IReadOnlyList<string> Conflicts)
{
    public bool HasConflicts => Conflicts.Count > 0;
}

public sealed class RemoteSyncClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RemoteSyncDocument?> DownloadAsync(string serverUrl, string userName, string password,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(serverUrl));
        AddAuthorization(request, userName, password);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        ThrowFriendly(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<RemoteSyncDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("服务器返回了无效的同步数据。");
    }

    public async Task<(RemoteSyncDocument Document, bool Conflict)> UploadAsync(
        string serverUrl, string userName, string password, long baseRevision, SyncPayload payload,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(serverUrl));
        AddAuthorization(request, userName, password);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new SyncUploadRequest(baseRevision, payload), JsonOptions),
            Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var isConflict = response.StatusCode == HttpStatusCode.Conflict;
        ThrowFriendly(response, allowConflict: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonSerializer.DeserializeAsync<RemoteSyncDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("服务器返回了无效的同步数据。");
        return (document, isConflict);
    }

    private static Uri BuildUri(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/') + "/api/sync", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("请输入完整的同步服务器 HTTP/HTTPS 地址。");
        return uri;
    }

    private static void AddAuthorization(HttpRequestMessage request, string userName, string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static void ThrowFriendly(HttpResponseMessage response, bool allowConflict = false)
    {
        if (allowConflict && response.StatusCode == HttpStatusCode.Conflict) return;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("同步服务器登录失败，请检查账号和密码。");
        if ((int)response.StatusCode == 413)
            throw new InvalidOperationException("同步内容超过服务器允许的大小。");
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}

public static class SyncPayloadConverter
{
    public const int CurrentSchemaVersion = 3;

    public static SyncPayload FromSettings(AppSettings settings) => Normalize(new SyncPayload(
        CurrentSchemaVersion,
        new DisplaySyncSettings(settings.FontSize, settings.BackgroundOpacity, settings.ScrollSpeedPercent,
            settings.TextOpacity, settings.DisplayAreaPercent, settings.DanmakuEnabled, settings.Density),
        new StrategySyncSettings(settings.FreshnessSeconds, settings.DuplicateWindowSeconds,
            settings.SaveBlockedMessages, settings.HistoryRetentionDays),
        new FilterSyncSettings([.. settings.BlockedKeywords], [.. settings.BlockedUsers]),
        new RoomSyncSettings([.. settings.SavedRooms])));

    public static void Apply(AppSettings settings, SyncPayload payload)
    {
        payload = Normalize(payload);
        if (settings.SyncDisplaySettings)
        {
            settings.FontSize = payload.Display.FontSize;
            settings.BackgroundOpacity = payload.Display.BackgroundOpacity;
            settings.ScrollSpeedPercent = payload.Display.ScrollSpeedPercent;
            settings.TextOpacity = payload.Display.TextOpacity;
            settings.DisplayAreaPercent = payload.Display.DisplayAreaPercent;
            settings.DanmakuEnabled = payload.Display.DanmakuEnabled;
            settings.Density = payload.Display.Density;
        }
        if (settings.SyncStrategySettings)
        {
            settings.FreshnessSeconds = payload.Strategy.FreshnessSeconds;
            settings.DuplicateWindowSeconds = payload.Strategy.DuplicateWindowSeconds;
            settings.SaveBlockedMessages = payload.Strategy.SaveBlockedMessages;
            settings.HistoryRetentionDays = payload.Strategy.HistoryRetentionDays;
        }
        if (settings.SyncFilters)
        {
            settings.BlockedKeywords = [.. payload.Filters.BlockedKeywords];
            settings.BlockedUsers = [.. payload.Filters.BlockedUsers];
        }
        if (settings.SyncRooms) settings.SavedRooms = [.. payload.Rooms.SavedRooms];
    }

    public static SyncPayload Normalize(SyncPayload payload)
    {
        var scrollSpeedPercent = payload.SchemaVersion < 2
            ? AppSettings.ConvertLegacyScrollSpeed(payload.Display.ScrollSpeedPercent)
            : payload.Display.ScrollSpeedPercent;

        return payload with
        {
            SchemaVersion = CurrentSchemaVersion,
            Display = payload.Display with
            {
                FontSize = Closest(payload.Display.FontSize, 14, 18, 24),
                ScrollSpeedPercent = Closest(scrollSpeedPercent, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100),
                BackgroundOpacity = Math.Clamp(payload.Display.BackgroundOpacity, 0, 1),
                TextOpacity = Math.Clamp(payload.Display.TextOpacity, 0.1, 1),
                DisplayAreaPercent = Closest(payload.Display.DisplayAreaPercent, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100),
                Density = Enum.IsDefined(payload.Display.Density) ? payload.Display.Density : DanmakuDensity.Standard
            },
            Strategy = payload.Strategy with
            {
                FreshnessSeconds = Math.Clamp(payload.Strategy.FreshnessSeconds, 0.2, 3),
                DuplicateWindowSeconds = Math.Clamp(payload.Strategy.DuplicateWindowSeconds, 0.5, 8),
                HistoryRetentionDays = payload.Strategy.HistoryRetentionDays is 0 or 7 or 30 or 90
                    ? payload.Strategy.HistoryRetentionDays : 30
            },
            Filters = new FilterSyncSettings(
                Unique(payload.Filters.BlockedKeywords),
                Unique(payload.Filters.BlockedUsers).Where(item => item is not ("***" or "匿名")).ToList()),
            Rooms = new RoomSyncSettings(NormalizeRooms(payload.Rooms.SavedRooms))
        };
    }

    private static double Closest(double value, params double[] choices) =>
        choices.MinBy(choice => Math.Abs(choice - value));

    private static List<string> Unique(IEnumerable<string>? values) => (values ?? [])
        .Select(value => value?.Trim() ?? "")
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static List<SavedRoom> NormalizeRooms(IEnumerable<SavedRoom>? values)
    {
        var result = new List<SavedRoom>();
        foreach (var item in values ?? [])
        {
            var room = item.Room?.Trim() ?? "";
            if (room.Length == 0 || result.Any(existing =>
                    string.Equals(existing.Room, room, StringComparison.OrdinalIgnoreCase))) continue;
            var name = string.IsNullOrWhiteSpace(item.Name) ? room : item.Name.Trim();
            result.Add(new SavedRoom(name, room, item.IsPinned));
            if (result.Count == 30) break;
        }
        return result.OrderByDescending(room => room.IsPinned).ToList();
    }
}

public static class SyncPayloadMerger
{
    public static SyncMergeResult Merge(SyncPayload @base, SyncPayload local, SyncPayload remote)
    {
        @base = SyncPayloadConverter.Normalize(@base);
        local = SyncPayloadConverter.Normalize(local);
        remote = SyncPayloadConverter.Normalize(remote);
        var conflicts = new List<string>();

        var display = new DisplaySyncSettings(
            MergeValue("字号", @base.Display.FontSize, local.Display.FontSize, remote.Display.FontSize, conflicts),
            MergeValue("背景透明度", @base.Display.BackgroundOpacity, local.Display.BackgroundOpacity, remote.Display.BackgroundOpacity, conflicts),
            MergeValue("滚动速度", @base.Display.ScrollSpeedPercent, local.Display.ScrollSpeedPercent,
                remote.Display.ScrollSpeedPercent, conflicts),
            MergeValue("文字透明度", @base.Display.TextOpacity, local.Display.TextOpacity, remote.Display.TextOpacity, conflicts),
            MergeValue("显示区域", @base.Display.DisplayAreaPercent, local.Display.DisplayAreaPercent, remote.Display.DisplayAreaPercent, conflicts),
            MergeValue("弹幕开关", @base.Display.DanmakuEnabled, local.Display.DanmakuEnabled, remote.Display.DanmakuEnabled, conflicts),
            MergeValue("弹幕密度", @base.Display.Density, local.Display.Density, remote.Display.Density, conflicts));
        var strategy = new StrategySyncSettings(
            MergeValue("弹幕新鲜度", @base.Strategy.FreshnessSeconds, local.Strategy.FreshnessSeconds, remote.Strategy.FreshnessSeconds, conflicts),
            MergeValue("重复合并窗口", @base.Strategy.DuplicateWindowSeconds, local.Strategy.DuplicateWindowSeconds, remote.Strategy.DuplicateWindowSeconds, conflicts),
            MergeValue("保存已屏蔽弹幕", @base.Strategy.SaveBlockedMessages, local.Strategy.SaveBlockedMessages, remote.Strategy.SaveBlockedMessages, conflicts),
            MergeValue("历史保留天数", @base.Strategy.HistoryRetentionDays, local.Strategy.HistoryRetentionDays, remote.Strategy.HistoryRetentionDays, conflicts));
        var keywords = MergeSet(@base.Filters.BlockedKeywords, local.Filters.BlockedKeywords,
            remote.Filters.BlockedKeywords);
        var users = MergeSet(@base.Filters.BlockedUsers, local.Filters.BlockedUsers,
            remote.Filters.BlockedUsers);
        var rooms = MergeRooms(@base.Rooms.SavedRooms, local.Rooms.SavedRooms, remote.Rooms.SavedRooms, conflicts);

        if (conflicts.Count > 0) return new SyncMergeResult(null, conflicts);
        return new SyncMergeResult(SyncPayloadConverter.Normalize(new SyncPayload(
            SyncPayloadConverter.CurrentSchemaVersion, display, strategy,
            new FilterSyncSettings(keywords, users), new RoomSyncSettings(rooms))), conflicts);
    }

    private static T MergeValue<T>(string name, T @base, T local, T remote, List<string> conflicts)
    {
        if (EqualityComparer<T>.Default.Equals(local, remote)) return local;
        if (EqualityComparer<T>.Default.Equals(local, @base)) return remote;
        if (EqualityComparer<T>.Default.Equals(remote, @base)) return local;
        conflicts.Add(name);
        return local;
    }

    private static List<string> MergeSet(IEnumerable<string> @base, IEnumerable<string> local,
        IEnumerable<string> remote)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var baseSet = new HashSet<string>(@base, comparer);
        var localSet = new HashSet<string>(local, comparer);
        var remoteSet = new HashSet<string>(remote, comparer);
        var merged = new HashSet<string>(baseSet, comparer);
        merged.ExceptWith(baseSet.Except(localSet, comparer));
        merged.ExceptWith(baseSet.Except(remoteSet, comparer));
        merged.UnionWith(localSet.Except(baseSet, comparer));
        merged.UnionWith(remoteSet.Except(baseSet, comparer));
        return merged.OrderBy(value => value, comparer).ToList();
    }

    private static List<SavedRoom> MergeRooms(IEnumerable<SavedRoom> @base, IEnumerable<SavedRoom> local,
        IEnumerable<SavedRoom> remote, List<string> conflicts)
    {
        var baseMap = @base.ToDictionary(item => item.Room, StringComparer.OrdinalIgnoreCase);
        var localMap = local.ToDictionary(item => item.Room, StringComparer.OrdinalIgnoreCase);
        var remoteMap = remote.ToDictionary(item => item.Room, StringComparer.OrdinalIgnoreCase);
        var keys = baseMap.Keys.Concat(localMap.Keys).Concat(remoteMap.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<SavedRoom>();
        foreach (var key in keys)
        {
            baseMap.TryGetValue(key, out var baseValue);
            localMap.TryGetValue(key, out var localValue);
            remoteMap.TryGetValue(key, out var remoteValue);
            if (Equals(localValue, remoteValue)) { if (localValue is not null) result.Add(localValue); continue; }
            if (Equals(localValue, baseValue)) { if (remoteValue is not null) result.Add(remoteValue); continue; }
            if (Equals(remoteValue, baseValue)) { if (localValue is not null) result.Add(localValue); continue; }
            conflicts.Add($"直播间 {key}");
        }
        return result;
    }
}

public sealed record LocalSyncState(long Revision, SyncPayload? BasePayload, DateTimeOffset? LastSyncedAt);

public static class LocalSyncStateStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveDanmakuOverlay", "sync-state.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static LocalSyncState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<LocalSyncState>(File.ReadAllText(FilePath)) ?? new(0, null, null)
                : new(0, null, null);
        }
        catch { return new(0, null, null); }
    }

    public static void Save(LocalSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, Options));
        File.Move(temporary, FilePath, true);
    }
}

public static class SyncCredentialStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveDanmakuOverlay", "accounts", "sync.dat");

    public static void Save(string password)
    {
        if (string.IsNullOrEmpty(password)) { Clear(); return; }
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllBytes(FilePath, encrypted);
    }

    public static string Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser))
                : "";
        }
        catch { return ""; }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { }
    }
}
