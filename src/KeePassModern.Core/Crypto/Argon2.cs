using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace KeePassModern.Core.Crypto;

public enum Argon2Type : uint
{
    Argon2d = 0,
    Argon2i = 1,
    Argon2id = 2
}

/// <summary>
/// 纯托管 RFC 9106 Argon2d / Argon2id 密钥派生实现（零 P/Invoke，完全兼容 KDBX 4.0/4.1 与 NativeAOT）。
/// </summary>
public static class Argon2
{
    private const int SyncPoints = 4;
    private const int QWordsInBlock = 128;

    public static byte[] DeriveKey(
        ReadOnlySpan<byte> password,
        ReadOnlySpan<byte> salt,
        Argon2Type type,
        uint version,
        uint iterations,
        ulong memoryBytes,
        uint parallelism,
        int outputLength = 32,
        ReadOnlySpan<byte> secretKey = default,
        ReadOnlySpan<byte> associatedData = default)
    {
        uint memoryKiB = (uint)Math.Max(8 * parallelism, memoryBytes / 1024);
        uint mPrime = memoryKiB - (memoryKiB % (4 * parallelism));
        uint laneLength = mPrime / parallelism;
        uint segmentLength = laneLength / SyncPoints;

        // 1. H0
        var h0Hasher = new Blake2b(64);
        h0Hasher.UpdateUInt32(parallelism);
        h0Hasher.UpdateUInt32((uint)outputLength);
        h0Hasher.UpdateUInt32(memoryKiB);
        h0Hasher.UpdateUInt32(iterations);
        h0Hasher.UpdateUInt32(version);
        h0Hasher.UpdateUInt32((uint)type);
        h0Hasher.UpdateUInt32((uint)password.Length);
        h0Hasher.Update(password);
        h0Hasher.UpdateUInt32((uint)salt.Length);
        h0Hasher.Update(salt);
        h0Hasher.UpdateUInt32((uint)secretKey.Length);
        if (!secretKey.IsEmpty) h0Hasher.Update(secretKey);
        h0Hasher.UpdateUInt32((uint)associatedData.Length);
        if (!associatedData.IsEmpty) h0Hasher.Update(associatedData);
        byte[] h0 = h0Hasher.FinalizeHash();

        // 2. Allocate memory blocks: mPrime blocks of 128 ulongs (1024 bytes each)
        var memory = new ulong[mPrime][];
        for (int i = 0; i < mPrime; i++)
            memory[i] = new ulong[QWordsInBlock];

        try
        {
            byte[] blockBytes = new byte[1024];
            byte[] h0WithIdx = new byte[72];
            Buffer.BlockCopy(h0, 0, h0WithIdx, 0, 64);

            for (uint lane = 0; lane < parallelism; lane++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(h0WithIdx.AsSpan(68, 4), lane);

                BinaryPrimitives.WriteUInt32LittleEndian(h0WithIdx.AsSpan(64, 4), 0);
                Blake2bLong(h0WithIdx, blockBytes);
                BytesToBlock(blockBytes, memory[lane * laneLength]);

                BinaryPrimitives.WriteUInt32LittleEndian(h0WithIdx.AsSpan(64, 4), 1);
                Blake2bLong(h0WithIdx, blockBytes);
                BytesToBlock(blockBytes, memory[lane * laneLength + 1]);
            }

            // 3. Fill segments
            for (uint pass = 0; pass < iterations; pass++)
            {
                for (uint slice = 0; slice < SyncPoints; slice++)
                {
                    if (parallelism == 1)
                    {
                        FillSegment(memory, type, version, iterations, mPrime, laneLength, segmentLength, parallelism, pass, 0, slice);
                    }
                    else
                    {
                        Parallel.For(0, (int)parallelism, lane =>
                        {
                            FillSegment(memory, type, version, iterations, mPrime, laneLength, segmentLength, parallelism, pass, (uint)lane, slice);
                        });
                    }
                }
            }

            // 4. Finalize: XOR last block of each lane
            ulong[] finalBlock = new ulong[QWordsInBlock];
            Array.Copy(memory[laneLength - 1], finalBlock, QWordsInBlock);
            for (uint lane = 1; lane < parallelism; lane++)
            {
                var last = memory[lane * laneLength + laneLength - 1];
                for (int i = 0; i < QWordsInBlock; i++)
                    finalBlock[i] ^= last[i];
            }

            BlockToBytes(finalBlock, blockBytes);
            byte[] result = new byte[outputLength];
            Blake2bLong(blockBytes, result);
            CryptographicOperations.ZeroMemory(blockBytes);
            return result;
        }
        finally
        {
            for (int i = 0; i < mPrime; i++)
                Array.Clear(memory[i]);
            CryptographicOperations.ZeroMemory(h0);
        }
    }

    private static void FillSegment(
        ulong[][] memory,
        Argon2Type type,
        uint version,
        uint iterations,
        uint mPrime,
        uint laneLength,
        uint segmentLength,
        uint parallelism,
        uint pass,
        uint lane,
        uint slice)
    {
        bool dataIndependent = type == Argon2Type.Argon2i ||
                               (type == Argon2Type.Argon2id && pass == 0 && slice < SyncPoints / 2);

        ulong[]? addressBlock = null;
        ulong[]? inputBlock = null;
        ulong[]? zeroBlock = null;
        ulong[] tmpBlock = new ulong[QWordsInBlock];
        ulong[] rBlock = new ulong[QWordsInBlock];

        if (dataIndependent)
        {
            addressBlock = new ulong[QWordsInBlock];
            inputBlock = new ulong[QWordsInBlock];
            zeroBlock = new ulong[QWordsInBlock];
            inputBlock[0] = pass;
            inputBlock[1] = lane;
            inputBlock[2] = slice;
            inputBlock[3] = mPrime;
            inputBlock[4] = iterations;
            inputBlock[5] = (ulong)type;
        }

        uint startingIndex = (pass == 0 && slice == 0) ? 2u : 0u;
        uint currOffset = lane * laneLength + slice * segmentLength + startingIndex;
        uint prevOffset = (currOffset % laneLength == 0) ? (currOffset + laneLength - 1) : (currOffset - 1);

        if (dataIndependent && pass == 0 && slice == 0)
        {
            NextAddresses(zeroBlock!, inputBlock!, addressBlock!, rBlock, tmpBlock);
        }

        for (uint i = startingIndex; i < segmentLength; i++, currOffset++, prevOffset++)
        {
            if (currOffset % laneLength == 1)
                prevOffset = currOffset - 1;

            ulong pseudoRand;
            if (dataIndependent)
            {
                if (i % QWordsInBlock == 0)
                    NextAddresses(zeroBlock!, inputBlock!, addressBlock!, rBlock, tmpBlock);
                pseudoRand = addressBlock![i % QWordsInBlock];
            }
            else
            {
                pseudoRand = memory[prevOffset][0];
            }

            uint refLane = (uint)((pseudoRand >> 32) % parallelism);
            if (pass == 0 && slice == 0)
                refLane = lane;

            uint refIndex = IndexAlpha(pass, slice, segmentLength, laneLength, i, (uint)pseudoRand, refLane == lane);
            uint refOffset = refLane * laneLength + refIndex;

            bool withXor = pass != 0 && version != 0x10;
            FillBlock(memory[prevOffset], memory[refOffset], memory[currOffset], rBlock, tmpBlock, withXor);
        }
    }

    private static void NextAddresses(ulong[] zeroBlock, ulong[] inputBlock, ulong[] addressBlock, ulong[] rBlock, ulong[] tmpBlock)
    {
        inputBlock[6]++;
        FillBlock(zeroBlock, inputBlock, addressBlock, rBlock, tmpBlock, false);
        FillBlock(zeroBlock, addressBlock, addressBlock, rBlock, tmpBlock, false);
    }

    private static uint IndexAlpha(uint pass, uint slice, uint segmentLength, uint laneLength, uint index, uint pseudoRand, bool sameLane)
    {
        uint referenceAreaSize;
        if (pass == 0)
        {
            if (slice == 0)
                referenceAreaSize = index - 1;
            else if (sameLane)
                referenceAreaSize = slice * segmentLength + index - 1;
            else
                referenceAreaSize = slice * segmentLength + (index == 0 ? unchecked((uint)-1) : 0);
        }
        else
        {
            if (sameLane)
                referenceAreaSize = laneLength - segmentLength + index - 1;
            else
                referenceAreaSize = laneLength - segmentLength + (index == 0 ? unchecked((uint)-1) : 0);
        }

        ulong relativePosition = pseudoRand;
        relativePosition = (relativePosition * relativePosition) >> 32;
        relativePosition = referenceAreaSize - 1 - ((referenceAreaSize * relativePosition) >> 32);

        uint startPosition = 0;
        if (pass != 0)
            startPosition = (slice == SyncPoints - 1) ? 0 : (slice + 1) * segmentLength;

        return (uint)((startPosition + relativePosition) % laneLength);
    }

    private static void FillBlock(ulong[] prev, ulong[] refBlock, ulong[] next, ulong[] r, ulong[] z, bool withXor)
    {
        for (int i = 0; i < QWordsInBlock; i++)
        {
            ulong x = prev[i] ^ refBlock[i];
            r[i] = x;
            z[i] = x;
        }

        // Row-wise Blake2b round
        for (int i = 0; i < 8; i++)
        {
            int b = i * 16;
            Round(z, b + 0, b + 1, b + 2, b + 3, b + 4, b + 5, b + 6, b + 7,
                     b + 8, b + 9, b + 10, b + 11, b + 12, b + 13, b + 14, b + 15);
        }

        // Column-wise Blake2b round
        for (int i = 0; i < 8; i++)
        {
            int b = i * 2;
            Round(z, b + 0, b + 1, b + 16, b + 17, b + 32, b + 33, b + 48, b + 49,
                     b + 64, b + 65, b + 80, b + 81, b + 96, b + 97, b + 112, b + 113);
        }

        if (withXor)
        {
            for (int i = 0; i < QWordsInBlock; i++)
                next[i] ^= r[i] ^ z[i];
        }
        else
        {
            for (int i = 0; i < QWordsInBlock; i++)
                next[i] = r[i] ^ z[i];
        }
    }

    private static void Round(
        ulong[] v,
        int v0, int v1, int v2, int v3,
        int v4, int v5, int v6, int v7,
        int v8, int v9, int v10, int v11,
        int v12, int v13, int v14, int v15)
    {
        G(v, v0, v4, v8, v12);
        G(v, v1, v5, v9, v13);
        G(v, v2, v6, v10, v14);
        G(v, v3, v7, v11, v15);
        G(v, v0, v5, v10, v15);
        G(v, v1, v6, v11, v12);
        G(v, v2, v7, v8, v13);
        G(v, v3, v4, v9, v14);
    }

    private static void G(ulong[] v, int a, int b, int c, int d)
    {
        v[a] = v[a] + v[b] + 2UL * (v[a] & 0xFFFFFFFFUL) * (v[b] & 0xFFFFFFFFUL);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d] + 2UL * (v[c] & 0xFFFFFFFFUL) * (v[d] & 0xFFFFFFFFUL);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + 2UL * (v[a] & 0xFFFFFFFFUL) * (v[b] & 0xFFFFFFFFUL);
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d] + 2UL * (v[c] & 0xFFFFFFFFUL) * (v[d] & 0xFFFFFFFFUL);
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    private static void Blake2bLong(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outLen = output.Length;
        Span<byte> outLenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(outLenBytes, (uint)outLen);

        if (outLen <= 64)
        {
            var h = new Blake2b(outLen);
            h.Update(outLenBytes);
            h.Update(input);
            h.FinalizeHash(output);
            return;
        }

        byte[] v = new byte[64];
        var h0 = new Blake2b(64);
        h0.Update(outLenBytes);
        h0.Update(input);
        h0.FinalizeHash(v);

        v.AsSpan(0, 32).CopyTo(output.Slice(0, 32));
        int pos = 32;
        int remaining = outLen - 32;

        while (remaining > 64)
        {
            var hi = new Blake2b(64);
            hi.Update(v);
            hi.FinalizeHash(v);
            v.AsSpan(0, 32).CopyTo(output.Slice(pos, 32));
            pos += 32;
            remaining -= 32;
        }

        var hLast = new Blake2b(remaining);
        hLast.Update(v);
        hLast.FinalizeHash(output.Slice(pos, remaining));
    }

    private static void BytesToBlock(ReadOnlySpan<byte> bytes, ulong[] block)
    {
        for (int i = 0; i < QWordsInBlock; i++)
            block[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i * 8, 8));
    }

    private static void BlockToBytes(ulong[] block, Span<byte> bytes)
    {
        for (int i = 0; i < QWordsInBlock; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(i * 8, 8), block[i]);
    }
}
