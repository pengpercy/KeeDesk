using System.Runtime.InteropServices;

namespace KeeDesk.Helpers;

internal static class SystemMotionPreferences
{
    private const uint GetClientAreaAnimation = 0x1042;
    private const string Objc = "/usr/lib/libobjc.A.dylib";

    public static TimeSpan FastControlDuration => Duration(100);
    public static TimeSpan SwitchDuration => Duration(200);
    public static TimeSpan ProgressDuration => Duration(300);
    public static TimeSpan TooltipDuration => Duration(150);

    private static TimeSpan Duration(int milliseconds) => ReduceMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);

    public static bool ReduceMotion
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return SystemParametersInfo(GetClientAreaAnimation, 0, out var animationsEnabled, 0) && animationsEnabled == 0;

            if (OperatingSystem.IsMacOSVersionAtLeast(10, 12))
            {
                var workspace = SendObject(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
                return SendBool(workspace, sel_registerName("accessibilityDisplayShouldReduceMotion"));
            }

            return false;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int result, uint flags);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendObject(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendBool(IntPtr receiver, IntPtr selector);
}
