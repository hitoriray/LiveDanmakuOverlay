using System.Globalization;
using System.Text;

namespace LiveDanmakuOverlay;

public sealed class MessageFilter
{
    private readonly AppSettings _settings;
    private readonly object _sync = new();

    public MessageFilter(AppSettings settings) => _settings = settings;

    public bool IsBlocked(DanmakuMessage message, out string? reason)
    {
        lock (_sync)
        {
            var blockedUser = _settings.BlockedUsers.FirstOrDefault(user =>
                string.Equals(user.Trim(), message.UserName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (blockedUser is not null)
            {
                reason = $"用户：{blockedUser}";
                return true;
            }
        }
        if (IsBlocked(message.Text, out var keyword))
        {
            reason = $"关键词：{keyword}";
            return true;
        }
        reason = null;
        return false;
    }

    public bool IsBlocked(string text, out string? matchedKeyword)
    {
        var normalizedText = Normalize(text);
        string[] keywords;
        lock (_sync) keywords = _settings.BlockedKeywords.ToArray();
        foreach (var keyword in keywords)
        {
            var normalizedKeyword = Normalize(keyword);
            if (normalizedKeyword.Length > 0 && normalizedText.Contains(normalizedKeyword, StringComparison.Ordinal))
            {
                matchedKeyword = keyword;
                return true;
            }
        }
        matchedKeyword = null;
        return false;
    }

    public IReadOnlyList<string> GetKeywords()
    {
        lock (_sync) return _settings.BlockedKeywords.ToArray();
    }

    public bool AddKeyword(string keyword)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return false;
        lock (_sync)
        {
            if (_settings.BlockedKeywords.Any(item => string.Equals(item, keyword, StringComparison.OrdinalIgnoreCase)))
                return false;
            _settings.BlockedKeywords.Add(keyword);
            return true;
        }
    }

    public bool RemoveKeyword(string keyword)
    {
        lock (_sync) return _settings.BlockedKeywords.Remove(keyword);
    }

    public IReadOnlyList<string> GetBlockedUsers()
    {
        lock (_sync) return _settings.BlockedUsers.ToArray();
    }

    public bool AddUser(string userName)
    {
        userName = userName.Trim();
        if (userName.Length == 0 || userName == "***" || userName == "匿名") return false;
        lock (_sync)
        {
            if (_settings.BlockedUsers.Any(item => string.Equals(item, userName, StringComparison.OrdinalIgnoreCase)))
                return false;
            _settings.BlockedUsers.Add(userName);
            return true;
        }
    }

    public bool RemoveUser(string userName)
    {
        lock (_sync)
        {
            var existing = _settings.BlockedUsers.FirstOrDefault(item =>
                string.Equals(item, userName, StringComparison.OrdinalIgnoreCase));
            return existing is not null && _settings.BlockedUsers.Remove(existing);
        }
    }

    public static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator or UnicodeCategory.ConnectorPunctuation or
                UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or
                UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or
                UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or
                UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol or
                UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol)
                continue;
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
