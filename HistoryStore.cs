using System.Threading.Channels;
using System.IO;
using Microsoft.Data.Sqlite;

namespace LiveDanmakuOverlay;

public sealed record HistoryRecord(long Id, DateTimeOffset Timestamp, string Room, string UserName,
    string Text, bool WasBlocked, bool WasDisplayed, string? BlockReason);

public sealed class HistoryStore : IAsyncDisposable
{
    private static readonly string DefaultDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveDanmakuOverlay", "history.db");
    private readonly string _databasePath;
    private readonly Channel<HistoryWrite> _writes = Channel.CreateUnbounded<HistoryWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
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

    public async Task<IReadOnlyList<HistoryRecord>> SearchAsync(string query, int limit = 500)
    {
        var results = new List<HistoryRecord>();
        await using var connection = OpenConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp, room, username, text, was_blocked, was_displayed, block_reason
            FROM messages
            WHERE $query = '' OR text LIKE $pattern ESCAPE '\' OR username LIKE $pattern ESCAPE '\' OR room LIKE $pattern ESCAPE '\'
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new HistoryRecord(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
                reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return results;
    }

    private async Task WriterLoopAsync()
    {
        await using var connection = OpenConnection();
        await connection.OpenAsync(_cts.Token);
        try
        {
            await foreach (var item in _writes.Reader.ReadAllAsync(_cts.Token))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO messages(timestamp, room, username, text, was_blocked, was_displayed, block_reason)
                    VALUES($timestamp, $room, $username, $text, $blocked, $displayed, $reason);
                    """;
                command.Parameters.AddWithValue("$timestamp", item.Timestamp.ToString("O"));
                command.Parameters.AddWithValue("$room", item.Room);
                command.Parameters.AddWithValue("$username", item.UserName);
                command.Parameters.AddWithValue("$text", item.Text);
                command.Parameters.AddWithValue("$blocked", item.WasBlocked);
                command.Parameters.AddWithValue("$displayed", item.WasDisplayed);
                command.Parameters.AddWithValue("$reason", (object?)item.BlockReason ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(_cts.Token);
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

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        try { await _writerTask.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { await _cts.CancelAsync(); }
        _cts.Dispose();
    }

    private sealed record HistoryWrite(DateTimeOffset Timestamp, string Room, string UserName, string Text,
        bool WasBlocked, bool WasDisplayed, string? BlockReason);
}
