using System.Diagnostics;
using System.Runtime.InteropServices;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Services;

/// <summary>
/// 跨平台原生自动输入执行引擎（macOS CoreGraphics CGEvent / Windows SendInput / Linux xdotool &amp; wtype），
/// 100% NativeAOT 兼容，无反射依赖。
/// </summary>
public static partial class AutoTypeService
{
    public sealed record AutoTypeExecutionResult(bool Success, string Message);

    public static async Task<AutoTypeExecutionResult> ExecuteAsync(KdbxEntry entry, KdbxDatabase? db, Action? minimizeWindowBeforeType = null)
    {
        if (!entry.AutoType.Enabled)
            return new AutoTypeExecutionResult(false, "当前条目已禁用自动输入（Auto-Type）");

        var steps = AutoTypeSequenceParser.Parse(entry, db);
        if (steps.Count == 0)
            return new AutoTypeExecutionResult(false, "自动输入序列为空");

        if (OperatingSystem.IsMacOS() && !AXIsProcessTrusted())
        {
            RequestMacAccessibilityPermissionAndOpenSettings();
            return new AutoTypeExecutionResult(false, "已打开 macOS 辅助功能设置，请勾选允许 KeeDesk 后重试自动输入");
        }

        // 先最小化主窗口，将焦点交还给上一个前台应用（浏览器/终端等）
        minimizeWindowBeforeType?.Invoke();
        await Task.Delay(350);

        try
        {
            await Task.Run(() => RunSteps(steps));
            return new AutoTypeExecutionResult(true, $"已完成「{entry.Title}」自动输入");
        }
        catch (Exception ex)
        {
            return new AutoTypeExecutionResult(false, $"自动输入失败：{ex.Message}");
        }
    }

    private static void RunSteps(List<AutoTypeStep> steps)
    {
        int defaultDelayMs = 12;
        foreach (var step in steps)
        {
            switch (step)
            {
                case AutoTypeSetDefaultDelayStep setDelay:
                    defaultDelayMs = setDelay.Milliseconds;
                    break;
                case AutoTypeDelayStep delay:
                    Thread.Sleep(delay.Milliseconds);
                    break;
                case AutoTypeKeyStep keyStep:
                    for (int i = 0; i < keyStep.Repeat; i++)
                    {
                        SendKey(keyStep.Key);
                        if (defaultDelayMs > 0) Thread.Sleep(defaultDelayMs);
                    }
                    break;
                case AutoTypeTextStep textStep:
                    SendText(textStep.Text, defaultDelayMs);
                    break;
            }
        }
    }

    private static void SendText(string text, int delayMs)
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var ch in text)
            {
                MacSendUnicodeChar(ch);
                if (delayMs > 0) Thread.Sleep(delayMs);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var ch in text)
            {
                WinSendUnicodeChar(ch);
                if (delayMs > 0) Thread.Sleep(delayMs);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxSendText(text);
        }
    }

    private static void SendKey(AutoTypeKey key)
    {
        if (OperatingSystem.IsMacOS())
            MacSendVirtualKey(key);
        else if (OperatingSystem.IsWindows())
            WinSendVirtualKey(key);
        else if (OperatingSystem.IsLinux())
            LinuxSendKey(key);
    }

    // ==================== macOS CoreGraphics CGEvent ====================
    private const uint KCgHidEventTap = 0;

    [LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool AXIsProcessTrusted();

    [LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool AXIsProcessTrustedWithOptions(IntPtr options);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial IntPtr CFDictionaryCreate(
        IntPtr allocator,
        in IntPtr keys,
        in IntPtr values,
        nint numValues,
        IntPtr keyCallBacks,
        IntPtr valueCallBacks);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    /// <summary>
    /// 触发 macOS 原生辅助功能授权提示（自动将本应用加入辅助功能列表）并直接打开系统设置 -> 隐私与安全性 -> 辅助功能面板。
    /// </summary>
    private static void RequestMacAccessibilityPermissionAndOpenSettings()
    {
        try
        {
            // 1. 触发原生 AXTrustedCheckOptionPrompt，自动把 KeeDesk 注册进辅助功能列表
            var cfLib = NativeLibrary.Load("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
            var kCfBooleanTrue = Marshal.ReadIntPtr(NativeLibrary.GetExport(cfLib, "kCFBooleanTrue"));
            var key = CFStringCreateWithCString(IntPtr.Zero, "AXTrustedCheckOptionPrompt", 0x08000100);
            if (key != IntPtr.Zero && kCfBooleanTrue != IntPtr.Zero)
            {
                var dict = CFDictionaryCreate(IntPtr.Zero, in key, in kCfBooleanTrue, 1, IntPtr.Zero, IntPtr.Zero);
                if (dict != IntPtr.Zero)
                {
                    _ = AXIsProcessTrustedWithOptions(dict);
                    CFRelease(dict);
                }
                CFRelease(key);
            }
        }
        catch { }

        try
        {
            // 2. 直接跳转打开「系统设置 -> 隐私与安全性 -> 辅助功能」窗口
            Process.Start(new ProcessStartInfo("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
            {
                UseShellExecute = false
            });
        }
        catch { }
    }

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial void CGEventKeyboardSetUnicodeString(IntPtr cgEvent, nuint stringLength, in ushort unicodeString);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial void CGEventPost(uint tap, IntPtr cgEvent);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(IntPtr cf);

    private static void MacSendUnicodeChar(char ch)
    {
        ushort u = ch;
        var down = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, true);
        if (down != IntPtr.Zero)
        {
            CGEventKeyboardSetUnicodeString(down, 1, in u);
            CGEventPost(KCgHidEventTap, down);
            CFRelease(down);
        }

        var up = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, false);
        if (up != IntPtr.Zero)
        {
            CGEventKeyboardSetUnicodeString(up, 1, in u);
            CGEventPost(KCgHidEventTap, up);
            CFRelease(up);
        }
    }

    private static void MacSendVirtualKey(AutoTypeKey key)
    {
        ushort vk = key switch
        {
            AutoTypeKey.Tab => 48,
            AutoTypeKey.Enter => 36,
            AutoTypeKey.Space => 49,
            AutoTypeKey.Backspace => 51,
            AutoTypeKey.Delete => 117,
            AutoTypeKey.Escape => 53,
            AutoTypeKey.Up => 126,
            AutoTypeKey.Down => 125,
            AutoTypeKey.Left => 123,
            AutoTypeKey.Right => 124,
            AutoTypeKey.Home => 115,
            AutoTypeKey.End => 119,
            _ => 48
        };

        var down = CGEventCreateKeyboardEvent(IntPtr.Zero, vk, true);
        if (down != IntPtr.Zero) { CGEventPost(KCgHidEventTap, down); CFRelease(down); }
        var up = CGEventCreateKeyboardEvent(IntPtr.Zero, vk, false);
        if (up != IntPtr.Zero) { CGEventPost(KCgHidEventTap, up); CFRelease(up); }
    }

    // ==================== Windows user32.dll SendInput ====================
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinInput
    {
        public uint Type;
        public WinInputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct WinInputUnion
    {
        [FieldOffset(0)] public WinKeybdInput Ki;
        [FieldOffset(0)] public WinMouseInput Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinKeybdInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, WinInput[] pInputs, int cbSize);

    private static void WinSendUnicodeChar(char ch)
    {
        var inputs = new WinInput[2];
        inputs[0].Type = InputKeyboard;
        inputs[0].U.Ki = new WinKeybdInput { WScan = ch, DwFlags = KeyEventFUnicode };
        inputs[1].Type = InputKeyboard;
        inputs[1].U.Ki = new WinKeybdInput { WScan = ch, DwFlags = KeyEventFUnicode | KeyEventFKeyUp };
        _ = SendInput(2, inputs, Marshal.SizeOf<WinInput>());
    }

    private static void WinSendVirtualKey(AutoTypeKey key)
    {
        ushort vk = key switch
        {
            AutoTypeKey.Tab => 0x09,
            AutoTypeKey.Enter => 0x0D,
            AutoTypeKey.Space => 0x20,
            AutoTypeKey.Backspace => 0x08,
            AutoTypeKey.Delete => 0x2E,
            AutoTypeKey.Escape => 0x1B,
            AutoTypeKey.Up => 0x26,
            AutoTypeKey.Down => 0x28,
            AutoTypeKey.Left => 0x25,
            AutoTypeKey.Right => 0x27,
            AutoTypeKey.Home => 0x24,
            AutoTypeKey.End => 0x23,
            _ => 0x09
        };
        var inputs = new WinInput[2];
        inputs[0].Type = InputKeyboard;
        inputs[0].U.Ki = new WinKeybdInput { WVk = vk };
        inputs[1].Type = InputKeyboard;
        inputs[1].U.Ki = new WinKeybdInput { WVk = vk, DwFlags = KeyEventFKeyUp };
        _ = SendInput(2, inputs, Marshal.SizeOf<WinInput>());
    }

    // ==================== Linux xdotool / wtype ====================
    private static void LinuxSendText(string text)
    {
        var isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var tool = isWayland ? "wtype" : "xdotool";
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true };
        if (isWayland)
        {
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(text);
        }
        else
        {
            psi.ArgumentList.Add("type");
            psi.ArgumentList.Add("--clearmodifiers");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(text);
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"未找到 {tool}，请先安装 {tool} 以启用 Linux 自动输入");
        proc.WaitForExit(5000);
    }

    private static void LinuxSendKey(AutoTypeKey key)
    {
        var isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var tool = isWayland ? "wtype" : "xdotool";
        var keyName = key switch
        {
            AutoTypeKey.Tab => "Tab",
            AutoTypeKey.Enter => "Return",
            AutoTypeKey.Space => "space",
            AutoTypeKey.Backspace => "BackSpace",
            AutoTypeKey.Delete => "Delete",
            AutoTypeKey.Escape => "Escape",
            AutoTypeKey.Up => "Up",
            AutoTypeKey.Down => "Down",
            AutoTypeKey.Left => "Left",
            AutoTypeKey.Right => "Right",
            AutoTypeKey.Home => "Home",
            AutoTypeKey.End => "End",
            _ => "Tab"
        };
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
        if (isWayland)
        {
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add(keyName);
        }
        else
        {
            psi.ArgumentList.Add("key");
            psi.ArgumentList.Add("--clearmodifiers");
            psi.ArgumentList.Add(keyName);
        }
        using var proc = Process.Start(psi);
        proc?.WaitForExit(3000);
    }
}
