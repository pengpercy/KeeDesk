using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KeePassModern.Services;

/// <summary>
/// 跨平台前台窗口标题探测与 OS 级全局热键服务（默认 Ctrl+Alt+A / macOS Control+Option+A）。
/// 100% NativeAOT 兼容，无反射。
/// </summary>
public sealed partial class GlobalHotKeyService : IDisposable
{
    public event Action? HotKeyPressed;

    private bool _registered;
    private IntPtr _macHotKeyRef;
    private IntPtr _macHandlerRef;
    private CancellationTokenSource? _winLoopCts;

    /// <summary>
    /// 获取当前操作系统前台活跃窗口的标题（用于全局 Auto-Type 自动匹配条目）。
    /// </summary>
    public static string GetForegroundWindowTitle()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return string.Empty;
                var buf = new char[512];
                int len = GetWindowTextW(hwnd, buf, buf.Length);
                return len > 0 ? new string(buf, 0, len) : string.Empty;
            }

            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("osascript")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(
                    "tell application \"System Events\" to set frontApp to first application process whose frontmost is true\n" +
                    "set appName to name of frontApp\n" +
                    "try\n" +
                    "  set winTitle to name of front window of frontApp\n" +
                    "  return appName & \" - \" & winTitle\n" +
                    "on error\n" +
                    "  return appName\n" +
                    "end try");
                using var proc = Process.Start(psi);
                if (proc is null) return string.Empty;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1500);
                return output;
            }

            if (OperatingSystem.IsLinux())
            {
                var psi = new ProcessStartInfo("xdotool", "getactivewindow getwindowname")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) return string.Empty;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1500);
                return output;
            }
        }
        catch { }

        return string.Empty;
    }

    /// <summary>
    /// 注册全局 Auto-Type 快捷键（Windows: Ctrl+Alt+A；macOS: Control+Option+A）。
    /// </summary>
    public bool TryRegisterDefault()
    {
        if (_registered) return true;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                _registered = TryRegisterMacHotKey();
                return _registered;
            }

            if (OperatingSystem.IsWindows())
            {
                _registered = TryRegisterWindowsHotKey();
                return _registered;
            }
        }
        catch { }

        return false;
    }

    public void Dispose()
    {
        if (!_registered) return;
        _registered = false;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (_macHotKeyRef != IntPtr.Zero) { UnregisterEventHotKey(_macHotKeyRef); _macHotKeyRef = IntPtr.Zero; }
                if (_macHandlerRef != IntPtr.Zero) { RemoveEventHandler(_macHandlerRef); _macHandlerRef = IntPtr.Zero; }
            }
            else if (OperatingSystem.IsWindows())
            {
                _winLoopCts?.Cancel();
                _winLoopCts?.Dispose();
                _winLoopCts = null;
            }
        }
        catch { }
    }

    // ==================== macOS Carbon RegisterEventHotKey ====================
    private const uint KEventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint KEventHotKeyPressed = 6;
    private const uint ControlKeyMask = 0x1000;
    private const uint OptionKeyMask = 0x0800;
    private const uint KeyCodeA = 0x00;

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventHandlerDelegate(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    private EventHandlerDelegate? _macCallbackKeepAlive;

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern IntPtr GetApplicationEventTarget();

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern int InstallEventHandler(IntPtr target, EventHandlerDelegate handler, uint numTypes, in EventTypeSpec list, IntPtr userData, out IntPtr outRef);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern int RemoveEventHandler(IntPtr handlerRef);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern int RegisterEventHotKey(uint inHotKeyCode, uint inHotKeyModifiers, EventHotKeyID inHotKeyID, IntPtr inTarget, uint inOptions, out IntPtr outRef);

    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")]
    private static extern int UnregisterEventHotKey(IntPtr inHotKey);

    private bool TryRegisterMacHotKey()
    {
        var target = GetApplicationEventTarget();
        if (target == IntPtr.Zero) return false;

        _macCallbackKeepAlive = (_, _, _) =>
        {
            HotKeyPressed?.Invoke();
            return 0;
        };

        var spec = new EventTypeSpec { EventClass = KEventClassKeyboard, EventKind = KEventHotKeyPressed };
        int hr1 = InstallEventHandler(target, _macCallbackKeepAlive, 1, in spec, IntPtr.Zero, out _macHandlerRef);
        if (hr1 != 0) return false;

        var id = new EventHotKeyID { Signature = 0x4B504D44, Id = 1 }; // 'KPMD'
        int hr2 = RegisterEventHotKey(KeyCodeA, ControlKeyMask | OptionKeyMask, id, target, 0, out _macHotKeyRef);
        return hr2 == 0;
    }

    // ==================== Windows RegisterHotKey ====================
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkA = 0x41;
    private const uint WmHotKey = 0x0312;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinMsg
    {
        public IntPtr Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out WinMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    private bool TryRegisterWindowsHotKey()
    {
        _winLoopCts = new CancellationTokenSource();
        var token = _winLoopCts.Token;
        var thread = new Thread(() =>
        {
            if (!RegisterHotKey(IntPtr.Zero, 1, ModControl | ModAlt, VkA))
                return;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (PeekMessageW(out var msg, IntPtr.Zero, WmHotKey, WmHotKey, 1))
                        HotKeyPressed?.Invoke();
                    Thread.Sleep(40);
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, 1);
            }
        }) { IsBackground = true, Name = "WinGlobalHotKeyThread" };
        thread.Start();
        return true;
    }
}
