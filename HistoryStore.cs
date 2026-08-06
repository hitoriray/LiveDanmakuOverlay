using System.Threading.Channels;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LiveDanmakuOverlay;

public sealed record HistoryRecord(long Id, DateTimeOffset Timestamp, string Room, string UserName,
    string Text, bool WasBlocked, bool WasDisplayed, string? BlockReason);

public sealed record HistoryStatistics(long RecordCount, DateTimeOffset? EarliestTimestamp,
    DateTimeOffset? LatestTimestamp, long DiskBytes);

public sealed class HistoryStore : IAsyncDisposable
{
    private static readonly string DefaultDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveDanmakuOverlay", "history.db");
    private readonly string _databasePath;
    private readonly Channel<HistoryQueueItem> _writes = Channel.CreateUnbounded<HistoryQueueItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    public HistoryStore(string? databasePath = null)
    {
        _databasePath = databasePath ?? DefaultDatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        InitializeDatabase();
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public void Record(string room, DanmakuMessage message, bool blocked, string? reason)
    {
        _writes.Writer.TryWrite(new HistoryWrite(DateTimeOffset.Now, room, message.UserName,
            message.Text, blocked, !blocked, reason));
    }

    public async Task<IReadOnlyList<HistoryRecord>> SearchAsync(string query, int limit = 500, bool? blocked = null)
    {
        await FlushAsync();
        var results = new List<HistoryRecord>();
        await _databaseGate.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, timestamp, room, username, text, was_blocked, was_displayed, block_reason
                FROM messages
                WHERE ($query = '' OR text LIKE $pattern ESCAPE '\' OR username LIKE $pattern ESCAPE '\' OR room LIKE $pattern ESCAPE '\')
                  AND ($blocked IS NULL OR was_blocked = $blocked)
                ORDER BY id DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
            command.Parameters.AddWithValue("$blocked", blocked.HasValue ? blocked.Value : DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new HistoryRecord(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
                    reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }
        finally { _databaseGate.Release(); }
        return results;
    }

    public async Task<HistoryStatistics> GetStatisticsAsync()
    {
        await FlushAsync();
        await _databaseGate.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), MIN(timestamp), MAX(timestamp) FROM messages;";
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            var count = reader.GetInt64(0);
            DateTimeOffset? earliest = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1));
            DateTimeOffset? latest = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2));
            return new HistoryStatistics(count, earliest, latest, GetDatabaseDiskBytes());
        }
        finally { _databaseGate.Release(); }
    }

    public async Task<long> CleanupOlderThanAsync(int days, bool compact = true)
    {
        if (days <= 0) return 0;
        await FlushAsync();
        await _databaseGate.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM messages WHERE timestamp < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.AddDays(-days).ToString("O"));
            var deleted = await command.ExecuteNonQueryAsync();
            if (compact) await CompactAsync(connection);
            return deleted;
        }
        finally { _databaseGate.Release(); }
    }

    public async Task<long> ClearAsync()
    {
        await FlushAsync();
        await _databaseGate.WaitAsync();
        try
        {
            await using var connection = OpenConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM messages;";
            var deleted = await command.ExecuteNonQueryAsync();
            command.CommandText = "DELETE FROM sqlite_sequence WHERE name = 'messages';";
            await command.ExecuteNonQueryAsync();
            await CompactAsync(connection);
            return deleted;
        }
        finally { _databaseGate.Release(); }
    }

    private async Task WriterLoopAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(_cts.Token);
        try
        {
            while (await _writes.Reader.WaitToReadAsync(_cts.Token))
            {
                var batch = new List<HistoryWrite>(200);
                TaskCompletionSource? barrier = null;
                while (batch.Count < 200 && _writes.Reader.TryRead(out var item))
                {
                    if (item is HistoryWrite write) batch.Add(write);
                    else if (item is FlushBarrier flush) { barrier = flush.Completion; break; }
                }
                if (batch.Count == 0) { barrier?.TrySetResult(); continue; }

                await _databaseGate.WaitAsync(_cts.Token);
                try
                {
                    using var transaction = connection.BeginTransaction();
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO messages(timestamp, room, username, text, was_blocked, was_displayed, block_reason)
                        VALUES($timestamp, $room, $username, $text, $blocked, $displayed, $reason);
                        """;
                    var timestamp = command.Parameters.Add("$timestamp", SqliteType.Text);
                    var room = command.Parameters.Add("$room", SqliteType.Text);
                    var username = command.Parameters.Add("$username", SqliteType.Text);
                    var text = command.Parameters.Add("$text", SqliteType.Text);
                    var blocked = command.Parameters.Add("$blocked", SqliteType.Integer);
                    var displayed = command.Parameters.Add("$displayed", SqliteType.Integer);
                    var reason = command.Parameters.Add("$reason", SqliteType.Text);

                    foreach (var item in batch)
                    {
                        timestamp.Value = item.Timestamp.ToString("O");
                        room.Value = item.Room;
                        username.Value = item.UserName;
                        text.Value = item.Text;
                        blocked.Value = item.WasBlocked;
                        displayed.Value = item.WasDisplayed;
                        reason.Value = (object?)item.BlockReason ?? DBNull.Value;
                        await command.ExecuteNonQueryAsync(_cts.Token);
                    }
                    transaction.Commit();
                }
                finally { _databaseGate.Release(); }
                barrier?.TrySetResult();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                room TEXT NOT NULL,
                username TEXT NOT NULL,
                text TEXT NOT NULL,
                was_blocked INTEGER NOT NULL,
                was_displayed INTEGER NOT NULL,
                block_reason TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_messages_timestamp ON messages(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_messages_username ON messages(username);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection() => new($"Data Source={_databasePath};Cache=Shared;Pooling=False");
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private async Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new FlushBarrier(completion)))
            throw new ObjectDisposedException(nameof(HistoryStore));
        await completion.Task;
    }

    private long GetDatabaseDiskBytes() => new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);

    private static async Task CompactAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "VACUUM;";
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        try { await _writerTask.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { await _cts.CancelAsync(); }
        _cts.Dispose();
        _databaseGate.Dispose();
    }

    private abstract record HistoryQueueItem;
    private sealed record HistoryWrite(DateTimeOffset Timestamp, string Room, string UserName, string Text,
        bool WasBlocked, bool WasDisplayed, string? BlockReason) : HistoryQueueItem;
    private sealed record FlushBarrier(TaskCompletionSource Completion) : HistoryQueueItem;
}
