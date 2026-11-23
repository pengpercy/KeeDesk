using System.Buffers.Binary;
using System.Security.Cryptography;
using KeeDesk.Core.Crypto;

namespace KeeDesk.Core.Kdbx;

public sealed class KdbxFileChangedException()
    : IOException("The database file changed on disk after it was opened or while it was being saved.");

/// <summary>
/// 完整的 KDBX 3.1 / 4.0 / 4.1 二进制读写引擎。
/// 支持 AES-256-CBC / ChaCha20 / Twofish 加密，Argon2d / Argon2id / AES-KDF 密钥派生，
/// 以及 KDBX 4 HMAC-SHA-256 块流认证与 KDBX 3.1 HashedBlockStream 校验。
/// </summary>
public static partial class KdbxFormat
{
    private const byte HeaderEnd = 0;
    private const byte HeaderCipherId = 2;
    private const byte HeaderCompressionFlags = 3;
    private const byte HeaderMasterSeed = 4;
    private const byte HeaderTransformSeed = 5;
    private const byte HeaderTransformRounds = 6;
    private const byte HeaderEncryptionIv = 7;
    private const byte HeaderProtectedStreamKey = 8;
    private const byte HeaderStreamStartBytes = 9;
    private const byte HeaderInnerRandomStreamId = 10;
    private const byte HeaderKdfParameters = 11;

    private const byte InnerHeaderEnd = 0;
    private const byte InnerHeaderRandomStreamId = 1;
    private const byte InnerHeaderRandomStreamKey = 2;
    private const byte InnerHeaderBinary = 3;

    public static KdbxDatabase Load(string filePath, CompositeKey key)
    {
        filePath = Path.GetFullPath(filePath);
        byte[] encrypted = File.ReadAllBytes(filePath);
        var db = LoadEncrypted(encrypted, key);
        db.FilePath = filePath;
        db.SavedFileHash = SHA256.HashData(encrypted);
        return db;
    }

    public static KdbxDatabase Load(Stream stream, CompositeKey key)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return LoadEncrypted(ms.ToArray(), key);
    }

    private static KdbxDatabase LoadEncrypted(byte[] allBytes, CompositeKey key)
    {
        int pos = 0;
        uint sig1 = BinaryPrimitives.ReadUInt32LittleEndian(allBytes.AsSpan(pos, 4)); pos += 4;
        uint sig2 = BinaryPrimitives.ReadUInt32LittleEndian(allBytes.AsSpan(pos, 4)); pos += 4;
        if (sig1 != KdbxConstants.Signature1 || sig2 != KdbxConstants.Signature2)
            throw new InvalidDataException("Not a valid KDBX database file.");

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(allBytes.AsSpan(pos, 4)); pos += 4;
        uint major = version >> 16;
        if (major is not (3 or 4))
            throw new NotSupportedException($"Unsupported KDBX major version: {major}");

        bool isKdbx4 = major >= 4;
        var db = new KdbxDatabase { FormatVersion = version };

        byte[]? cipherId = null;
        byte[]? masterSeed = null;
        byte[]? encryptionIv = null;
        byte[]? transformSeed = null;
        ulong transformRounds = 600_000;
        byte[]? protectedStreamKey = null;
        byte[]? streamStartBytes = null;
        var innerStreamAlgo = KdbxInnerStreamAlgorithm.Salsa20;
        VariantDictionary? kdfParams = null;

        while (true)
        {
            byte fieldId = allBytes[pos++];
            int fieldLen;
            if (isKdbx4)
            {
                fieldLen = BinaryPrimitives.ReadInt32LittleEndian(allBytes.AsSpan(pos, 4));
                pos += 4;
            }
            else
            {
                fieldLen = BinaryPrimitives.ReadUInt16LittleEndian(allBytes.AsSpan(pos, 2));
                pos += 2;
            }

            var fieldData = allBytes.AsSpan(pos, fieldLen);
            pos += fieldLen;

            switch (fieldId)
            {
                case HeaderEnd:
                    goto OuterHeaderDone;
                case HeaderCipherId:
                    cipherId = fieldData.ToArray();
                    break;
                case HeaderCompressionFlags:
                    db.Compression = (KdbxCompression)BinaryPrimitives.ReadUInt32LittleEndian(fieldData);
                    break;
                case HeaderMasterSeed:
                    masterSeed = fieldData.ToArray();
                    break;
                case HeaderTransformSeed:
                    transformSeed = fieldData.ToArray();
                    break;
                case HeaderTransformRounds:
                    transformRounds = BinaryPrimitives.ReadUInt64LittleEndian(fieldData);
                    break;
                case HeaderEncryptionIv:
                    encryptionIv = fieldData.ToArray();
                    break;
                case HeaderProtectedStreamKey:
                    protectedStreamKey = fieldData.ToArray();
                    break;
                case HeaderStreamStartBytes:
                    streamStartBytes = fieldData.ToArray();
                    break;
                case HeaderInnerRandomStreamId:
                    innerStreamAlgo = (KdbxInnerStreamAlgorithm)BinaryPrimitives.ReadUInt32LittleEndian(fieldData);
                    break;
                case HeaderKdfParameters:
                    kdfParams = VariantDictionary.Deserialize(fieldData);
                    break;
            }
        }

    OuterHeaderDone:
        int headerEndPos = pos;
        ReadOnlySpan<byte> headerBytes = allBytes.AsSpan(0, headerEndPos);

        db.Cipher = ParseCipher(cipherId ?? throw new InvalidDataException("Missing CipherID."));
        if (masterSeed is null || encryptionIv is null)
            throw new InvalidDataException("Missing MasterSeed or EncryptionIV.");

        byte[] compositeHash = key.ComputeCompositeHash();
        byte[] transformedKey;

        if (isKdbx4)
        {
            if (kdfParams is null) throw new InvalidDataException("Missing KDF parameters in KDBX 4.");
            transformedKey = DeriveKdbx4Key(compositeHash, kdfParams, db);
        }
        else
        {
            db.Kdf = KdbxKdfAlgorithm.AesKdf;
            db.AesKdfRounds = transformRounds;
            if (transformSeed is null) throw new InvalidDataException("Missing TransformSeed in KDBX 3.1.");
            transformedKey = AesKdf.DeriveKey(compositeHash, transformSeed, transformRounds);
        }

        byte[] masterKey = ComputeMasterKey(masterSeed, transformedKey);
        CryptographicOperations.ZeroMemory(compositeHash);

        try
        {
            if (isKdbx4)
            {
                // Verify header SHA-256 hash
                ReadOnlySpan<byte> storedHeaderHash = allBytes.AsSpan(pos, 32); pos += 32;
                byte[] actualHeaderHash = SHA256.HashData(headerBytes);
                if (!CryptographicOperations.FixedTimeEquals(storedHeaderHash, actualHeaderHash))
                    throw new InvalidDataException("KDBX 4 header hash mismatch (file corrupted).");

                // Verify header HMAC-SHA-256
                ReadOnlySpan<byte> storedHeaderHmac = allBytes.AsSpan(pos, 32); pos += 32;
                byte[] hmacKey = ComputeHmacBaseKey(masterSeed, transformedKey);
                byte[] headerHmacKey = ComputeBlockHmacKey(ulong.MaxValue, hmacKey);
                byte[] actualHeaderHmac = HMACSHA256.HashData(headerHmacKey, headerBytes);
                if (!CryptographicOperations.FixedTimeEquals(storedHeaderHmac, actualHeaderHmac))
                    throw new CryptographicException("Invalid master key or corrupted KDBX 4 header.");

                // Read & authenticate HMAC block stream
                byte[] encryptedPayload = ReadHmacBlockStream(allBytes.AsSpan(pos), hmacKey);
                CryptographicOperations.ZeroMemory(hmacKey);

                byte[] decrypted = DecryptPayload(db.Cipher, masterKey, encryptionIv, encryptedPayload);
                byte[] innerData = db.Compression == KdbxCompression.GZip
                    ? KdbxXmlSerializer.DecompressGZip(decrypted)
                    : decrypted;

                // Parse Inner Header
                int innerPos = 0;
                var binaryPool = new List<ProtectedBinary>();
                while (true)
                {
                    byte id = innerData[innerPos++];
                    int len = BinaryPrimitives.ReadInt32LittleEndian(innerData.AsSpan(innerPos, 4));
                    innerPos += 4;
                    var span = innerData.AsSpan(innerPos, len);
                    innerPos += len;

                    if (id == InnerHeaderEnd) break;
                    if (id == InnerHeaderRandomStreamId)
                        innerStreamAlgo = (KdbxInnerStreamAlgorithm)BinaryPrimitives.ReadUInt32LittleEndian(span);
                    else if (id == InnerHeaderRandomStreamKey)
                        protectedStreamKey = span.ToArray();
                    else if (id == InnerHeaderBinary && span.Length >= 1)
                    {
                        bool isProtected = (span[0] & 0x01) != 0;
                        binaryPool.Add(new ProtectedBinary(isProtected, span[1..]));
                    }
                }

                var innerCipher = protectedStreamKey is not null
                    ? new KdbxXmlSerializer.InnerStreamCipher(innerStreamAlgo, protectedStreamKey)
                    : null;

                using var xmlStream = new MemoryStream(innerData, innerPos, innerData.Length - innerPos, writable: false);
                KdbxXmlSerializer.ReadXml(xmlStream, db, isKdbx4: true, innerCipher, binaryPool);
            }
            else
            {
                // KDBX 3.1
                byte[] decrypted = DecryptPayload(db.Cipher, masterKey, encryptionIv, allBytes.AsSpan(pos));
                if (streamStartBytes is null || decrypted.Length < streamStartBytes.Length ||
                    !CryptographicOperations.FixedTimeEquals(decrypted.AsSpan(0, streamStartBytes.Length), streamStartBytes))
                {
                    throw new CryptographicException("Invalid master key for KDBX 3.1 database.");
                }

                byte[] rawPayload = ReadHashedBlockStream(decrypted.AsSpan(streamStartBytes.Length));
                byte[] xmlBytes = db.Compression == KdbxCompression.GZip
                    ? KdbxXmlSerializer.DecompressGZip(rawPayload)
                    : rawPayload;

                var innerCipher = protectedStreamKey is not null
                    ? new KdbxXmlSerializer.InnerStreamCipher(innerStreamAlgo, protectedStreamKey)
                    : null;

                using var xmlStream = new MemoryStream(xmlBytes, writable: false);
                KdbxXmlSerializer.ReadXml(xmlStream, db, isKdbx4: false, innerCipher, new List<ProtectedBinary>());
            }

            db.IsModified = false;
            return db;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transformedKey);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public static void Save(KdbxDatabase db, Stream output, CompositeKey key)
    {
        bool useKdbx3 = (db.FormatVersion >> 16) == 3;
        if (useKdbx3)
        {
            SaveKdbx31(db, output, key);
            return;
        }

        // Conditional KDBX 4.1 upgrade
        uint version = db.RequiresKdbx41() ? KdbxConstants.Version41 : KdbxConstants.Version40;
        db.FormatVersion = version;

        byte[] masterSeed = RandomNumberGenerator.GetBytes(32);
        byte[] kdfSalt = RandomNumberGenerator.GetBytes(32);
        byte[] encryptionIv = RandomNumberGenerator.GetBytes(db.Cipher == KdbxCipherAlgorithm.ChaCha20 ? 12 : 16);
        byte[] innerStreamKey = RandomNumberGenerator.GetBytes(64);

        var kdfParams = BuildKdfParameters(db, kdfSalt);
        byte[] compositeHash = key.ComputeCompositeHash();
        byte[] transformedKey = DeriveKdbx4Key(compositeHash, kdfParams, db);
        byte[] masterKey = ComputeMasterKey(masterSeed, transformedKey);
        byte[] hmacBaseKey = ComputeHmacBaseKey(masterSeed, transformedKey);
        CryptographicOperations.ZeroMemory(compositeHash);

        try
        {
            // 1. Build Outer Header
            byte[] headerBytes;
            using (var hms = new MemoryStream())
            using (var bw = new BinaryWriter(hms))
            {
                bw.Write(KdbxConstants.Signature1);
                bw.Write(KdbxConstants.Signature2);
                bw.Write(version);

                WriteKdbx4HeaderField(bw, HeaderCipherId, GetCipherUuid(db.Cipher));
                WriteKdbx4HeaderField(bw, HeaderCompressionFlags, BitConverter.GetBytes((uint)db.Compression));
                WriteKdbx4HeaderField(bw, HeaderMasterSeed, masterSeed);
                WriteKdbx4HeaderField(bw, HeaderEncryptionIv, encryptionIv);
                WriteKdbx4HeaderField(bw, HeaderKdfParameters, kdfParams.Serialize());
                WriteKdbx4HeaderField(bw, HeaderEnd, [0x0D, 0x0A, 0x0D, 0x0A]);
                headerBytes = hms.ToArray();
            }

            // 2. Serialize XML with Inner Stream encryption
            var innerCipher = new KdbxXmlSerializer.InnerStreamCipher(KdbxInnerStreamAlgorithm.ChaCha20, innerStreamKey);
            var binaryPool = new List<ProtectedBinary>();
            byte[] xmlBytes = KdbxXmlSerializer.WriteXml(db, isKdbx4: true, innerCipher, binaryPool);

            // 3. Build Inner Header + XML
            byte[] plainInner;
            using (var ims = new MemoryStream())
            using (var bw = new BinaryWriter(ims))
            {
                WriteKdbx4HeaderField(bw, InnerHeaderRandomStreamId, BitConverter.GetBytes((uint)KdbxInnerStreamAlgorithm.ChaCha20));
                WriteKdbx4HeaderField(bw, InnerHeaderRandomStreamKey, innerStreamKey);
                foreach (var bin in binaryPool)
                {
                    var data = bin.ReadData();
                    var item = new byte[1 + data.Length];
                    item[0] = bin.IsProtected ? (byte)0x01 : (byte)0x00;
                    Buffer.BlockCopy(data, 0, item, 1, data.Length);
                    WriteKdbx4HeaderField(bw, InnerHeaderBinary, item);
                    CryptographicOperations.ZeroMemory(data);
                }
                WriteKdbx4HeaderField(bw, InnerHeaderEnd, Array.Empty<byte>());
                bw.Write(xmlBytes);
                plainInner = ims.ToArray();
            }

            byte[] compressed = db.Compression == KdbxCompression.GZip
                ? KdbxXmlSerializer.CompressGZip(plainInner)
                : plainInner;
            byte[] encrypted = EncryptPayload(db.Cipher, masterKey, encryptionIv, compressed);

            // 4. Write Header + SHA256 + HeaderHMAC + HMAC Block Stream
            output.Write(headerBytes);
            output.Write(SHA256.HashData(headerBytes));

            byte[] headerHmacKey = ComputeBlockHmacKey(ulong.MaxValue, hmacBaseKey);
            output.Write(HMACSHA256.HashData(headerHmacKey, headerBytes));

            WriteHmacBlockStream(output, encrypted, hmacBaseKey);
            db.IsModified = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transformedKey);
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(hmacBaseKey);
        }
    }

    private static void SaveKdbx31(KdbxDatabase db, Stream output, CompositeKey key)
    {
        byte[] masterSeed = RandomNumberGenerator.GetBytes(32);
        byte[] transformSeed = RandomNumberGenerator.GetBytes(32);
        byte[] encryptionIv = RandomNumberGenerator.GetBytes(db.Cipher == KdbxCipherAlgorithm.ChaCha20 ? 12 : 16);
        byte[] protectedStreamKey = RandomNumberGenerator.GetBytes(32);
        byte[] streamStartBytes = RandomNumberGenerator.GetBytes(32);

        byte[] compositeHash = key.ComputeCompositeHash();
        byte[] transformedKey = AesKdf.DeriveKey(compositeHash, transformSeed, db.AesKdfRounds);
        byte[] masterKey = ComputeMasterKey(masterSeed, transformedKey);
        CryptographicOperations.ZeroMemory(compositeHash);

        try
        {
            byte[] headerBytes;
            using (var hms = new MemoryStream())
            using (var bw = new BinaryWriter(hms))
            {
                bw.Write(KdbxConstants.Signature1);
                bw.Write(KdbxConstants.Signature2);
                bw.Write(KdbxConstants.Version31);

                WriteKdbx3HeaderField(bw, HeaderCipherId, GetCipherUuid(db.Cipher));
                WriteKdbx3HeaderField(bw, HeaderCompressionFlags, BitConverter.GetBytes((uint)db.Compression));
                WriteKdbx3HeaderField(bw, HeaderMasterSeed, masterSeed);
                WriteKdbx3HeaderField(bw, HeaderTransformSeed, transformSeed);
                WriteKdbx3HeaderField(bw, HeaderTransformRounds, BitConverter.GetBytes(db.AesKdfRounds));
                WriteKdbx3HeaderField(bw, HeaderEncryptionIv, encryptionIv);
                WriteKdbx3HeaderField(bw, HeaderProtectedStreamKey, protectedStreamKey);
                WriteKdbx3HeaderField(bw, HeaderStreamStartBytes, streamStartBytes);
                WriteKdbx3HeaderField(bw, HeaderInnerRandomStreamId, BitConverter.GetBytes((uint)KdbxInnerStreamAlgorithm.Salsa20));
                WriteKdbx3HeaderField(bw, HeaderEnd, [0x0D, 0x0A, 0x0D, 0x0A]);
                headerBytes = hms.ToArray();
            }

            byte[] headerHash = SHA256.HashData(headerBytes);
            var innerCipher = new KdbxXmlSerializer.InnerStreamCipher(KdbxInnerStreamAlgorithm.Salsa20, protectedStreamKey);
            byte[] xmlBytes = KdbxXmlSerializer.WriteXml(db, isKdbx4: false, innerCipher, new List<ProtectedBinary>(), headerHash);

            byte[] payload = db.Compression == KdbxCompression.GZip
                ? KdbxXmlSerializer.CompressGZip(xmlBytes)
                : xmlBytes;

            byte[] hashedStream = WriteHashedBlockStream(payload);
            byte[] plainStream = new byte[streamStartBytes.Length + hashedStream.Length];
            Buffer.BlockCopy(streamStartBytes, 0, plainStream, 0, streamStartBytes.Length);
            Buffer.BlockCopy(hashedStream, 0, plainStream, streamStartBytes.Length, hashedStream.Length);

            byte[] encrypted = EncryptPayload(db.Cipher, masterKey, encryptionIv, plainStream);
            output.Write(headerBytes);
            output.Write(encrypted);
            db.IsModified = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transformedKey);
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static VariantDictionary BuildKdfParameters(KdbxDatabase db, byte[] salt)
    {
        var dict = new VariantDictionary();
        if (db.Kdf == KdbxKdfAlgorithm.AesKdf)
        {
            dict.SetByteArray("$UUID", KdbxConstants.KdfAesKdbx4);
            dict.SetByteArray("S", salt);
            dict.SetUInt64("R", db.AesKdfRounds);
        }
        else
        {
            dict.SetByteArray("$UUID", db.Kdf == KdbxKdfAlgorithm.Argon2d ? KdbxConstants.KdfArgon2d : KdbxConstants.KdfArgon2id);
            dict.SetByteArray("S", salt);
            dict.SetUInt32("P", Math.Max(1u, db.KdfParallelism));
            dict.SetUInt64("M", Math.Max(8192UL * 1024, db.KdfMemoryBytes));
            dict.SetUInt64("I", Math.Max(1UL, db.KdfIterations));
            dict.SetUInt32("V", 0x13);
        }
        return dict;
    }

    private static byte[] DeriveKdbx4Key(byte[] compositeHash, VariantDictionary kdfParams, KdbxDatabase db)
    {
        byte[] uuid = kdfParams.GetByteArray("$UUID") ?? throw new InvalidDataException("Missing KDF UUID.");
        byte[] salt = kdfParams.GetByteArray("S") ?? throw new InvalidDataException("Missing KDF salt.");

        if (uuid.AsSpan().SequenceEqual(KdbxConstants.KdfArgon2d) || uuid.AsSpan().SequenceEqual(KdbxConstants.KdfArgon2id))
        {
            bool isId = uuid.AsSpan().SequenceEqual(KdbxConstants.KdfArgon2id);
            db.Kdf = isId ? KdbxKdfAlgorithm.Argon2id : KdbxKdfAlgorithm.Argon2d;
            db.KdfParallelism = kdfParams.GetUInt32("P", 2);
            db.KdfMemoryBytes = kdfParams.GetUInt64("M", 64UL * 1024 * 1024);
            db.KdfIterations = kdfParams.GetUInt64("I", 3);
            uint version = kdfParams.GetUInt32("V", 0x13);
            byte[]? secret = kdfParams.GetByteArray("K");
            byte[]? assoc = kdfParams.GetByteArray("A");

            return Argon2.DeriveKey(
                compositeHash, salt,
                isId ? Argon2Type.Argon2id : Argon2Type.Argon2d,
                version, (uint)db.KdfIterations, db.KdfMemoryBytes, db.KdfParallelism,
                32, secret ?? ReadOnlySpan<byte>.Empty, assoc ?? ReadOnlySpan<byte>.Empty);
        }

        if (uuid.AsSpan().SequenceEqual(KdbxConstants.KdfAesKdbx4) || uuid.AsSpan().SequenceEqual(KdbxConstants.KdfAesKdbx3))
        {
            db.Kdf = KdbxKdfAlgorithm.AesKdf;
            db.AesKdfRounds = kdfParams.GetUInt64("R", 600_000);
            return AesKdf.DeriveKey(compositeHash, salt, db.AesKdfRounds);
        }

        throw new NotSupportedException($"Unsupported KDF UUID: {Convert.ToHexString(uuid)}");
    }

    private static byte[] ComputeMasterKey(byte[] masterSeed, byte[] transformedKey)
    {
        byte[] buf = new byte[masterSeed.Length + transformedKey.Length];
        Buffer.BlockCopy(masterSeed, 0, buf, 0, masterSeed.Length);
        Buffer.BlockCopy(transformedKey, 0, buf, masterSeed.Length, transformedKey.Length);
        byte[] result = SHA256.HashData(buf);
        CryptographicOperations.ZeroMemory(buf);
        return result;
    }

    private static byte[] ComputeHmacBaseKey(byte[] masterSeed, byte[] transformedKey)
    {
        byte[] buf = new byte[masterSeed.Length + transformedKey.Length + 1];
        Buffer.BlockCopy(masterSeed, 0, buf, 0, masterSeed.Length);
        Buffer.BlockCopy(transformedKey, 0, buf, masterSeed.Length, transformedKey.Length);
        buf[^1] = 0x01;
        byte[] result = SHA512.HashData(buf);
        CryptographicOperations.ZeroMemory(buf);
        return result;
    }

    private static byte[] ComputeBlockHmacKey(ulong blockIndex, byte[] hmacBaseKey)
    {
        byte[] buf = new byte[8 + hmacBaseKey.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(0, 8), blockIndex);
        Buffer.BlockCopy(hmacBaseKey, 0, buf, 8, hmacBaseKey.Length);
        byte[] result = SHA512.HashData(buf);
        CryptographicOperations.ZeroMemory(buf);
        return result;
    }

    private static byte[] ReadHmacBlockStream(ReadOnlySpan<byte> data, byte[] hmacBaseKey)
    {
        using var ms = new MemoryStream();
        int pos = 0;
        ulong blockIndex = 0;

        while (pos < data.Length)
        {
            var storedHmac = data.Slice(pos, 32); pos += 32;
            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4)); pos += 4;
            var blockData = data.Slice(pos, blockSize); pos += blockSize;

            byte[] blockKey = ComputeBlockHmacKey(blockIndex, hmacBaseKey);
            using var hmac = new HMACSHA256(blockKey);
            byte[] prefix = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(0, 8), blockIndex);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8, 4), blockSize);
            hmac.TransformBlock(prefix, 0, 12, null, 0);
            hmac.TransformFinalBlock(blockData.ToArray(), 0, blockSize);
            CryptographicOperations.ZeroMemory(blockKey);

            if (!CryptographicOperations.FixedTimeEquals(storedHmac, hmac.Hash!))
                throw new CryptographicException($"HMAC verification failed on block {blockIndex}.");

            if (blockSize == 0) break;
            ms.Write(blockData);
            blockIndex++;
        }

        return ms.ToArray();
    }

    private static void WriteHmacBlockStream(Stream output, byte[] data, byte[] hmacBaseKey)
    {
        const int chunkSize = 1024 * 1024; // 1 MiB blocks
        ulong blockIndex = 0;
        int offset = 0;

        while (offset < data.Length)
        {
            int len = Math.Min(chunkSize, data.Length - offset);
            WriteHmacBlock(output, blockIndex++, data.AsSpan(offset, len), hmacBaseKey);
            offset += len;
        }
        // Terminating empty block
        WriteHmacBlock(output, blockIndex, ReadOnlySpan<byte>.Empty, hmacBaseKey);
    }

    private static void WriteHmacBlock(Stream output, ulong blockIndex, ReadOnlySpan<byte> blockData, byte[] hmacBaseKey)
    {
        byte[] blockKey = ComputeBlockHmacKey(blockIndex, hmacBaseKey);
        byte[] prefix = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix.AsSpan(0, 8), blockIndex);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8, 4), blockData.Length);

        using var hmac = new HMACSHA256(blockKey);
        hmac.TransformBlock(prefix, 0, 12, null, 0);
        hmac.TransformFinalBlock(blockData.ToArray(), 0, blockData.Length);
        CryptographicOperations.ZeroMemory(blockKey);

        output.Write(hmac.Hash!);
        output.Write(prefix, 8, 4);
        if (!blockData.IsEmpty)
            output.Write(blockData);
    }

    private static byte[] ReadHashedBlockStream(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        int pos = 0;
        uint expectedIdx = 0;

        while (pos < data.Length)
        {
            uint idx = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4)); pos += 4;
            if (idx != expectedIdx++) throw new InvalidDataException("Invalid KDBX 3.1 block index.");
            var storedHash = data.Slice(pos, 32); pos += 32;
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(pos, 4)); pos += 4;
            if (size == 0) break;

            var block = data.Slice(pos, size); pos += size;
            byte[] actualHash = SHA256.HashData(block);
            if (!CryptographicOperations.FixedTimeEquals(storedHash, actualHash))
                throw new InvalidDataException("Corrupted KDBX 3.1 hashed block.");
            ms.Write(block);
        }

        return ms.ToArray();
    }

    private static byte[] WriteHashedBlockStream(byte[] data)
    {
        const int chunkSize = 1024 * 1024;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        uint idx = 0;
        int offset = 0;
        while (offset < data.Length)
        {
            int len = Math.Min(chunkSize, data.Length - offset);
            var chunk = data.AsSpan(offset, len);
            bw.Write(idx++);
            bw.Write(SHA256.HashData(chunk));
            bw.Write(len);
            bw.Write(chunk);
            offset += len;
        }
        bw.Write(idx);
        bw.Write(new byte[32]);
        bw.Write(0);
        return ms.ToArray();
    }

    private static byte[] EncryptPayload(KdbxCipherAlgorithm cipher, byte[] key, byte[] iv, byte[] plaintext)
    {
        return cipher switch
        {
            KdbxCipherAlgorithm.Aes256 => EncryptAes(key, iv, plaintext),
            KdbxCipherAlgorithm.ChaCha20 => new ChaCha20Cipher(key, iv).ProcessBytes(plaintext),
            KdbxCipherAlgorithm.Twofish => new TwofishCipher(key).EncryptCbc(plaintext, iv),
            _ => throw new NotSupportedException($"Unsupported cipher: {cipher}")
        };
    }

    private static byte[] DecryptPayload(KdbxCipherAlgorithm cipher, byte[] key, byte[] iv, ReadOnlySpan<byte> ciphertext)
    {
        return cipher switch
        {
            KdbxCipherAlgorithm.Aes256 => DecryptAes(key, iv, ciphertext),
            KdbxCipherAlgorithm.ChaCha20 => new ChaCha20Cipher(key, iv).ProcessBytes(ciphertext),
            KdbxCipherAlgorithm.Twofish => new TwofishCipher(key).DecryptCbc(ciphertext, iv),
            _ => throw new NotSupportedException($"Unsupported cipher: {cipher}")
        };
    }

    private static byte[] EncryptAes(byte[] key, byte[] iv, byte[] plaintext)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
    }

    private static byte[] DecryptAes(byte[] key, byte[] iv, ReadOnlySpan<byte> ciphertext)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(ciphertext.ToArray(), 0, ciphertext.Length);
    }

    private static KdbxCipherAlgorithm ParseCipher(byte[] uuid)
    {
        if (uuid.AsSpan().SequenceEqual(KdbxConstants.CipherAes256)) return KdbxCipherAlgorithm.Aes256;
        if (uuid.AsSpan().SequenceEqual(KdbxConstants.CipherChaCha20)) return KdbxCipherAlgorithm.ChaCha20;
        if (uuid.AsSpan().SequenceEqual(KdbxConstants.CipherTwofish)) return KdbxCipherAlgorithm.Twofish;
        throw new NotSupportedException($"Unsupported cipher UUID: {Convert.ToHexString(uuid)}");
    }

    private static byte[] GetCipherUuid(KdbxCipherAlgorithm cipher) => cipher switch
    {
        KdbxCipherAlgorithm.Aes256 => KdbxConstants.CipherAes256,
        KdbxCipherAlgorithm.ChaCha20 => KdbxConstants.CipherChaCha20,
        KdbxCipherAlgorithm.Twofish => KdbxConstants.CipherTwofish,
        _ => KdbxConstants.CipherAes256
    };

    private static void WriteKdbx4HeaderField(BinaryWriter bw, byte id, byte[] data)
    {
        bw.Write(id);
        bw.Write(data.Length);
        bw.Write(data);
    }

    private static void WriteKdbx3HeaderField(BinaryWriter bw, byte id, byte[] data)
    {
        bw.Write(id);
        bw.Write((ushort)data.Length);
        bw.Write(data);
    }
}
