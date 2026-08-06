namespace LiveDanmakuOverlay;

public sealed record DanmakuMessage(
    string UserName,
    string Text,
    string? EmoticonUrl = null,
    int EmoticonWidth = 0,
    int EmoticonHeight = 0);
