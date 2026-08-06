using System.Buffers.Binary;
using System.IO.Compression;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveDanmakuOverlay;

public sealed class BilibiliDanmakuClient : IAsyncDisposable
{
    private static readonly int[] WbiKeyIndexes =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
        27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13
    ];
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly SemaphoreSlim _switchLock = new(1, 1);
    private CancellationTokenSource? _connectionCts;
    private Task? _connectionTask;

    public event EventHandler<DanmakuMessage>? MessageReceived;
    public event EventHandler<string>? StatusChanged;

    public async Task ConnectAsync(string roomInput)
    {
        var shortRoomId = ParseRoomId(roomInput);
        StatusChanged?.Invoke(this, "正在获取直播间信息…");
        var connection = await ResolveConnectionAsync(shortRoomId, CancellationToken.None);

        await _switchLock.WaitAsync();
        try
        {
            if (_connectionCts is not null)
            {
                await _connectionCts.CancelAsync();
                if (_connectionTask is not null)
                {
                    try { await _connectionTask; } catch (OperationCanceledException) { }
                }
                _connectionCts.Dispose();
            }

            _connectionCts = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionTask = RunReconnectLoopAsync(connection, ready, _connectionCts.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            _switchLock.Release();
        }
    }

    private async Task RunReconnectLoopAsync(ConnectionInfo info, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        var firstAttempt = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke(this, firstAttempt ? "正在连接弹幕服务器…" : "正在重新连接…");
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
                socket.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 LiveDanmakuOverlay/0.1");
                await socket.ConnectAsync(info.ServerUri, cancellationToken);
                await SendPacketAsync(socket, 7, BuildAuthBody(info), cancellationToken);

                StatusChanged?.Invoke(this, $"已连接 · 房间 {info.RoomId}");
                ready.TrySetResult();
                firstAttempt = false;

                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var heartbeat = SendHeartbeatLoopAsync(socket, heartbeatCts.Token);
                try
                {
                    await ReceiveLoopAsync(socket, cancellationToken);
                }
                finally
                {
                    await heartbeatCts.CancelAsync();
                    try { await heartbeat; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ready.TrySetCanceled(cancellationToken);
                break;
            }
            catch (Exception ex)
            {
                if (firstAttempt) ready.TrySetException(ex);
                StatusChanged?.Invoke(this, $"连接中断，5 秒后重试：{FriendlyMessage(ex)}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            ProcessPackets(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
        }
    }

    private void ProcessPackets(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset + 16 <= data.Length)
        {
            var packetLength = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
            var headerLength = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset + 4, 2));
            var version = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset + 6, 2));
            var operation = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 8, 4));
            if (packetLength < headerLength || headerLength < 16 || offset + packetLength > data.Length) break;

            var body = data.Slice(offset + headerLength, packetLength - headerLength);
            if (operation == 5)
            {
                if (version is 2 or 3) ProcessCompressed(body, version);
                else ProcessJson(body);
            }
            offset += packetLength;
        }
    }

    private void ProcessCompressed(ReadOnlySpan<byte> body, int version)
    {
        try
        {
            using var input = new MemoryStream(body.ToArray());
            using Stream decompressor = version == 3
                ? new BrotliStream(input, CompressionMode.Decompress)
                : new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            decompressor.CopyTo(output);
            ProcessPackets(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
        }
        catch (InvalidDataException) { }
    }

    private void ProcessJson(ReadOnlySpan<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            var command = root.TryGetProperty("cmd", out var cmd) ? cmd.GetString() ?? "" : "";
            if (!command.StartsWith("DANMU_MSG", StringComparison.Ordinal)) return;

            var info = root.GetProperty("info");
            var text = info[1].GetString() ?? "";
            var user = info[2][1].GetString() ?? "匿名";
            var (emoticonUrl, emoticonWidth, emoticonHeight) = ParseEmoticon(info);
            if (!string.IsNullOrWhiteSpace(text))
                MessageReceived?.Invoke(this, new DanmakuMessage(user, text, emoticonUrl, emoticonWidth, emoticonHeight));
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }

    private static (string? Url, int Width, int Height) ParseEmoticon(JsonElement info)
    {
        try
        {
            var metadata = info[0];
            if (metadata.GetArrayLength() <= 13 || metadata[12].GetInt32() != 1) return (null, 0, 0);
            var options = metadata[13];
            JsonElement value;
            JsonDocument? parsed = null;
            if (options.ValueKind == JsonValueKind.Object)
            {
                value = options;
            }
            else if (options.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(options.GetString()))
            {
                parsed = JsonDocument.Parse(options.GetString()!);
                value = parsed.RootElement;
            }
            else return (null, 0, 0);

            using (parsed)
            {
                var url = value.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
                var width = value.TryGetProperty("width", out var widthValue) ? widthValue.GetInt32() : 0;
                var height = value.TryGetProperty("height", out var heightValue) ? heightValue.GetInt32() : 0;
                return (url, width, height);
            }
        }
        catch (JsonException) { return (null, 0, 0); }
        catch (InvalidOperationException) { return (null, 0, 0); }
        catch (IndexOutOfRangeException) { return (null, 0, 0); }
    }

    private static async Task SendHeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await SendPacketAsync(socket, 2, "[object Object]", cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
        }
    }

    private static async Task SendPacketAsync(ClientWebSocket socket, int operation, string bodyText, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(bodyText);
        var packet = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), 1);
        body.CopyTo(packet, 16);
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
    }

    private static string BuildAuthBody(ConnectionInfo info) => JsonSerializer.Serialize(new
    {
        uid = 0,
        roomid = info.RoomId,
        protover = 3,
        buvid = info.Buvid,
        platform = "web",
        type = 2,
        key = info.Token
    });

    private static async Task<ConnectionInfo> ResolveConnectionAsync(long shortRoomId, CancellationToken cancellationToken)
    {
        var buvid = await GetBuvidAsync(cancellationToken);
        using var roomDoc = await GetJsonAsync($"https://api.live.bilibili.com/room/v1/Room/room_init?id={shortRoomId}", buvid, cancellationToken);
        EnsureSuccess(roomDoc.RootElement, "直播间不存在或暂时无法访问");
        var roomId = roomDoc.RootElement.GetProperty("data").GetProperty("room_id").GetInt64();

        var signedQuery = await CreateWbiSignedQueryAsync(roomId, buvid, cancellationToken);
        using var serverDoc = await GetJsonAsync($"https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?{signedQuery}", buvid, cancellationToken);
        EnsureSuccess(serverDoc.RootElement, "无法取得弹幕服务器信息");
        var data = serverDoc.RootElement.GetProperty("data");
        var token = data.GetProperty("token").GetString() ?? throw new InvalidOperationException("弹幕令牌为空");
        var host = data.GetProperty("host_list")[0];
        var hostName = host.GetProperty("host").GetString() ?? throw new InvalidOperationException("弹幕服务器地址为空");
        var port = host.GetProperty("wss_port").GetInt32();
        return new ConnectionInfo(roomId, new Uri($"wss://{hostName}:{port}/sub"), token, buvid);
    }

    private static async Task<string> CreateWbiSignedQueryAsync(long roomId, string buvid, CancellationToken cancellationToken)
    {
        using var navDoc = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", buvid, cancellationToken);
        var wbiImage = navDoc.RootElement.GetProperty("data").GetProperty("wbi_img");
        var imageKey = FileNameWithoutExtension(wbiImage.GetProperty("img_url").GetString());
        var subKey = FileNameWithoutExtension(wbiImage.GetProperty("sub_url").GetString());
        var source = imageKey + subKey;
        var mixedKey = string.Concat(WbiKeyIndexes.Where(index => index < source.Length).Select(index => source[index]));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // WBI signs parameters in ordinal key order: id, type, wts.
        var query = $"id={roomId}&type=0&wts={timestamp}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mixedKey));
        return $"{query}&w_rid={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string FileNameWithoutExtension(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("无法取得 B站 WBI 签名密钥");
        return Path.GetFileNameWithoutExtension(uri.AbsolutePath);
    }

    private static async Task<string> GetBuvidAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await GetJsonAsync("https://api.bilibili.com/x/frontend/finger/spi", null, cancellationToken);
            return doc.RootElement.GetProperty("data").GetProperty("b_3").GetString() ?? "";
        }
        catch { return ""; }
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, string? buvid, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/");
        if (!string.IsNullOrWhiteSpace(buvid)) request.Headers.TryAddWithoutValidation("Cookie", $"buvid3={buvid}");
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void EnsureSuccess(JsonElement root, string fallbackMessage)
    {
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
        if (code == 0) return;
        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? fallbackMessage : message);
    }

    private static long ParseRoomId(string input)
    {
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase))
            value = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!long.TryParse(value, out var roomId) || roomId <= 0)
            throw new ArgumentException("请输入 B站直播间链接或纯数字房间号");
        return roomId;
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        WebSocketException => "网络或弹幕服务器连接失败",
        HttpRequestException => "无法访问 B站接口",
        _ => ex.Message
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiveDanmakuOverlay/0.1");
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connectionCts is null) return;
        await _connectionCts.CancelAsync();
        if (_connectionTask is not null)
        {
            try { await _connectionTask; } catch (OperationCanceledException) { }
        }
        _connectionCts.Dispose();
        _connectionCts = null;
    }

    private sealed record ConnectionInfo(long RoomId, Uri ServerUri, string Token, string Buvid);
}
