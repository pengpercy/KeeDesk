using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Runtime.InteropServices;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Services;

/// <summary>
/// 跨平台原生自动输入执行引擎（macOS CoreGraphics CGEvent / Windows SendInput / Linux xdotool &amp; wtype），
/// 100% NativeAOT 兼容，无反射依赖。
/// </summary>
public static partial class AutoTypeService
{
    public sealed record AutoTypeExecutionResult(bool Success, string Message);

    internal static string? CheckAvailability()
    {
        if (OperatingSystem.IsMacOS() && !AXIsProcessTrusted())
        {
            RequestMacAccessibilityPermissionAndOpenSettings();
            return L("Str_AllowKeeDeskInMacOSAccessibilitySettingsThen");
        }

        if (OperatingSystem.IsLinux() && !AutoTypeTarget.IsX11 && !AutoTypeTarget.IsWayland)
            return L("Str_AutoTypeNoTargetWindow");
        return null;
    }

    internal static async Task<AutoTypeExecutionResult> ExecuteAsync(AutoTypeMatchResult match, AutoTypeTarget target,
        Func<AutoTypePickCharactersStep, CancellationToken, Task<IReadOnlyList<int>?>>? pickCharacters,
        Action? minimizeWaylandPicker, CancellationToken cancellationToken)
    {
        var entry = match.Entry;
        if (!AutoTypeMatcher.IsEnabled(entry))
            return new AutoTypeExecutionResult(false, L("Str_AutoTypeIsDisabledForThisEntry"));

        if (entry.AutoType.DataTransferObfuscation != 0)
            return new AutoTypeExecutionResult(false, L("Str_AutoTypeRequiresObfuscation"));

        var title = entry.Title;
        try
        {
            var steps = AutoTypeSequenceParser.Parse(entry, match.Vault, match.Sequence);
            var prepared = new List<AutoTypeStep>();
            var selections = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step is AutoTypePickCharactersStep pick)
                {
                    if (!selections.TryGetValue(pick.Id, out var positions))
                    {
                        positions = pickCharacters is null ? null : await pickCharacters(pick, cancellationToken);
                        if (positions is null) throw new OperationCanceledException();
                        selections.Add(pick.Id, positions);
                    }
                    prepared.AddRange(pick.CreateSteps(positions));
                }
                else
                    prepared.Add(step);
            }

            ValidateKeys(prepared);
            if (prepared.Count == 0)
                return new AutoTypeExecutionResult(false, L("Str_TheAutoTypeSequenceIsEmpty"));

            cancellationToken.ThrowIfCancellationRequested();
            if (target.UsesWaylandKeyboard && selections.Count > 0)
            {
                minimizeWaylandPicker?.Invoke();
                await Task.Delay(300, cancellationToken);
            }
            if (!await target.RestoreFocusAsync(cancellationToken))
                return new AutoTypeExecutionResult(false, L("Str_AutoTypeTargetChanged"));

            if (!await WaitForShortcutReleaseAsync(cancellationToken))
                return new AutoTypeExecutionResult(false, L("Str_AutoTypeReleaseModifiers"));

            await Task.Run(() => RunSteps(prepared, target, cancellationToken), cancellationToken);
            return new AutoTypeExecutionResult(true, L("Str_AutoTypeCompleteForFormat", title));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is FormatException or OverflowException or NotSupportedException)
        {
            return new AutoTypeExecutionResult(false, L("Str_AutoTypeSequenceUnsupported"));
        }
        catch (InvalidOperationException ex)
        {
            return new AutoTypeExecutionResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ExternalException)
        {
            return new AutoTypeExecutionResult(false, L("Str_AutoTypeInjectionFailed"));
        }
    }

    private static async Task<bool> WaitForShortcutReleaseAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            bool held = OperatingSystem.IsMacOS() && (CGEventSourceFlagsState(0) & 0x1E0000) != 0 ||
                        OperatingSystem.IsWindows() && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => GetAsyncKeyState(key) < 0);
            if (!held) return true;
            await Task.Delay(50, cancellationToken);
        }
        return false;
    }

    private static void ValidateKeys(List<AutoTypeStep> steps)
    {
        foreach (var step in steps)
        {
            if (OperatingSystem.IsMacOS() && step is AutoTypeKeyStep key)
                _ = GetMacKey(key.Key);
            if (step is AutoTypeCharacterKeyStep character)
                _ = DesktopShortcut.GetKeyCode(character.Character);
        }
    }

    private static void RunSteps(List<AutoTypeStep> steps, AutoTypeTarget target, CancellationToken cancellationToken)
    {
        int delay = 12;
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (step)
            {
                case AutoTypeSetDefaultDelayStep setting:
                    delay = setting.Milliseconds;
                    break;
                case AutoTypeDelayStep pause:
                    Wait(pause.Milliseconds, cancellationToken);
                    break;
                case AutoTypeKeyStep key:
                    for (int repeat = 0; repeat < key.Repeat; repeat++)
                    {
                        EnsureFocused(target, cancellationToken);
                        SendKey(key.Key, key.Modifiers, target, cancellationToken);
                        Wait(delay, cancellationToken);
                    }
                    break;
                case AutoTypeCharacterKeyStep character:
                    EnsureFocused(target, cancellationToken);
                    SendCharacterKey(character.Character, character.Modifiers, target, cancellationToken);
                    Wait(delay, cancellationToken);
                    break;
                case AutoTypeClearFieldStep:
                    EnsureFocused(target, cancellationToken);
                    SendCharacterKey('A', OperatingSystem.IsMacOS() ? AutoTypeModifiers.Command : AutoTypeModifiers.Control, target, cancellationToken);
                    Wait(delay, cancellationToken);
                    EnsureFocused(target, cancellationToken);
                    SendKey(AutoTypeKey.Backspace, default, target, cancellationToken);
                    Wait(delay, cancellationToken);
                    break;
                case AutoTypeTotpStep otp:
                    SendText(TotpGenerator.Generate(otp.Settings).Code, delay, target, cancellationToken);
                    break;
                case AutoTypeTextStep text:
                    SendText(text.Text, delay, target, cancellationToken);
                    break;
            }
        }
    }

    private static void EnsureFocused(AutoTypeTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!target.IsFocused()) throw new InvalidOperationException(L("Str_AutoTypeTargetChanged"));
    }

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds > 0 && cancellationToken.WaitHandle.WaitOne(milliseconds))
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static void SendText(string text, int delayMs, AutoTypeTarget target, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsLinux())
        {
            EnsureFocused(target, cancellationToken);
            LinuxSendText(text, delayMs, target, cancellationToken);
            return;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            EnsureFocused(target, cancellationToken);
            if (OperatingSystem.IsMacOS())
                MacSendUnicode(rune, target.ProcessId);
            else if (OperatingSystem.IsWindows())
                WinSendUnicode(rune);
            Wait(delayMs, cancellationToken);
        }
    }

    private static void SendKey(AutoTypeKey key, AutoTypeModifiers modifiers, AutoTypeTarget target,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsMacOS())
            MacSendVirtualKey(GetMacKey(key), modifiers, target.ProcessId);
        else if (OperatingSystem.IsWindows())
            WinSendVirtualKey(GetWindowsKey(key), modifiers, key is AutoTypeKey.Delete or AutoTypeKey.Insert or
                AutoTypeKey.Home or AutoTypeKey.End or AutoTypeKey.PageUp or AutoTypeKey.PageDown or
                AutoTypeKey.Up or AutoTypeKey.Down or AutoTypeKey.Left or AutoTypeKey.Right);
        else if (OperatingSystem.IsLinux())
            LinuxSendKey(GetLinuxKey(key), modifiers, target, cancellationToken);
    }

    private static void SendCharacterKey(char character, AutoTypeModifiers modifiers, AutoTypeTarget target,
        CancellationToken cancellationToken)
    {
        ushort virtualKey = DesktopShortcut.GetKeyCode(character);
        if (OperatingSystem.IsMacOS())
            MacSendVirtualKey(DesktopShortcut.GetMacKeyCode((char)virtualKey), modifiers, target.ProcessId);
        else if (OperatingSystem.IsWindows())
            WinSendVirtualKey(virtualKey, modifiers);
        else if (OperatingSystem.IsLinux())
            LinuxSendKey(char.ToLowerInvariant((char)virtualKey).ToString(), modifiers, target, cancellationToken);
    }

    // ==================== macOS CoreGraphics CGEvent ====================
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
    private static partial void CGEventPostToPid(int processId, IntPtr cgEvent);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial void CGEventSetFlags(IntPtr cgEvent, ulong flags);

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial ulong CGEventSourceFlagsState(int state);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(IntPtr cf);

    private static void MacSendUnicode(Rune rune, int processId)
    {
        Span<char> text = stackalloc char[2];
        int length = rune.EncodeToUtf16(text);
        for (int i = 0; i < 2; i++)
        {
            var key = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, i == 0);
            if (key == 0) throw new InvalidOperationException(L("Str_AutoTypeInjectionFailed"));
            CGEventKeyboardSetUnicodeString(key, (nuint)length, in MemoryMarshal.Cast<char, ushort>(text)[0]);
            CGEventPostToPid(processId, key);
            CFRelease(key);
        }
    }

    private static ushort GetMacKey(AutoTypeKey key) => key switch
    {
        AutoTypeKey.Tab => 48, AutoTypeKey.Enter => 36, AutoTypeKey.Space => 49,
        AutoTypeKey.Backspace => 51, AutoTypeKey.Delete => 117, AutoTypeKey.Escape => 53,
        AutoTypeKey.Up => 126, AutoTypeKey.Down => 125, AutoTypeKey.Left => 123, AutoTypeKey.Right => 124,
        AutoTypeKey.Home => 115, AutoTypeKey.End => 119, AutoTypeKey.PageUp => 116, AutoTypeKey.PageDown => 121,
        AutoTypeKey.F1 => 122, AutoTypeKey.F2 => 120, AutoTypeKey.F3 => 99, AutoTypeKey.F4 => 118,
        AutoTypeKey.F5 => 96, AutoTypeKey.F6 => 97, AutoTypeKey.F7 => 98, AutoTypeKey.F8 => 100,
        AutoTypeKey.F9 => 101, AutoTypeKey.F10 => 109, AutoTypeKey.F11 => 103, AutoTypeKey.F12 => 111,
        AutoTypeKey.F13 => 105, AutoTypeKey.F14 => 107, AutoTypeKey.F15 => 113, AutoTypeKey.F16 => 106,
        _ => throw new NotSupportedException()
    };

    private static void MacSendVirtualKey(ushort key, AutoTypeModifiers modifiers, int processId)
    {
        ulong flags = 0;
        if (modifiers.HasFlag(AutoTypeModifiers.Shift)) flags |= 0x020000;
        if (modifiers.HasFlag(AutoTypeModifiers.Control)) flags |= 0x040000;
        if (modifiers.HasFlag(AutoTypeModifiers.Alt)) flags |= 0x080000;
        if (modifiers.HasFlag(AutoTypeModifiers.Command)) flags |= 0x100000;
        for (int i = 0; i < 2; i++)
        {
            var keyboardEvent = CGEventCreateKeyboardEvent(IntPtr.Zero, key, i == 0);
            if (keyboardEvent == 0) throw new InvalidOperationException(L("Str_AutoTypeInjectionFailed"));
            CGEventSetFlags(keyboardEvent, flags);
            CGEventPostToPid(processId, keyboardEvent);
            CFRelease(keyboardEvent);
        }
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

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);

    private static void WinSendUnicode(Rune rune)
    {
        Span<char> characters = stackalloc char[2];
        int length = rune.EncodeToUtf16(characters);
        var inputs = new WinInput[length * 2];
        for (int i = 0; i < length; i++)
        {
            inputs[i * 2] = CreateInput(0, characters[i], KeyEventFUnicode);
            inputs[i * 2 + 1] = CreateInput(0, characters[i], KeyEventFUnicode | KeyEventFKeyUp);
        }
        WinSend(inputs);
    }

    private static ushort GetWindowsKey(AutoTypeKey key) => key switch
    {
        AutoTypeKey.Tab => 0x09, AutoTypeKey.Enter => 0x0D, AutoTypeKey.Space => 0x20,
        AutoTypeKey.Backspace => 0x08, AutoTypeKey.Delete => 0x2E, AutoTypeKey.Escape => 0x1B,
        AutoTypeKey.Up => 0x26, AutoTypeKey.Down => 0x28, AutoTypeKey.Left => 0x25, AutoTypeKey.Right => 0x27,
        AutoTypeKey.Home => 0x24, AutoTypeKey.End => 0x23, AutoTypeKey.PageUp => 0x21, AutoTypeKey.PageDown => 0x22,
        AutoTypeKey.Insert => 0x2D, >= AutoTypeKey.F1 and <= AutoTypeKey.F16 => (ushort)(0x70 + key - AutoTypeKey.F1),
        _ => throw new NotSupportedException()
    };

    private static void WinSendVirtualKey(ushort key, AutoTypeModifiers modifiers, bool extended = false)
    {
        var inputs = new List<WinInput>();
        var pressed = new Stack<ushort>();
        var keys = new (AutoTypeModifiers Flag, ushort Code)[]
        {
            (AutoTypeModifiers.Control, 0x11), (AutoTypeModifiers.Alt, 0x12), (AutoTypeModifiers.Shift, 0x10)
        };
        foreach (var (flag, code) in keys)
        {
            if (!modifiers.HasFlag(flag)) continue;
            inputs.Add(CreateInput(code, 0, 0));
            pressed.Push(code);
        }
        uint flags = extended ? 1u : 0;
        inputs.Add(CreateInput(key, 0, flags));
        inputs.Add(CreateInput(key, 0, flags | KeyEventFKeyUp));
        foreach (ushort modifier in pressed) inputs.Add(CreateInput(modifier, 0, KeyEventFKeyUp));

        var events = inputs.ToArray();
        uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<WinInput>());
        if (sent == events.Length) return;
        if (sent > 0 && pressed.Count > 0)
        {
            var release = pressed.Select(modifier => CreateInput(modifier, 0, KeyEventFKeyUp)).ToArray();
            _ = SendInput((uint)release.Length, release, Marshal.SizeOf<WinInput>());
        }
        throw new InvalidOperationException(L("Str_AutoTypeInjectionFailed"));
    }

    private static WinInput CreateInput(ushort key, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        U = new WinInputUnion { Ki = new WinKeybdInput { WVk = key, WScan = scan, DwFlags = flags } }
    };

    private static void WinSend(WinInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<WinInput>()) != inputs.Length)
            throw new InvalidOperationException(L("Str_AutoTypeInjectionFailed"));
    }

    private static void LinuxSendText(string text, int delayMs, AutoTypeTarget target, CancellationToken cancellationToken)
    {
        var delay = delayMs.ToString(CultureInfo.InvariantCulture);
        if (target.UsesWaylandKeyboard)
            RunLinuxTool("wtype", ["-d", delay, "-"], text, cancellationToken);
        else
            RunLinuxTool("xdotool", ["type", "--window", target.Window.ToString(CultureInfo.InvariantCulture),
                "--clearmodifiers", "--delay", delay, "--file", "-"], text, cancellationToken);
    }

    private static string GetLinuxKey(AutoTypeKey key) => key switch
    {
        AutoTypeKey.Enter => "Return", AutoTypeKey.Space => "space", AutoTypeKey.Backspace => "BackSpace",
        AutoTypeKey.PageUp => "Page_Up", AutoTypeKey.PageDown => "Page_Down", _ => key.ToString()
    };

    private static void LinuxSendKey(string name, AutoTypeModifiers modifiers, AutoTypeTarget target,
        CancellationToken cancellationToken)
    {
        if (target.UsesWaylandKeyboard)
        {
            var arguments = new List<string>();
            if (modifiers.HasFlag(AutoTypeModifiers.Control)) arguments.AddRange(["-M", "ctrl"]);
            if (modifiers.HasFlag(AutoTypeModifiers.Alt)) arguments.AddRange(["-M", "alt"]);
            if (modifiers.HasFlag(AutoTypeModifiers.Shift)) arguments.AddRange(["-M", "shift"]);
            arguments.AddRange(["-k", name]);
            RunLinuxTool("wtype", arguments, null, cancellationToken);
            return;
        }
        if (modifiers.HasFlag(AutoTypeModifiers.Shift)) name = "shift+" + name;
        if (modifiers.HasFlag(AutoTypeModifiers.Alt)) name = "alt+" + name;
        if (modifiers.HasFlag(AutoTypeModifiers.Control)) name = "ctrl+" + name;
        RunLinuxTool("xdotool", ["key", "--window", target.Window.ToString(CultureInfo.InvariantCulture), "--clearmodifiers", name],
            null, cancellationToken);
    }

    private static void RunLinuxTool(string tool, IEnumerable<string> arguments, string? text, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, RedirectStandardInput = text is not null,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException(L("Str_AutoTypeInjectionFailed"));
        _ = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        try
        {
            if (text is not null)
            {
                if (text.Contains('\0')) throw new FormatException();
                process.StandardInput.Write(text);
                process.StandardInput.Close();
            }
            while (!process.WaitForExit(50))
                cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(L("Str_AutoTypeInjectionFailed"));
        }
        finally
        {
            if (!process.HasExited) process.Kill();
        }
    }
}
