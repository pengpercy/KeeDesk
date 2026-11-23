using Avalonia;
using Avalonia.Controls;

namespace KeeDesk.Helpers;

internal static class MacWindowPlacement
{
    public static void Attach()
    {
        if (!OperatingSystem.IsMacOS()) return;

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.Opened += CenterFrame);
    }

    private static void CenterFrame(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.Opened -= CenterFrame;
        if (window.WindowStartupLocation != WindowStartupLocation.CenterScreen
            || window.WindowState != WindowState.Normal || window.FrameSize is not { Width: > 0, Height: > 0 } frame)
            return;

        // The macOS backend reports the native title bar as part of the frame only after showing the window.
        var screen = window.Owner is Window owner ? window.Screens.ScreenFromWindow(owner) : null;
        screen ??= window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is not { Scaling: > 0 }) return;

        var area = screen.WorkingArea;
        if (area.Width <= 0 || area.Height <= 0) return;
        var size = PixelSize.FromSize(frame, screen.Scaling);
        var position = new PixelPoint(area.X + Math.Max(0, area.Width - size.Width) / 2,
                                      area.Y + Math.Max(0, area.Height - size.Height) / 2);
        if (window.Position != position)
            window.Position = position;
    }
}
