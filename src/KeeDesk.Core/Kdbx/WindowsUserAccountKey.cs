using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KeeDesk.Core.Kdbx;

/// <summary>
/// 100% 兼容原版 KeePass 2.x (KcpUserAccount.cs) 的 Windows 用户账户密钥（DPAPI）。
/// 复用 %APPDATA%\KeePass\ProtectedUserKey.bin 与相同的 16 字节域分离熵值，
/// 与经典 KeePass 2.x 创建的 Windows 账户加密密码库完全互通，且零 unsafe、NativeAOT 友好。
/// </summary>
public static class WindowsUserAccountKey
{
    private const string UserKeyFileName = "ProtectedUserKey.bin";
    private const uint CryptProtectUiForbidden = 0x1;

    // 与原版 KeePass 2.x KcpUserAccount.g_pbDomainSepTag 字节级一致
    private static readonly byte[] DomainSepTag =
    [
        0xDE, 0x13, 0x5B, 0x5F, 0x18, 0xA3, 0x46, 0x70,
        0xB2, 0x57, 0x24, 0x29, 0x69, 0x88, 0x98, 0xE6
    ];

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// 读取或创建当前 Windows 用户的 64 字节 DPAPI 受保护密钥，并返回其 SHA-256 哈希供 CompositeKey 使用。
    /// </summary>
    public static byte[] GetOrCreateKeyHash()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows 用户账户密钥（DPAPI）仅支持在 Windows 操作系统上使用。");

        byte[] plainKey = LoadUserKey() ?? CreateUserKey();
        try
        {
            return SHA256.HashData(plainKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainKey);
        }
    }

    private static string GetUserKeyFilePath(bool createDir)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "KeePass");
        if (createDir && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        return Path.Combine(dir, UserKeyFileName);
    }

    private static byte[]? LoadUserKey()
    {
        var path = GetUserKeyFilePath(createDir: false);
        if (!File.Exists(path)) return null;

        var protectedBytes = File.ReadAllBytes(path);
        if (protectedBytes.Length == 0) return null;

        return DpapiUnprotect(protectedBytes, DomainSepTag);
    }

    private static byte[] CreateUserKey()
    {
        var path = GetUserKeyFilePath(createDir: true);
        var randomKey = RandomNumberGenerator.GetBytes(64);
        var protectedBytes = DpapiProtect(randomKey, DomainSepTag);
        File.WriteAllBytes(path, protectedBytes);
        return randomKey;
    }

    // ==================== Windows crypt32.dll DPAPI P/Invoke（零 unsafe） ====================
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] DpapiProtect(byte[] plain, byte[] entropy)
    {
        var inBlob = AllocBlob(plain);
        var entropyBlob = AllocBlob(entropy);
        DataBlob outBlob = default;
        try
        {
            if (!CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
                throw new CryptographicException($"Windows DPAPI CryptProtectData 失败（Win32 错误码：{Marshal.GetLastWin32Error()}）。");
            return CopyAndFreeLocalBlob(outBlob);
        }
        finally
        {
            FreeHGlobalBlob(inBlob);
            FreeHGlobalBlob(entropyBlob);
        }
    }

    private static byte[] DpapiUnprotect(byte[] encrypted, byte[] entropy)
    {
        var inBlob = AllocBlob(encrypted);
        var entropyBlob = AllocBlob(entropy);
        DataBlob outBlob = default;
        try
        {
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob))
                throw new CryptographicException($"Windows DPAPI CryptUnprotectData 失败（Win32 错误码：{Marshal.GetLastWin32Error()}）。");
            return CopyAndFreeLocalBlob(outBlob);
        }
        finally
        {
            FreeHGlobalBlob(inBlob);
            FreeHGlobalBlob(entropyBlob);
        }
    }

    private static DataBlob AllocBlob(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { CbData = data.Length, PbData = ptr };
    }

    private static void FreeHGlobalBlob(DataBlob blob)
    {
        if (blob.PbData != IntPtr.Zero)
            Marshal.FreeHGlobal(blob.PbData);
    }

    private static byte[] CopyAndFreeLocalBlob(DataBlob blob)
    {
        try
        {
            var result = new byte[blob.CbData];
            if (blob.CbData > 0 && blob.PbData != IntPtr.Zero)
                Marshal.Copy(blob.PbData, result, 0, blob.CbData);
            return result;
        }
        finally
        {
            if (blob.PbData != IntPtr.Zero)
                LocalFree(blob.PbData);
        }
    }
}
