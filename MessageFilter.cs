using System.Globalization;
using System.Text;

namespace LiveDanmakuOverlay;

public sealed class MessageFilter
{
    private readonly AppSettings _settings;
    private readonly object _sync = new();

    public MessageFilter(AppSettings settings) => _settings = settings;

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
