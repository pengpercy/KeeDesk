using System.Runtime.InteropServices;
using System.Text;

namespace KeePassModern.Services;

/// <summary>
/// 安全剪贴板防历史留痕服务（100% NativeAOT 兼容）：
/// - macOS: 通过 AppKit NSPasteboard 写入 org.nspasteboard.ConcealedType 与 org.nspasteboard.TransientType，
///   让 Raycast / Alfred / Paste / Maccy 等剪贴板历史工具自动忽略密码。
/// - Windows: 通过 user32.dll 写入 ExcludeClipboardContentFromMonitorProcessing 与 CanIncludeInClipboardHistory=0，
///   防止 Win+V 剪贴板历史与云同步记录敏感密码。
/// </summary>
public static class NativeClipboardSecurityService
{
    public static bool TrySetConcealedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            if (OperatingSystem.IsMacOS())
                return TrySetMacConcealedText(text);
            if (OperatingSystem.IsWindows())
                return TrySetWindowsConcealedText(text);
        }
        catch { }
        return false;
    }

    // ==================== macOS NSPasteboard + ConcealedType / TransientType ====================
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjCLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_IntPtr_Utf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSend_Bool_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    private static IntPtr CreateNSString(string s)
    {
        var cls = objc_getClass("NSString");
        var sel = sel_registerName("stringWithUTF8String:");
        return MsgSend_IntPtr_Utf8(cls, sel, s);
    }

    private static bool TrySetMacConcealedText(string text)
    {
        var pbClass = objc_getClass("NSPasteboard");
        if (pbClass == IntPtr.Zero) return false;

        var generalPb = MsgSend_IntPtr(pbClass, sel_registerName("generalPasteboard"));
        if (generalPb == IntPtr.Zero) return false;

        _ = MsgSend_IntPtr(generalPb, sel_registerName("clearContents"));

        var nsText = CreateNSString(text);
        var typeUtf8 = CreateNSString("public.utf8-plain-text");
        var selSetString = sel_registerName("setString:forType:");
        byte ok = MsgSend_Bool_IntPtr_IntPtr(generalPb, selSetString, nsText, typeUtf8);
        if (ok == 0) return false;

        // 写入空 NSData 作为 ConcealedType 与 TransientType 标记，主流剪贴板管理器会严格遵守此规范
        var emptyData = MsgSend_IntPtr(objc_getClass("NSData"), sel_registerName("data"));
        var selSetData = sel_registerName("setData:forType:");
        _ = MsgSend_Bool_IntPtr_IntPtr(generalPb, selSetData, emptyData, CreateNSString("org.nspasteboard.ConcealedType"));
        _ = MsgSend_Bool_IntPtr_IntPtr(generalPb, selSetData, emptyData, CreateNSString("org.nspasteboard.TransientType"));
        return true;
    }

    // ==================== Windows Win32 Clipboard + ExcludeFromHistory ====================
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private static bool TrySetWindowsConcealedText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();

            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            if (!SetGlobalBytes(CfUnicodeText, bytes))
                return false;

            // 通知 Windows 10/11 剪贴板历史（Win+V）与云剪贴板排除此敏感内容
            uint fmtExclude = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
            if (fmtExclude != 0)
                SetGlobalBytes(fmtExclude, [0, 0, 0, 0]);

            uint fmtHistory = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
            if (fmtHistory != 0)
                SetGlobalBytes(fmtHistory, [0, 0, 0, 0]);

            uint fmtCloud = RegisterClipboardFormatW("CanUploadToCloudClipboard");
            if (fmtCloud != 0)
                SetGlobalBytes(fmtCloud, [0, 0, 0, 0]);

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool SetGlobalBytes(uint format, byte[] data)
    {
        var hMem = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (hMem == IntPtr.Zero) return false;

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (SetClipboardData(format, hMem) == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        return true;
    }
}
