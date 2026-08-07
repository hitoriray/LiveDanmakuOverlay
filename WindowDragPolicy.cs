using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LiveDanmakuOverlay;

internal static class WindowDragPolicy
{
    public static bool CanStart(DependencyObject? source, bool isLocked, WindowState windowState,
        MouseButtonState leftButton) =>
        !isLocked && windowState == WindowState.Normal && leftButton == MouseButtonState.Pressed &&
        !IsWithinInteractiveControl(source);

    private static bool IsWithinInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase or Thumb or
                System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox or Slider)
                return true;

            var parent = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : null;
            source = parent ?? LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
