using System.Text.Json;

namespace LiveDanmakuOverlay;

public sealed record SyncStatus(string Message, bool IsError = false, bool HasConflict = false,
    IReadOnlyList<string>? Conflicts = null);

public sealed class SyncCoordinator : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly MessageFilter _filter;
    private readonly Action _saveSettings;
    private readonly Action _applySettings;
    private readonly RemoteSyncClient _client = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _debounce;
    private Task? _periodicTask;
    private RemoteSyncDocument? _pendingRemote;
    private SyncPayload? _pendingLocal;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public event EventHandler<SyncStatus>? StatusChanged;

    public SyncCoordinator(AppSettings settings, MessageFilter filter, Action saveSettings, Action applySettings)
    {
        _settings = settings;
        _filter = filter;
        _saveSettings = saveSettings;
        _applySettings = applySettings;
    }

    public void Start()
    {
        if (_periodicTask is not null) return;
        _periodicTask = PeriodicLoopAsync(_lifetime.Token);
        if (CanSync) _ = SyncNowAsync();
    }

    public void SettingsChanged()
    {
        if (!CanSync) return;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _ = DebouncedSyncAsync(_debounce.Token);
    }

    public async Task<SyncStatus> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSync) return Publish(new SyncStatus("同步未启用，或服务器地址、账号、密码不完整。", true));
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            Publish(new SyncStatus("正在同步…"));
            var password = SyncCredentialStore.Load();
            var state = LocalSyncStateStore.Load();
            var local = SyncPayloadConverter.FromSettings(_settings);
            var remote = await _client.DownloadAsync(_settings.RemoteSyncUrl,
                _settings.RemoteSyncUserName, password, cancellationToken);

            if (remote is null)
                return await UploadAndApplyAsync(0, local, cancellationToken, "已创建远程同步数据");

            local = SelectEnabledCategories(local, remote.Payload);

            if (state.BasePayload is null)
            {
                var first = MergeFirstTime(local, remote.Payload);
                return await UploadAndApplyAsync(remote.Revision, first, cancellationToken, "首次同步已合并");
            }

            if (remote.Revision == state.Revision)
            {
                if (Same(local, state.BasePayload))
                    return Publish(new SyncStatus($"同步完成 · 版本 {remote.Revision}"));
                return await UploadAndApplyAsync(remote.Revision, local, cancellationToken, "本机修改已上传");
            }

            var merge = SyncPayloadMerger.Merge(state.BasePayload, local, remote.Payload);
            if (merge.HasConflicts)
            {
                _pendingRemote = remote;
                _pendingLocal = local;
                return Publish(new SyncStatus("检测到同步冲突，请选择保留本机或使用远程。",
                    HasConflict: true, Conflicts: merge.Conflicts));
            }
            return await UploadAndApplyAsync(remote.Revision, merge.Payload!, cancellationToken, "远程修改已合并");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Publish(new SyncStatus("同步超时或已取消，请检查网络和服务器地址。", true));
        }
        catch (Exception ex)
        {
            return Publish(new SyncStatus($"同步失败：{ex.Message}", true));
        }
        finally { _syncLock.Release(); }
    }

    public async Task<SyncStatus> ResolveConflictAsync(bool useLocal,
        CancellationToken cancellationToken = default)
    {
        if (_pendingRemote is null) return Publish(new SyncStatus("当前没有待处理的冲突。", true));
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (useLocal)
                return await UploadAndApplyAsync(_pendingRemote.Revision,
                    _pendingLocal ?? SyncPayloadConverter.FromSettings(_settings), cancellationToken, "已保留本机设置");

            ApplyPayload(_pendingRemote.Payload);
            SaveState(_pendingRemote);
            _pendingRemote = null;
            _pendingLocal = null;
            return Publish(new SyncStatus("已使用远程设置"));
        }
        catch (Exception ex) { return Publish(new SyncStatus($"处理冲突失败：{ex.Message}", true)); }
        finally { _syncLock.Release(); }
    }

    public void ResetLocalBase()
    {
        LocalSyncStateStore.Save(new LocalSyncState(0, null, null));
        _pendingRemote = null;
        _pendingLocal = null;
    }

    private async Task<SyncStatus> UploadAndApplyAsync(long baseRevision, SyncPayload payload,
        CancellationToken cancellationToken, string message)
    {
        var password = SyncCredentialStore.Load();
        var result = await _client.UploadAsync(_settings.RemoteSyncUrl,
            _settings.RemoteSyncUserName, password, baseRevision, payload, cancellationToken);
        if (result.Conflict)
        {
            _pendingRemote = result.Document;
            _pendingLocal = payload;
            return Publish(new SyncStatus("同步期间远程又发生变化，请重试或选择版本。",
                HasConflict: true, Conflicts: ["远程版本已更新"]));
        }
        ApplyPayload(result.Document.Payload);
        SaveState(result.Document);
        _pendingRemote = null;
        _pendingLocal = null;
        return Publish(new SyncStatus($"{message} · 版本 {result.Document.Revision}"));
    }

    private void ApplyPayload(SyncPayload payload)
    {
        SyncPayloadConverter.Apply(_settings, payload);
        _filter.ReplaceAll(_settings.BlockedKeywords, _settings.BlockedUsers);
        _saveSettings();
        _applySettings();
    }

    private static void SaveState(RemoteSyncDocument document) =>
        LocalSyncStateStore.Save(new LocalSyncState(document.Revision,
            document.Payload, DateTimeOffset.Now));

    private static SyncPayload MergeFirstTime(SyncPayload local, SyncPayload remote)
    {
        local = SyncPayloadConverter.Normalize(local);
        remote = SyncPayloadConverter.Normalize(remote);
        var keywords = local.Filters.BlockedKeywords.Concat(remote.Filters.BlockedKeywords)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var users = local.Filters.BlockedUsers.Concat(remote.Filters.BlockedUsers)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rooms = local.Rooms.SavedRooms.Concat(remote.Rooms.SavedRooms)
            .DistinctBy(room => room.Room, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
        return SyncPayloadConverter.Normalize(remote with
        {
            Filters = new FilterSyncSettings(keywords, users),
            Rooms = new RoomSyncSettings(rooms)
        });
    }

    private SyncPayload SelectEnabledCategories(SyncPayload local, SyncPayload remote) => local with
    {
        Display = _settings.SyncDisplaySettings ? local.Display : remote.Display,
        Strategy = _settings.SyncStrategySettings ? local.Strategy : remote.Strategy,
        Filters = _settings.SyncFilters ? local.Filters : remote.Filters,
        Rooms = _settings.SyncRooms ? local.Rooms : remote.Rooms
    };

    private async Task DebouncedSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            await SyncNowAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private async Task PeriodicLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                if (CanSync) await SyncNowAsync(cancellationToken);
        }
        catch (OperationCanceledException) { }
    }

    private bool CanSync => _settings.RemoteSyncEnabled &&
        !string.IsNullOrWhiteSpace(_settings.RemoteSyncUrl) &&
        !string.IsNullOrWhiteSpace(_settings.RemoteSyncUserName) &&
        !string.IsNullOrEmpty(SyncCredentialStore.Load());

    private SyncStatus Publish(SyncStatus status)
    {
        StatusChanged?.Invoke(this, status);
        return status;
    }

    private static bool Same(SyncPayload first, SyncPayload second) =>
        JsonSerializer.Serialize(first, JsonOptions) == JsonSerializer.Serialize(second, JsonOptions);

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _debounce?.Cancel();
        if (_periodicTask is not null)
            try { await _periodicTask; } catch (OperationCanceledException) { }
        _debounce?.Dispose();
        _lifetime.Dispose();
        _syncLock.Dispose();
        _client.Dispose();
    }
}
