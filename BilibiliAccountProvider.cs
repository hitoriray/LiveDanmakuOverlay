using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace LiveDanmakuOverlay;

public sealed class BilibiliAccountProvider : IPlatformAccountProvider, IDisposable
{
    private static readonly Uri BilibiliUri = new("https://www.bilibili.com/");
    private static readonly string AccountPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiveDanmakuOverlay", "accounts", "bilibili.dat");
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;

    public string PlatformName => "B站";
    public PlatformAccountStatus Status { get; private set; } = new(false, "未登录", 0);
    public string CookieHeader => _cookies.GetCookieHeader(BilibiliUri);

    public BilibiliAccountProvider()
    {
        LoadCookies();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LiveDanmakuOverlay/0.1");
    }

    public async Task<QrLoginSession> BeginQrLoginAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", cancellationToken);
        EnsureSuccess(doc.RootElement);
        var data = doc.RootElement.GetProperty("data");
        var key = data.GetProperty("qrcode_key").GetString() ?? throw new InvalidOperationException("二维码密钥为空");
        var url = data.GetProperty("url").GetString() ?? throw new InvalidOperationException("二维码地址为空");
        using var generator = new QRCodeGenerator();
        using var qrData = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(qrData).GetGraphic(8);
        return new QrLoginSession(key, url, png);
    }

    public async Task<QrLoginPollResult> PollQrLoginAsync(string key, CancellationToken cancellationToken = default)
    {
        using var doc = await GetJsonAsync(
            $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={Uri.EscapeDataString(key)}", cancellationToken);
        EnsureSuccess(doc.RootElement);
        var code = doc.RootElement.GetProperty("data").GetProperty("code").GetInt32();
        switch (code)
        {
            case 0:
                SaveCookies();
                await RefreshStatusAsync(cancellationToken);
                return new(QrLoginState.Success, $"已登录：{Status.DisplayName}");
            case 86090: return new(QrLoginState.WaitingForConfirmation, "已扫码，请在手机上确认");
            case 86038: return new(QrLoginState.Expired, "二维码已过期，请重新获取");
            default: return new(QrLoginState.WaitingForScan, "请使用哔哩哔哩 App 扫码");
        }
    }

    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(CookieHeader))
        {
            Status = new(false, "未登录", 0);
            return;
        }
        try
        {
            using var doc = await GetJsonAsync("https://api.bilibili.com/x/web-interface/nav", cancellationToken);
            var data = doc.RootElement.GetProperty("data");
            if (!data.GetProperty("isLogin").GetBoolean()) throw new InvalidOperationException();
            Status = new(true, data.GetProperty("uname").GetString() ?? "已登录", data.GetProperty("mid").GetInt64());
        }
        catch
        {
            Status = new(false, "登录已失效", 0);
        }
    }

    public Task LogoutAsync()
    {
        foreach (Cookie cookie in _cookies.GetAllCookies()) cookie.Expired = true;
        Status = new(false, "未登录", 0);
        if (File.Exists(AccountPath)) File.Delete(AccountPath);
        return Task.CompletedTask;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private void SaveCookies()
    {
        var values = _cookies.GetAllCookies().Cast<Cookie>()
            .Where(cookie => !cookie.Expired)
            .Select(cookie => new SavedCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Expires))
            .ToArray();
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(AccountPath)!);
        File.WriteAllBytes(AccountPath, encrypted);
    }

    private void LoadCookies()
    {
        try
        {
            if (!File.Exists(AccountPath)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AccountPath), null, DataProtectionScope.CurrentUser);
            foreach (var saved in JsonSerializer.Deserialize<SavedCookie[]>(plain) ?? [])
            {
                var cookie = new Cookie(saved.Name, saved.Value, saved.Path, saved.Domain);
                if (saved.Expires != DateTime.MinValue) cookie.Expires = saved.Expires;
                _cookies.Add(cookie);
            }
        }
        catch { /* Invalid or moved credentials are treated as logged out. */ }
    }

    private static void EnsureSuccess(JsonElement root)
    {
        if (root.GetProperty("code").GetInt32() == 0) return;
        throw new InvalidOperationException(root.TryGetProperty("message", out var message)
            ? message.GetString() ?? "B站登录接口返回错误" : "B站登录接口返回错误");
    }

    public void Dispose() => _http.Dispose();
    private sealed record SavedCookie(string Name, string Value, string Domain, string Path, DateTime Expires);
}
