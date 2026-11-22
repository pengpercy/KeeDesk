using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KeeDesk.Services;

/// <summary>
/// macOS Touch ID 生物识别快速解锁 + 系统钥匙串（Keychain）安全托管服务。
/// 通过原生 LocalAuthentication.framework 弹出 Touch ID 验证框，校验通过后从 Keychain 取出主密码，
/// 零额外依赖、100% NativeAOT 兼容。
/// </summary>
public static unsafe class MacTouchIdService
{
    // Keep the service identifier so Touch ID can find items created before the rename.
    private const string KeychainServiceName = "com.pengpercy.keepassmodern.vault";
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
    private const nint LaPolicyBiometricsOrWatchOrPasscode = 2; // LAPolicyDeviceOwnerAuthentication

    private static bool _frameworkLoaded;

    private static void EnsureFrameworkLoaded()
    {
        if (_frameworkLoaded || !OperatingSystem.IsMacOS()) return;
        _frameworkLoaded = true;
        NativeLibrary.TryLoad("/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication", out _);
    }

    /// <summary>
    /// 检测当前 Mac 是否支持 Touch ID / Apple Watch / 设备密码生物识别。
    /// </summary>
    public static bool IsSupported()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        EnsureFrameworkLoaded();
        try
        {
            var cls = objc_getClass("LAContext");
            if (cls == IntPtr.Zero) return false;
            var ctx = MsgSend_IntPtr(MsgSend_IntPtr(cls, sel_registerName("alloc")), sel_registerName("init"));
            if (ctx == IntPtr.Zero) return false;
            try
            {
                IntPtr err = IntPtr.Zero;
                byte can = MsgSend_Bool_NInt_OutPtr(ctx, sel_registerName("canEvaluatePolicy:error:"), LaPolicyBiometricsOrWatchOrPasscode, ref err);
                return can != 0;
            }
            finally
            {
                MsgSend_Void(ctx, sel_registerName("release"));
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 弹出 macOS 原生 Touch ID / 系统认证框。
    /// </summary>
    public static Task<bool> AuthenticateAsync(string reason)
    {
        if (!OperatingSystem.IsMacOS()) return Task.FromResult(false);
        EnsureFrameworkLoaded();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cls = objc_getClass("LAContext");
            if (cls == IntPtr.Zero) return Task.FromResult(false);
            var ctx = MsgSend_IntPtr(MsgSend_IntPtr(cls, sel_registerName("alloc")), sel_registerName("init"));
            if (ctx == IntPtr.Zero) return Task.FromResult(false);

            var handle = GCHandle.Alloc((tcs, ctx));
            var descriptor = new BlockDescriptor
            {
                Reserved = 0,
                Size = (nuint)sizeof(BlockLiteral)
            };
            var descPtr = (BlockDescriptor*)Marshal.AllocHGlobal(sizeof(BlockDescriptor));
            *descPtr = descriptor;

            var blockPtr = (BlockLiteral*)Marshal.AllocHGlobal(sizeof(BlockLiteral));
            blockPtr->Isa = GetNSConcreteGlobalBlock();
            blockPtr->Flags = 0;
            blockPtr->Reserved = 0;
            blockPtr->Invoke = &OnAuthReply;
            blockPtr->Descriptor = descPtr;
            blockPtr->UserHandle = GCHandle.ToIntPtr(handle);

            var nsReason = MsgSend_IntPtr_Utf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), reason);
            MsgSend_Void_NInt_IntPtr_IntPtr(
                ctx,
                sel_registerName("evaluatePolicy:localizedReason:reply:"),
                LaPolicyBiometricsOrWatchOrPasscode,
                nsReason,
                (IntPtr)blockPtr);
        }
        catch
        {
            tcs.TrySetResult(false);
        }

        return tcs.Task;
    }

    // ==================== macOS Keychain 托管主密码（按密码库路径哈希隔离） ====================
    public static bool HasSavedPassword(string vaultPath)
        => OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(TryLoadPasswordFromKeychain(vaultPath));

    public static bool SavePasswordToKeychain(string vaultPath, string password)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(vaultPath) || string.IsNullOrEmpty(password))
            return false;
        try
        {
            var account = ComputeAccountKey(vaultPath);
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("add-generic-password");
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(KeychainServiceName);
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(account);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(password);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryLoadPasswordFromKeychain(string vaultPath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(vaultPath))
            return null;
        try
        {
            var account = ComputeAccountKey(vaultPath);
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(KeychainServiceName);
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(account);
            psi.ArgumentList.Add("-w");
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    public static void DeletePasswordFromKeychain(string vaultPath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(vaultPath)) return;
        try
        {
            var account = ComputeAccountKey(vaultPath);
            var psi = new ProcessStartInfo("/usr/bin/security")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("delete-generic-password");
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(KeychainServiceName);
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(account);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(3000);
        }
        catch { }
    }

    private static string ComputeAccountKey(string vaultPath)
    {
        var norm = Path.GetFullPath(vaultPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(norm));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    // ==================== ObjC Block & Runtime P/Invoke ====================
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged[Cdecl]<BlockLiteral*, byte, IntPtr, void> Invoke;
        public BlockDescriptor* Descriptor;
        public IntPtr UserHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnAuthReply(BlockLiteral* block, byte success, IntPtr nsError)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(block->UserHandle);
            if (handle.Target is ValueTuple<TaskCompletionSource<bool>, IntPtr> state)
            {
                state.Item1.TrySetResult(success != 0);
                if (state.Item2 != IntPtr.Zero)
                    MsgSend_Void(state.Item2, sel_registerName("release"));
            }
            handle.Free();
        }
        finally
        {
            if (block->Descriptor != null) Marshal.FreeHGlobal((IntPtr)block->Descriptor);
            Marshal.FreeHGlobal((IntPtr)block);
        }
    }

    private static IntPtr GetNSConcreteGlobalBlock()
    {
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        return NativeLibrary.GetExport(lib, "_NSConcreteGlobalBlock");
    }

    [DllImport(ObjCLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_IntPtr_Utf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSend_Bool_NInt_OutPtr(IntPtr receiver, IntPtr selector, nint policy, ref IntPtr error);

    [DllImport(ObjCLib, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void_NInt_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, nint policy, IntPtr reason, IntPtr replyBlock);
}
