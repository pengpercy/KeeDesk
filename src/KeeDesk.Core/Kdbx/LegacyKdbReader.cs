using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using KeeDesk.Core.Crypto;

namespace KeeDesk.Core.Kdbx;

/// <summary>Reads KeePass 1.x files into a new, unsaved KDBX model.</summary>
public static class LegacyKdbReader
{
    private const int HeaderSize = 124;
    private static readonly DateTimeOffset Never = new(2999, 12, 28, 23, 59, 59, TimeSpan.Zero);

    public static KdbxDatabase Load(string path, string password, string? keyFilePath)
    {
        var file = File.ReadAllBytes(path);
        var keyFile = keyFilePath is null or "" ? [] : File.ReadAllBytes(keyFilePath);
        try { return Read(file, password, keyFile, Path.GetFileNameWithoutExtension(path)); }
        finally { CryptographicOperations.ZeroMemory(keyFile); }
    }

    internal static KdbxDatabase Read(ReadOnlySpan<byte> file, string password, ReadOnlySpan<byte> keyFile, string name)
    {
        if (file.Length <= HeaderSize || ReadUInt32(file[..4]) != KdbxConstants.Signature1 ||
            ReadUInt32(file.Slice(4, 4)) != 0xB54BFB65)
            throw new InvalidDataException("This file is not a KeePass 1.x database.");

        var version = ReadUInt32(file.Slice(12, 4));
        if ((version & 0xFFFFFF00) != 0x00030000)
            throw new NotSupportedException("This KeePass 1.x database version is unsupported.");
        var cipher = (ReadUInt32(file.Slice(8, 4)) & 0xA) switch
        {
            2 => KdbxCipherAlgorithm.Aes256,
            8 => KdbxCipherAlgorithm.Twofish,
            _ => throw new NotSupportedException("This KeePass 1.x cipher is unsupported.")
        };

        byte[] fileKey = ReadFileKey(keyFile);
        var testedPasswords = new List<byte[]>();
        try
        {
            var encodings = new[] { CodePagesEncodingProvider.Instance.GetEncoding(1252)!, Encoding.Latin1, Encoding.UTF8 };
            foreach (var encoding in encodings)
            {
                byte[] passwordBytes = encoding.GetBytes(password);
                if (testedPasswords.Any(previous => passwordBytes.AsSpan().SequenceEqual(previous)))
                {
                    CryptographicOperations.ZeroMemory(passwordBytes);
                    continue;
                }
                testedPasswords.Add(passwordBytes);
                byte[] key = CreateLegacyKey(passwordBytes, fileKey);
                byte[]? decrypted = null;
                try
                {
                    decrypted = Decrypt(file, key, cipher);
                    if (decrypted is null) continue;
                    return ReadRecords(decrypted, ReadUInt32(file.Slice(48, 4)), ReadUInt32(file.Slice(52, 4)), name);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    if (decrypted is not null) CryptographicOperations.ZeroMemory(decrypted);
                }
            }
            throw new CryptographicException("Wrong KeePass 1.x key or corrupted content.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
            foreach (var passwordBytes in testedPasswords) CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static byte[] ReadFileKey(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or 32) return data.ToArray();
        if (data.Length == 64 && data.IndexOfAnyExcept("0123456789abcdefABCDEF"u8) < 0)
            return Convert.FromHexString(data);
        return SHA256.HashData(data);
    }

    private static byte[] CreateLegacyKey(ReadOnlySpan<byte> password, ReadOnlySpan<byte> fileKey)
    {
        if (fileKey.IsEmpty) return SHA256.HashData(password);
        if (password.IsEmpty) return fileKey.ToArray();

        Span<byte> parts = stackalloc byte[64];
        SHA256.HashData(password, parts[..32]);
        fileKey.CopyTo(parts[32..]);
        var key = SHA256.HashData(parts);
        CryptographicOperations.ZeroMemory(parts);
        return key;
    }

    private static byte[]? Decrypt(ReadOnlySpan<byte> file, byte[] legacyKey, KdbxCipherAlgorithm cipher)
    {
        byte[] transformed = AesKdf.DeriveKey(legacyKey, file.Slice(88, 32), ReadUInt32(file.Slice(120, 4)));
        Span<byte> keyInput = stackalloc byte[48];
        file.Slice(16, 16).CopyTo(keyInput);
        transformed.CopyTo(keyInput[16..]);
        byte[] key = SHA256.HashData(keyInput);
        CryptographicOperations.ZeroMemory(keyInput);
        CryptographicOperations.ZeroMemory(transformed);
        byte[]? content = null;
        try
        {
            if (cipher == KdbxCipherAlgorithm.Twofish)
                content = new TwofishCipher(key, standardKeyedBoxes: true).DecryptCbc(file[HeaderSize..], file.Slice(32, 16));
            else
            {
                using var aes = Aes.Create();
                aes.Key = key;
                content = aes.DecryptCbc(file[HeaderSize..], file.Slice(32, 16), PaddingMode.PKCS7);
            }
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(content, hash);
            if (CryptographicOperations.FixedTimeEquals(hash, file.Slice(56, 32))) return content;
            CryptographicOperations.ZeroMemory(content);
            return null;
        }
        catch (CryptographicException)
        {
            if (content is not null) CryptographicOperations.ZeroMemory(content);
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static KdbxDatabase ReadRecords(ReadOnlySpan<byte> content, uint groups, uint entries, string name)
    {
        var db = new KdbxDatabase();
        db.Meta.DatabaseName = name;
        db.RootGroup.Name = name;
        var byId = new Dictionary<uint, KdbxGroup>();
        var parents = new List<KdbxGroup> { db.RootGroup };
        var reader = new RecordReader(content);
        try
        {
            for (uint i = 0; i < groups; i++)
            {
                var group = new KdbxGroup();
                uint? id = null;
                int depth = 0;
                while (reader.ReadField(out ushort type, out var value))
                {
                    switch (type)
                    {
                        case 1: id = ReadUInt32(value); break;
                        case 2: group.Name = ReadString(value); break;
                        case 3: group.CreationTime = ReadTime(value) ?? group.CreationTime; break;
                        case 4: group.LastModificationTime = ReadTime(value) ?? group.LastModificationTime; break;
                        case 5: group.LastAccessTime = ReadTime(value) ?? group.LastAccessTime; break;
                        case 6: if (ReadTime(value) is { } expiry) { group.Expires = true; group.ExpiryTime = expiry; } break;
                        case 7: group.IconId = unchecked((int)ReadUInt32(value)); break;
                        case 8: depth = value.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(value) : throw InvalidRecords(); break;
                    }
                }

                if (id is null || !byId.TryAdd(id.Value, group) || depth >= parents.Count) throw InvalidRecords();
                parents[depth].AddGroup(group);
                parents.RemoveRange(depth + 1, parents.Count - depth - 1);
                parents.Add(group);
            }

            var uuids = new HashSet<Guid>();
            for (uint i = 0; i < entries; i++)
            {
                var (entry, groupId) = ReadEntry(ref reader);
                if (entry.Title == "Meta-Info" && entry.UserName == "SYSTEM" && entry.Url == "$" &&
                    entry.IconId == 0 && entry.Binaries.ContainsKey("bin-stream"))
                {
                    entry.Dispose();
                    continue;
                }
                if (!uuids.Add(entry.Uuid))
                {
                    entry.Uuid = Guid.NewGuid();
                    uuids.Add(entry.Uuid);
                }
                (byId.GetValueOrDefault(groupId) ?? db.RootGroup).AddEntry(entry);
            }
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static (KdbxEntry Entry, uint GroupId) ReadEntry(ref RecordReader reader)
    {
        var entry = new KdbxEntry();
        uint group = 0;
        string attachment = "attachment";
        try
        {
            while (reader.ReadField(out ushort kind, out var value))
            {
                switch (kind)
                {
                    case 1:
                        if (value.Length != 16) throw InvalidRecords();
                        var id = new Guid(value);
                        if (id != Guid.Empty) entry.Uuid = id;
                        break;
                    case 2: group = ReadUInt32(value); break;
                    case 3: entry.IconId = unchecked((int)ReadUInt32(value)); break;
                    case 4: entry.Title = ReadString(value); break;
                    case 5: entry.Url = ReadString(value); break;
                    case 6: entry.UserName = ReadString(value); break;
                    case 7: entry.Password = ReadString(value); break;
                    case 8: entry.Notes = ReadString(value); break;
                    case 9: entry.CreationTime = ReadTime(value) ?? entry.CreationTime; break;
                    case 10: entry.LastModificationTime = ReadTime(value) ?? entry.LastModificationTime; break;
                    case 11: entry.LastAccessTime = ReadTime(value) ?? entry.LastAccessTime; break;
                    case 12: if (ReadTime(value) is { } expiry) { entry.Expires = true; entry.ExpiryTime = expiry; } break;
                    case 13: attachment = ReadString(value); break;
                    case 14:
                        if (!value.IsEmpty) entry.Binaries[attachment] = new ProtectedBinary(true, value);
                        break;
                }
            }
            entry.LocationChanged = entry.LastModificationTime;
            return (entry, group);
        }
        catch
        {
            entry.Dispose();
            throw;
        }
    }

    private static DateTimeOffset? ReadTime(ReadOnlySpan<byte> values)
    {
        if (values.Length != 5) throw InvalidRecords();
        try
        {
            var time = new DateTimeOffset((values[0] << 6) | (values[1] >> 2), ((values[1] & 3) << 2) | (values[2] >> 6),
                (values[2] >> 1) & 31, ((values[2] & 1) << 4) | (values[3] >> 4), ((values[3] & 15) << 2) | (values[4] >> 6),
                values[4] & 63, TimeSpan.Zero);
            return time == Never ? null : time;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string ReadString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> field) =>
        field.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(field) : throw InvalidRecords();

    private static InvalidDataException InvalidRecords() => new("A KeePass 1.x record has an invalid length or group hierarchy.");

    private ref struct RecordReader(ReadOnlySpan<byte> content)
    {
        private ReadOnlySpan<byte> _remaining = content;

        public bool ReadField(out ushort type, out ReadOnlySpan<byte> value)
        {
            if (_remaining.Length < 6) throw InvalidRecords();
            type = BinaryPrimitives.ReadUInt16LittleEndian(_remaining[..2]);
            uint length = ReadUInt32(_remaining.Slice(2, 4));
            if (length > _remaining.Length - 6) throw InvalidRecords();
            value = _remaining.Slice(6, (int)length);
            _remaining = _remaining[(6 + (int)length)..];
            return type != ushort.MaxValue;
        }
    }
}
