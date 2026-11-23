using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace KeeDesk.Services;

internal sealed partial class WindowsCaptureProtection
{
    private const uint WdaNone = 0;
    private const uint WdaMonitor = 1;
    private const uint WdaExcludeFromCapture = 0x11;

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Win32PlatformOptions _options;
    private readonly HashSet<Window> _failedWindows = [];

    public bool HasFailures => _failedWindows.Count != 0;
    public event Action? StatusChanged;

    public WindowsCaptureProtection(IClassicDesktopStyleApplicationLifetime desktop,
        Win32PlatformOptions options, bool enabled)
    {
        _desktop = desktop;
        _options = options;
        _options.OverlayPopups = enabled;

        // This routed event runs before the native window is shown.
        var opening = Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            if (!_options.OverlayPopups && !_failedWindows.Contains(window)) return;
            if (TryApply(window, _options.OverlayPopups))
            {
                RecordResult(window, true);
                return;
            }

            window.Opened += ApplyAfterShowing;
            void ApplyAfterShowing(object? sender, EventArgs args)
            {
                window.Opened -= ApplyAfterShowing;
                RecordResult(window, TryApply(window, _options.OverlayPopups));
            }
        });
        var closing = Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => RecordResult(window, true));
        desktop.Exit += (_, _) =>
        {
            opening.Dispose();
            closing.Dispose();
        };
    }

    public void SetEnabled(bool enabled)
    {
        // Avalonia reads this option each time it creates a popup. Render menus and tooltips
        // inside their protected window so they do not have separate native handles.
        _options.OverlayPopups = enabled;
        var hadFailures = HasFailures;
        foreach (var window in _desktop.Windows.ToArray())
        {
            if (enabled)
            {
                foreach (var popup in window.OpenedPopups.ToArray())
                    popup.Close();
            }
            SetResult(window, TryApply(window, enabled));
        }
        if (HasFailures != hadFailures) StatusChanged?.Invoke();
    }

    private void RecordResult(Window window, bool applied)
    {
        var hadFailures = HasFailures;
        SetResult(window, applied);
        if (HasFailures != hadFailures) StatusChanged?.Invoke();
    }

    private void SetResult(Window window, bool applied)
    {
        if (applied)
            _failedWindows.Remove(window);
        else
            _failedWindows.Add(window);
    }

    private static bool TryApply(Window window, bool enabled)
    {
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND", Handle: var handle } || handle == 0)
            return false;

        var affinity = !enabled ? WdaNone : OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? WdaExcludeFromCapture
            : WdaMonitor;
        return SetWindowDisplayAffinity(handle, affinity);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);
}
