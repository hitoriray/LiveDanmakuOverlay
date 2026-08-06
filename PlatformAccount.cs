namespace LiveDanmakuOverlay;

public sealed record PlatformAccountStatus(bool IsLoggedIn, string DisplayName, long UserId);
public sealed record QrLoginSession(string Key, string Url, byte[] PngBytes);
public enum QrLoginState { WaitingForScan, WaitingForConfirmation, Success, Expired }
public sealed record QrLoginPollResult(QrLoginState State, string Message);

public interface IPlatformAccountProvider
{
    string PlatformName { get; }
    PlatformAccountStatus Status { get; }
    string CookieHeader { get; }
    Task<QrLoginSession> BeginQrLoginAsync(CancellationToken cancellationToken = default);
    Task<QrLoginPollResult> PollQrLoginAsync(string key, CancellationToken cancellationToken = default);
    Task RefreshStatusAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync();
}
