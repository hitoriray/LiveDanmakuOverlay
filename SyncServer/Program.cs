using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5091);
    options.Limits.MaxRequestBodySize = 256 * 1024;
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 256 * 1024);

var app = builder.Build();
var dataPath = Environment.GetEnvironmentVariable("SYNC_DATA_PATH") ??
               Path.Combine(AppContext.BaseDirectory, "data", "sync.db");
var userName = Environment.GetEnvironmentVariable("SYNC_USERNAME") ?? "";
var password = Environment.GetEnvironmentVariable("SYNC_PASSWORD") ?? "";
if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
    throw new InvalidOperationException("SYNC_USERNAME and SYNC_PASSWORD must be configured.");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataPath))!);
var store = new SyncStore(dataPath);
await store.InitializeAsync();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health") { await next(); return; }
    if (!BasicAuthentication.IsValid(context.Request.Headers.Authorization, userName, password))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"DanmakuSync\"";
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/api/sync", async () =>
{
    var current = await store.GetCurrentAsync();
    return current is null ? Results.NotFound() : Results.Json(current);
});
app.MapPut("/api/sync", async (SyncUploadRequest request) =>
{
    if (request.BaseRevision < 0 || request.Payload.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { error = "invalid sync payload" });
    var result = await store.TryUpdateAsync(request.BaseRevision, request.Payload);
    return result.Conflict
        ? Results.Json(result.Document, statusCode: StatusCodes.Status409Conflict)
        : Results.Json(result.Document);
});

await app.RunAsync();

internal sealed record SyncUploadRequest(long BaseRevision, JsonElement Payload);
internal sealed record RemoteSyncDocument(long Revision, DateTimeOffset UpdatedAt, JsonElement Payload);
internal sealed record StoreUpdateResult(RemoteSyncDocument Document, bool Conflict);

internal static class BasicAuthentication
{
    public static bool IsValid(string? authorization, string expectedUser, string expectedPassword)
    {
        if (authorization is null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
            var separator = decoded.IndexOf(':');
            if (separator < 0) return false;
            return FixedEquals(decoded[..separator], expectedUser) &&
                   FixedEquals(decoded[(separator + 1)..], expectedPassword);
        }
        catch { return false; }
    }

    private static bool FixedEquals(string value, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(expected));
}

internal sealed class SyncStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SyncStore(string path) => _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS current_sync(
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                revision INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_versions(
                revision INTEGER PRIMARY KEY,
                updated_at TEXT NOT NULL,
                payload TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<RemoteSyncDocument?> GetCurrentAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        return await ReadCurrentAsync(connection, null);
    }

    public async Task<StoreUpdateResult> TryUpdateAsync(long baseRevision, JsonElement payload)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var current = await ReadCurrentAsync(connection, transaction);
            var currentRevision = current?.Revision ?? 0;
            if (baseRevision != currentRevision)
            {
                await transaction.RollbackAsync();
                return new StoreUpdateResult(current ?? new RemoteSyncDocument(0,
                    DateTimeOffset.UnixEpoch, EmptyPayload()), true);
            }

            var next = new RemoteSyncDocument(currentRevision + 1, DateTimeOffset.UtcNow, payload.Clone());
            var json = payload.GetRawText();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO current_sync(singleton, revision, updated_at, payload)
                    VALUES(1, $revision, $updatedAt, $payload)
                    ON CONFLICT(singleton) DO UPDATE SET
                      revision = excluded.revision, updated_at = excluded.updated_at, payload = excluded.payload;
                    INSERT INTO sync_versions(revision, updated_at, payload)
                    VALUES($revision, $updatedAt, $payload);
                    DELETE FROM sync_versions WHERE revision NOT IN
                      (SELECT revision FROM sync_versions ORDER BY revision DESC LIMIT 20);
                    """;
                command.Parameters.AddWithValue("$revision", next.Revision);
                command.Parameters.AddWithValue("$updatedAt", next.UpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$payload", json);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return new StoreUpdateResult(next, false);
        }
        finally { _gate.Release(); }
    }

    private static async Task<RemoteSyncDocument?> ReadCurrentAsync(SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction as SqliteTransaction;
        command.CommandText = "SELECT revision, updated_at, payload FROM current_sync WHERE singleton = 1;";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        using var document = JsonDocument.Parse(reader.GetString(2));
        return new RemoteSyncDocument(reader.GetInt64(0),
            DateTimeOffset.Parse(reader.GetString(1)), document.RootElement.Clone());
    }

    private static JsonElement EmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
