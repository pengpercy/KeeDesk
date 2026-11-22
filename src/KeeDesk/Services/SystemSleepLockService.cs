using System.Runtime.InteropServices;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Services;

/// <summary>
/// 跨平台系统休眠与锁屏监听服务：
/// 检测到 macOS 锁屏（CGSessionCopyCurrentDictionary）或系统合盖休眠唤醒（单调时钟跳变）时自动触发回调锁定密码库。
/// </summary>
public sealed class SystemSleepLockService : IDisposable
{
    public event Action<string>? LockRequested;

    private readonly Timer _pollTimer;
    private long _lastTickMs = Environment.TickCount64;
    private DateTimeOffset _lastWallUtc = DateTimeOffset.UtcNow;
    private bool _wasScreenLocked;
    private bool _disposed;

    public SystemSleepLockService()
    {
        _pollTimer = new Timer(OnPollTick, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private void OnPollTick(object? state)
    {
        if (_disposed) return;

        var nowTick = Environment.TickCount64;
        var nowWall = DateTimeOffset.UtcNow;
        var wallDelta = (nowWall - _lastWallUtc).TotalSeconds;
        var tickDelta = (nowTick - _lastTickMs) / 1000.0;

        _lastTickMs = nowTick;
        _lastWallUtc = nowWall;

        // 1. 系统休眠检测：墙钟时间跳变明显大于轮询周期（> 15 秒），说明机器刚从合盖/休眠唤醒
        if (wallDelta > 15 && Math.Abs(wallDelta - tickDelta) > 8 || wallDelta > 20)
        {
            LockRequested?.Invoke(L("Str_VaultsLockedAfterWakingFromSleep"));
            return;
        }

        // 2. macOS 锁屏检测：通过 CoreGraphics CGSessionCopyCurrentDictionary 检测 CGSSessionScreenIsLocked
        if (OperatingSystem.IsMacOS())
        {
            bool locked = IsMacScreenLocked();
            if (locked && !_wasScreenLocked)
            {
                _wasScreenLocked = true;
                LockRequested?.Invoke(L("Str_VaultsLockedWhenMacOSLockedTheScreen"));
            }
            else if (!locked)
            {
                _wasScreenLocked = false;
            }
        }
    }

    // ==================== macOS CoreGraphics + CoreFoundation 锁屏检测 ====================
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGSessionCopyCurrentDictionary();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFDictionaryGetValue(IntPtr theDict, IntPtr key);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFBooleanGetValue(IntPtr boolean);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    private const uint KCfStringEncodingUtf8 = 0x08000100;

    private static bool IsMacScreenLocked()
    {
        IntPtr dict = IntPtr.Zero;
        IntPtr key = IntPtr.Zero;
        try
        {
            dict = CGSessionCopyCurrentDictionary();
            if (dict == IntPtr.Zero) return false;
            key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", KCfStringEncodingUtf8);
            if (key == IntPtr.Zero) return false;
            var val = CFDictionaryGetValue(dict, key);
            return val != IntPtr.Zero && CFBooleanGetValue(val);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (key != IntPtr.Zero) CFRelease(key);
            if (dict != IntPtr.Zero) CFRelease(dict);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
    }
}
