using System.Security.Cryptography;
using System.Text;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Tests;

public class LegacyKdbTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void LegacyImportPreservesGroupsStringsAndAttachment(int cipher)
    {
        var fileKey = SHA256.HashData("fixture key"u8);
        var file = CreateLegacyFile(cipher, fileKey);
        using var read = LegacyKdbReader.Read(file, "café", fileKey, "Migration");
        Assert.Null(read.FilePath);
        Assert.Equal(KdbxConstants.Version40, read.FormatVersion);
        var entry = Assert.Single(read.EnumerateAllEntries());
        Assert.Equal("门户", entry.Title);
        Assert.Equal("Team", entry.Parent?.Parent?.Name);
        Assert.Equal("Work", entry.Parent?.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, entry.Binaries["report.txt"].ReadData());
        Assert.Throws<CryptographicException>(() => LegacyKdbReader.Read(file, "wrong", fileKey, "Migration"));
    }

    private static byte[] CreateLegacyFile(int cipher, byte[] keyFile)
    {
        static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text + "\0");
        using var records = new MemoryStream();
        using var writer = new BinaryWriter(records);
        void Field(ushort fieldType, byte[] value)
        {
            writer.Write(fieldType);
            writer.Write(value.Length);
            writer.Write(value);
        }

        Field(1, BitConverter.GetBytes(1u)); Field(2, Bytes("Team")); Field(8, [0, 0]); Field(0xFFFF, []);
        Field(1, BitConverter.GetBytes(2u)); Field(2, Bytes("Work")); Field(8, [1, 0]); Field(0xFFFF, []);
        Field(1, Guid.NewGuid().ToByteArray()); Field(2, BitConverter.GetBytes(2u));
        Field(4, Bytes("门户")); Field(6, Bytes("example")); Field(7, Bytes("test"));
        Field(13, Bytes("report.txt")); Field(14, [1, 2, 3]); Field(0xFFFF, []);
        var data = records.ToArray();
        byte[] masterSeed = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] transformSeed = RandomNumberGenerator.GetBytes(32);
        const uint rounds = 1000;
        byte[] encoded = [0x63, 0x61, 0x66, 0xE9];
        var rawKey = SHA256.HashData([.. SHA256.HashData(encoded), .. keyFile]);
        var transformed = AesKdf.DeriveKey(rawKey, transformSeed, rounds);
        var key = SHA256.HashData([.. masterSeed, .. transformed]);
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] encrypted = cipher == 2 ? aes.EncryptCbc(data, iv, PaddingMode.PKCS7)
            : new TwofishCipher(key, standardKeyedBoxes: true).EncryptCbc(data, iv);
        using var file = new MemoryStream();
        using var header = new BinaryWriter(file);
        header.Write(0x9AA2D903u); header.Write(0xB54BFB65u); header.Write(cipher);
        header.Write(0x00030002u); header.Write(masterSeed); header.Write(iv);
        header.Write(2u); header.Write(1u); header.Write(SHA256.HashData(data));
        header.Write(transformSeed); header.Write(rounds); header.Write(encrypted);
        return file.ToArray();
    }
}
