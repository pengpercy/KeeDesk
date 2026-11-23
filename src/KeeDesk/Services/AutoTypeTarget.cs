using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace KeeDesk.Services;

internal sealed class AutoTypeTarget(nint window, int processId, string title) : IDisposable
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const uint Utf8 = 0x08000100;
    public nint Window { get; } = window;
    public int ProcessId { get; } = processId;
    public string Title { get; } = title;
    private const int CommandTimeoutMilliseconds = 3000;

    public bool UsesWaylandKeyboard => ProcessId == -1;
    public static bool IsWayland => OperatingSystem.IsLinux() &&
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
         string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase));

    public static bool IsX11 => OperatingSystem.IsLinux() &&
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) &&
        !IsWayland;

    public static AutoTypeTarget? CaptureManualTarget() =>
        IsWayland ? new AutoTypeTarget(0, -1, string.Empty) : Capture();

    public static AutoTypeTarget? Capture(bool readTitle = true)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                nint window = GetForegroundWindow();
                if (window == 0 || GetWindowThreadProcessId(window, out int pid) == 0) return null;
                var buffer = new char[1024];
                int length = readTitle ? GetWindowTextW(window, buffer, buffer.Length) : 0;
                return new AutoTypeTarget(window, pid, new string(buffer, 0, length));
            }

            if (OperatingSystem.IsMacOS())
                return CaptureMacTarget(readTitle);

            if (IsX11)
            {
                var id = RunXdotool("getactivewindow");
                if (!nint.TryParse(id, CultureInfo.InvariantCulture, out nint window) || window == 0)
                    return null;
                int.TryParse(RunXdotool("getwindowpid", id!), CultureInfo.InvariantCulture, out int pid);
                var title = readTitle ? RunXdotool("getwindowname", id!) : null;
                return new AutoTypeTarget(window, pid, title ?? string.Empty);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { }

        return null;
    }

    public bool IsFocused()
    {
        if (UsesWaylandKeyboard) return true;
        if (OperatingSystem.IsWindows())
            return GetForegroundWindow() == Window && GetWindowThreadProcessId(Window, out int pid) != 0 && pid == ProcessId;
        if (IsX11)
            return RunXdotool("getactivewindow") == Window.ToString(CultureInfo.InvariantCulture);

        using var current = Capture(readTitle: false);
        return current is not null && current.ProcessId == ProcessId &&
            CFEqual(current.Window, Window);
    }

    public async Task<bool> RestoreFocusAsync(CancellationToken cancellationToken)
    {
        if (IsFocused()) return true;
        using var current = Capture(readTitle: false);
        // Only transfer focus away from KeeDesk's own picker.
        if (current?.ProcessId != Environment.ProcessId) return false;

        if (OperatingSystem.IsWindows())
            _ = SetForegroundWindow(Window);
        else if (OperatingSystem.IsMacOS())
        {
            var app = SendObjectWithPid(objc_getClass("NSRunningApplication"),
                sel_registerName("runningApplicationWithProcessIdentifier:"), ProcessId);
            if (app != 0) _ = ActivateMacApp(app, sel_registerName("activateWithOptions:"), 0);
        }
        else if (IsX11)
            _ = RunXdotool("windowactivate", Window.ToString(CultureInfo.InvariantCulture));

        for (int i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
            if (IsFocused()) return true;
        }
        return false;
    }

    private static AutoTypeTarget? CaptureMacTarget(bool readTitle)
    {
        var system = AXUIElementCreateSystemWide();
        if (system == 0) return null;
        nint application = 0;
        try
        {
            application = CopyAttribute(system, "AXFocusedApplication");
            if (application == 0 || AXUIElementGetPid(application, out int pid) != 0) return null;
            // An application with its windows minimized can still be the active app.
            var window = CopyAttribute(application, "AXFocusedWindow");
            if (window == 0)
                return pid == Environment.ProcessId ? new AutoTypeTarget(0, pid, string.Empty) : null;
            return new AutoTypeTarget(window, pid, readTitle ? ReadMacTitle(window) : string.Empty);
        }
        finally
        {
            if (application != 0) CFRelease(application);
            CFRelease(system);
        }
    }

    private static nint CopyAttribute(nint element, string attribute)
    {
        nint name = CFStringCreateWithCString(0, attribute, Utf8);
        if (name == 0) return 0;
        try { return AXUIElementCopyAttributeValue(element, name, out nint value) == 0 ? value : 0; }
        finally { CFRelease(name); }
    }

    private static string ReadMacTitle(nint window)
    {
        nint value = CopyAttribute(window, "AXTitle");
        if (value == 0) return string.Empty;
        try
        {
            if (CFGetTypeID(value) != CFStringGetTypeID()) return string.Empty;
            var buffer = new byte[4096];
            return CFStringGetCString(value, buffer, buffer.Length, Utf8)
                ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)) : string.Empty;
        }
        finally { CFRelease(value); }
    }

    private static string? RunXdotool(params string[] arguments)
    {
        var command = new ProcessStartInfo("xdotool") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) command.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(command);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CommandTimeoutMilliseconds))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public void Dispose()
    {
        if (OperatingSystem.IsMacOS() && Window != 0) CFRelease(Window);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, [Out] char[] buffer, int capacity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);

    [DllImport(ApplicationServices)] private static extern nint AXUIElementCreateSystemWide();
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementGetPid(nint element, out int pid);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFEqual(nint left, nint right);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(nint value, [Out] byte[] buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(Objc)] private static extern nint objc_getClass(string name);
    [DllImport(Objc)] private static extern nint sel_registerName(string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint SendObjectWithPid(nint receiver, nint selector, int pid);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ActivateMacApp(nint receiver, nint selector, nuint options);
}
