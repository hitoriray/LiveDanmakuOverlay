using System.Windows;

namespace LiveDanmakuOverlay;

internal static class WindowPlacement
{
    public static System.Windows.Point TopRight(Rect workArea, double width, double height, double margin)
    {
        var left = Math.Max(workArea.Left, workArea.Right - width - margin);
        var top = Math.Max(workArea.Top, Math.Min(workArea.Top + margin, workArea.Bottom - height));
        return new System.Windows.Point(left, top);
    }

    public static bool IsVisible(System.Windows.Point position, Rect virtualBounds) =>
        double.IsFinite(position.X) && double.IsFinite(position.Y) &&
        position.X >= virtualBounds.Left - 100 &&
        position.X <= virtualBounds.Right - 100 &&
        position.Y >= virtualBounds.Top - 100 &&
        position.Y <= virtualBounds.Bottom - 100;
}
