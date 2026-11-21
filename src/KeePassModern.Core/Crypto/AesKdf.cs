using System.Security.Cryptography;

namespace KeePassModern.Core.Crypto;

/// <summary>
/// AES-256-ECB key derivation function used by KDBX 3.1 and optional in KDBX 4.x.
/// </summary>
public static class AesKdf
{
    public static byte[] DeriveKey(ReadOnlySpan<byte> compositeKey, ReadOnlySpan<byte> seed, ulong rounds)
    {
        if (compositeKey.Length != 32)
            throw new ArgumentException("Composite key must be 32 bytes.", nameof(compositeKey));
        if (seed.Length != 32)
            throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));

        byte[] key = compositeKey.ToArray();
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = seed.ToArray();

        using var encryptor = aes.CreateEncryptor();
        for (ulong i = 0; i < rounds; i++)
        {
            encryptor.TransformBlock(key, 0, 16, key, 0);
            encryptor.TransformBlock(key, 16, 16, key, 16);
        }

        byte[] transformed = SHA256.HashData(key);
        CryptographicOperations.ZeroMemory(key);
        return transformed;
    }
}
