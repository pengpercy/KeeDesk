using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace KeeDesk.Services;

/// <summary>
/// macOS Touch ID 生物识别快速解锁 + 系统钥匙串（Keychain）安全托管服务。
/// 通过原生 LocalAuthentication.framework 弹出 Touch ID 验证框，校验通过后从 Keychain 取出主密码，
/// 零额外依赖、100% NativeAOT 兼容。
/// </summary>
public static class MacTouchIdService
{
    // Keep the service identifier so Touch ID can find items created before the rename.
    private const string KeychainServiceName = "com.pengpercy.keepassmodern.vault";
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";
    private const string SystemLib = "/usr/lib/libSystem.B.dylib";
    private const nint LaPolicyBiometricsOrWatchOrPasscode = 2; // LAPolicyDeviceOwnerAuthentication

    private static IntPtr CreateContext()
    {
        if (LocalAuthentication.Class == IntPtr.Zero) return IntPtr.Zero;
        return MsgSend_IntPtr(MsgSend_IntPtr(LocalAuthentication.Class, sel_registerName("alloc")), sel_registerName("init"));
    }

    private static class LocalAuthentication
    {
        internal static readonly IntPtr Class =
            NativeLibrary.TryLoad("/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication", out _)
                ? objc_getClass("LAContext")
                : IntPtr.Zero;
    }

    /// <summary>
    /// 检测当前 Mac 是否支持 Touch ID / Apple Watch / 设备密码生物识别。
    /// </summary>
    public static bool IsSupported()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var ctx = CreateContext();
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
    public static async Task<bool> AuthenticateAsync(string reason)
    {
        if (!OperatingSystem.IsMacOS()) return false;

        var ctx = IntPtr.Zero;
        try
        {
            ctx = CreateContext();
            if (ctx == IntPtr.Zero) return false;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EvaluatePolicy(ctx, reason, completion);
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ctx != IntPtr.Zero)
                MsgSend_Void(ctx, sel_registerName("release"));
        }
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
    private static void EvaluatePolicy(IntPtr ctx, string reason, TaskCompletionSource<bool> completion)
    {
        var nsReason = MsgSend_IntPtr_Utf8(
            MsgSend_IntPtr(objc_getClass("NSString"), sel_registerName("alloc")),
            sel_registerName("initWithUTF8String:"),
            reason);
        var block = IntPtr.Zero;
        try
        {
            block = CreateAuthReplyBlock(completion);
            MsgSend_Void_NInt_IntPtr_IntPtr(
                ctx, sel_registerName("evaluatePolicy:localizedReason:reply:"),
                LaPolicyBiometricsOrWatchOrPasscode, nsReason, block);
        }
        finally
        {
            if (block != IntPtr.Zero)
                BlockRelease(block);
            MsgSend_Void(nsReason, sel_registerName("release"));
        }
    }

    private static unsafe IntPtr CreateAuthReplyBlock(TaskCompletionSource<bool> completion)
    {
        var literal = new BlockLiteral
        {
            Isa = AuthReplyBlock.StackClass,
            Flags = (1 << 25) | (1 << 30), // BLOCK_HAS_COPY_DISPOSE | BLOCK_HAS_SIGNATURE
            Invoke = &OnAuthReply,
            Descriptor = AuthReplyBlock.Descriptor
        };

        // _Block_copy creates a ref-counted block. Its final release frees the captured handle.
        var handle = GCHandle.Alloc(completion);
        literal.UserHandle = GCHandle.ToIntPtr(handle);
        try
        {
            var block = BlockCopy(&literal);
            if (block == IntPtr.Zero)
                throw new OutOfMemoryException();
            return block;
        }
        catch
        {
            handle.Free();
            throw;
        }
    }

    private static unsafe class AuthReplyBlock
    {
        internal static readonly IntPtr StackClass =
            NativeLibrary.GetExport(NativeLibrary.Load(SystemLib), "_NSConcreteStackBlock");
        internal static readonly BlockDescriptor* Descriptor = CreateDescriptor();

        private static BlockDescriptor* CreateDescriptor()
        {
            var descriptor = (BlockDescriptor*)RuntimeHelpers.AllocateTypeAssociatedMemory(
                typeof(AuthReplyBlock), sizeof(BlockDescriptor));
            // Match Clang's encoding for void (^)(BOOL, NSError *) on each macOS ABI.
            ReadOnlySpan<byte> signature = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "v20@?0B8@\"NSError\"12"u8
                : "v20@?0c8@\"NSError\"12"u8;
            fixed (byte* signaturePtr = signature)
            {
                *descriptor = new BlockDescriptor
                {
                    Size = (nuint)sizeof(BlockLiteral),
                    Copy = &CopyAuthReply,
                    Dispose = &DisposeAuthReply,
                    Signature = signaturePtr
                };
            }
            return descriptor;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public delegate* unmanaged[Cdecl]<BlockLiteral*, BlockLiteral*, void> Copy;
        public delegate* unmanaged[Cdecl]<BlockLiteral*, void> Dispose;
        public byte* Signature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged[Cdecl]<BlockLiteral*, byte, IntPtr, void> Invoke;
        public BlockDescriptor* Descriptor;
        public IntPtr UserHandle;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAuthReply(BlockLiteral* block, byte success, IntPtr nsError)
    {
        if (GCHandle.FromIntPtr(block->UserHandle).Target is TaskCompletionSource<bool> completion)
            completion.TrySetResult(success != 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void CopyAuthReply(BlockLiteral* destination, BlockLiteral* source)
    {
        // The stack block is copied once. Subsequent copies retain the same heap block.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void DisposeAuthReply(BlockLiteral* block)
        => GCHandle.FromIntPtr(block->UserHandle).Free();

    [DllImport(SystemLib, EntryPoint = "_Block_copy")]
    private static extern unsafe IntPtr BlockCopy(BlockLiteral* block);

    [DllImport(SystemLib, EntryPoint = "_Block_release")]
    private static extern void BlockRelease(IntPtr block);

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
