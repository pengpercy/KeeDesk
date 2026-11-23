using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace KeeDesk.Core.Crypto;

/// <summary>
/// Pure-managed Twofish-256 CBC with PKCS#7 padding for KDBX compatibility.
/// </summary>
public sealed class TwofishCipher
{
    private static readonly byte[,] Q0 =
    {
        { 0x8, 0x1, 0x7, 0xD, 0x6, 0xF, 0x3, 0x2, 0x0, 0xB, 0x5, 0x9, 0xE, 0xC, 0xA, 0x4 },
        { 0xE, 0xC, 0xB, 0x8, 0x1, 0x2, 0x3, 0x5, 0xF, 0x4, 0xA, 0x6, 0x7, 0x0, 0x9, 0xD },
        { 0xB, 0xA, 0x5, 0xE, 0x6, 0xD, 0x9, 0x0, 0xC, 0x8, 0xF, 0x3, 0x2, 0x4, 0x7, 0x1 },
        { 0xD, 0x7, 0xF, 0x4, 0x1, 0x2, 0x6, 0xE, 0x9, 0xB, 0x3, 0x0, 0x8, 0x5, 0xC, 0xA }
    };

    private static readonly byte[,] Q1 =
    {
        { 0x2, 0x8, 0xB, 0xD, 0xF, 0x7, 0x6, 0xE, 0x3, 0x1, 0x9, 0x4, 0x0, 0xA, 0xC, 0x5 },
        { 0x1, 0xE, 0x2, 0xB, 0x4, 0xC, 0x3, 0x7, 0x6, 0xD, 0xA, 0x5, 0xF, 0x9, 0x0, 0x8 },
        { 0x4, 0xC, 0x7, 0x5, 0x1, 0x6, 0x9, 0xA, 0x0, 0xE, 0xD, 0x8, 0x2, 0xB, 0x3, 0xF },
        { 0xB, 0x9, 0x5, 0x1, 0xC, 0x3, 0xD, 0xE, 0x6, 0x4, 0x7, 0xF, 0x2, 0x0, 0x8, 0xA }
    };

    private static readonly byte[] Q0Table = BuildQTable(Q0);
    private static readonly byte[] Q1Table = BuildQTable(Q1);
    private static readonly uint[,] Mds = BuildMds();

    private readonly uint[] _subKeys = new uint[40];
    private readonly uint[,] _sBox = new uint[4, 256];

    public TwofishCipher(ReadOnlySpan<byte> key) : this(key, standardKeyedBoxes: false) { }

    internal TwofishCipher(ReadOnlySpan<byte> key, bool standardKeyedBoxes)
    {
        if (key.Length != 32)
            throw new ArgumentException("Twofish requires a 32-byte key.", nameof(key));

        uint[] me = new uint[4];
        uint[] mo = new uint[4];
        uint[] s = new uint[4];

        for (int i = 0; i < 4; i++)
        {
            me[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 8, 4));
            mo[i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 8 + 4, 4));
            s[3 - i] = RsMdsEncode(me[i], mo[i]);
        }

        const uint rho = 0x01010101u;
        for (uint i = 0; i < 20; i++)
        {
            uint a = H(2 * i * rho, me);
            uint b = BitOperations.RotateLeft(H((2 * i + 1) * rho, mo), 8);
            _subKeys[2 * i] = a + b;
            _subKeys[2 * i + 1] = BitOperations.RotateLeft(a + 2 * b, 9);
        }

        for (int x = 0; x < 256; x++)
        {
            byte b0 = (byte)x;
            // KeePass 1.x files use the published Twofish key schedule. KeeDesk's prior
            // KDBX implementation used different keyed boxes and still needs its old reader.
            if (standardKeyedBoxes)
            {
                _sBox[0, x] = HColumn(0, b0, s);
                _sBox[1, x] = HColumn(1, b0, s);
                _sBox[2, x] = HColumn(2, b0, s);
                _sBox[3, x] = HColumn(3, b0, s);
                continue;
            }
            byte y0 = Q0Table[Q0Table[Q1Table[b0] ^ B0(s[3])] ^ B0(s[2])];
            y0 = Q1Table[Q0Table[y0 ^ B0(s[1])] ^ B0(s[0])];
            _sBox[0, x] = Mds[0, y0];

            byte y1 = Q0Table[Q1Table[Q0Table[b0] ^ B1(s[3])] ^ B1(s[2])];
            y1 = Q0Table[Q1Table[y1 ^ B1(s[1])] ^ B1(s[0])];
            _sBox[1, x] = Mds[1, y1];

            byte y2 = Q1Table[Q0Table[Q0Table[b0] ^ B2(s[3])] ^ B2(s[2])];
            y2 = Q1Table[Q1Table[y2 ^ B2(s[1])] ^ B2(s[0])];
            _sBox[2, x] = Mds[2, y2];

            byte y3 = Q1Table[Q1Table[Q1Table[b0] ^ B3(s[3])] ^ B3(s[2])];
            y3 = Q0Table[Q0Table[y3 ^ B3(s[1])] ^ B3(s[0])];
            _sBox[3, x] = Mds[3, y3];
        }
    }

    public byte[] EncryptCbc(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv)
    {
        if (iv.Length != 16) throw new ArgumentException("IV must be 16 bytes.", nameof(iv));
        int padLen = 16 - (plaintext.Length % 16);
        byte[] output = new byte[plaintext.Length + padLen];
        Span<byte> prev = stackalloc byte[16];
        iv.CopyTo(prev);
        Span<byte> block = stackalloc byte[16];

        int totalBlocks = output.Length / 16;
        for (int b = 0; b < totalBlocks; b++)
        {
            int offset = b * 16;
            for (int i = 0; i < 16; i++)
            {
                byte p = (offset + i < plaintext.Length) ? plaintext[offset + i] : (byte)padLen;
                block[i] = (byte)(p ^ prev[i]);
            }
            EncryptBlock(block, output.AsSpan(offset, 16));
            output.AsSpan(offset, 16).CopyTo(prev);
        }
        return output;
    }

    public byte[] DecryptCbc(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv)
    {
        if (iv.Length != 16) throw new ArgumentException("IV must be 16 bytes.", nameof(iv));
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
            throw new CryptographicException("Invalid Twofish ciphertext length.");

        byte[] padded = new byte[ciphertext.Length];
        Span<byte> prev = stackalloc byte[16];
        iv.CopyTo(prev);
        Span<byte> block = stackalloc byte[16];

        for (int offset = 0; offset < ciphertext.Length; offset += 16)
        {
            var cur = ciphertext.Slice(offset, 16);
            DecryptBlock(cur, block);
            for (int i = 0; i < 16; i++)
                padded[offset + i] = (byte)(block[i] ^ prev[i]);
            cur.CopyTo(prev);
        }

        int padLen = padded[^1];
        if (padLen is < 1 or > 16)
            throw new CryptographicException("Invalid PKCS#7 padding.");
        for (int i = 1; i <= padLen; i++)
        {
            if (padded[^i] != padLen)
                throw new CryptographicException("Invalid PKCS#7 padding.");
        }

        return padded[..^padLen];
    }

    private void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint r0 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(0, 4)) ^ _subKeys[0];
        uint r1 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(4, 4)) ^ _subKeys[1];
        uint r2 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(8, 4)) ^ _subKeys[2];
        uint r3 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(12, 4)) ^ _subKeys[3];

        for (int round = 0; round < 16; round += 2)
        {
            uint t0 = G(r0);
            uint t1 = G(BitOperations.RotateLeft(r1, 8));
            r2 = BitOperations.RotateRight(r2 ^ (t0 + t1 + _subKeys[8 + 2 * round]), 1);
            r3 = BitOperations.RotateLeft(r3, 1) ^ (t0 + 2 * t1 + _subKeys[9 + 2 * round]);

            t0 = G(r2);
            t1 = G(BitOperations.RotateLeft(r3, 8));
            r0 = BitOperations.RotateRight(r0 ^ (t0 + t1 + _subKeys[10 + 2 * round]), 1);
            r1 = BitOperations.RotateLeft(r1, 1) ^ (t0 + 2 * t1 + _subKeys[11 + 2 * round]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(0, 4), r2 ^ _subKeys[4]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(4, 4), r3 ^ _subKeys[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(8, 4), r0 ^ _subKeys[6]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(12, 4), r1 ^ _subKeys[7]);
    }

    private void DecryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        uint r2 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(0, 4)) ^ _subKeys[4];
        uint r3 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(4, 4)) ^ _subKeys[5];
        uint r0 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(8, 4)) ^ _subKeys[6];
        uint r1 = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(12, 4)) ^ _subKeys[7];

        for (int round = 14; round >= 0; round -= 2)
        {
            uint t0 = G(r2);
            uint t1 = G(BitOperations.RotateLeft(r3, 8));
            r0 = BitOperations.RotateLeft(r0, 1) ^ (t0 + t1 + _subKeys[10 + 2 * round]);
            r1 = BitOperations.RotateRight(r1 ^ (t0 + 2 * t1 + _subKeys[11 + 2 * round]), 1);

            t0 = G(r0);
            t1 = G(BitOperations.RotateLeft(r1, 8));
            r2 = BitOperations.RotateLeft(r2, 1) ^ (t0 + t1 + _subKeys[8 + 2 * round]);
            r3 = BitOperations.RotateRight(r3 ^ (t0 + 2 * t1 + _subKeys[9 + 2 * round]), 1);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(0, 4), r0 ^ _subKeys[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(4, 4), r1 ^ _subKeys[1]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(8, 4), r2 ^ _subKeys[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(12, 4), r3 ^ _subKeys[3]);
    }

    private uint G(uint x) => _sBox[0, B0(x)] ^ _sBox[1, B1(x)] ^ _sBox[2, B2(x)] ^ _sBox[3, B3(x)];

    private static uint HColumn(int column, byte x, uint[] words)
    {
        int shift = column * 8;
        byte key3 = (byte)(words[3] >> shift);
        byte key2 = (byte)(words[2] >> shift);
        byte key1 = (byte)(words[1] >> shift);
        byte key0 = (byte)(words[0] >> shift);
        int value = (column is 0 or 3 ? Q1Table : Q0Table)[x] ^ key3;
        value = (column < 2 ? Q1Table : Q0Table)[value] ^ key2;
        value = (column % 2 == 0 ? Q0Table : Q1Table)[value] ^ key1;
        value = (column < 2 ? Q0Table : Q1Table)[value] ^ key0;
        value = (column % 2 == 0 ? Q1Table : Q0Table)[value];
        return Mds[column, value];
    }

    private static uint H(uint x, uint[] l)
    {
        byte y0 = B0(x), y1 = B1(x), y2 = B2(x), y3 = B3(x);
        y0 = (byte)(Q1Table[y0] ^ B0(l[3]));
        y1 = (byte)(Q0Table[y1] ^ B1(l[3]));
        y2 = (byte)(Q0Table[y2] ^ B2(l[3]));
        y3 = (byte)(Q1Table[y3] ^ B3(l[3]));

        y0 = (byte)(Q1Table[y0] ^ B0(l[2]));
        y1 = (byte)(Q1Table[y1] ^ B1(l[2]));
        y2 = (byte)(Q0Table[y2] ^ B2(l[2]));
        y3 = (byte)(Q0Table[y3] ^ B3(l[2]));

        y0 = Q1Table[Q0Table[Q0Table[y0] ^ B0(l[1])] ^ B0(l[0])];
        y1 = Q0Table[Q0Table[Q1Table[y1] ^ B1(l[1])] ^ B1(l[0])];
        y2 = Q1Table[Q1Table[Q0Table[y2] ^ B2(l[1])] ^ B2(l[0])];
        y3 = Q0Table[Q1Table[Q1Table[y3] ^ B3(l[1])] ^ B3(l[0])];

        return Mds[0, y0] ^ Mds[1, y1] ^ Mds[2, y2] ^ Mds[3, y3];
    }

    private static byte B0(uint x) => (byte)(x & 0xFF);
    private static byte B1(uint x) => (byte)((x >> 8) & 0xFF);
    private static byte B2(uint x) => (byte)((x >> 16) & 0xFF);
    private static byte B3(uint x) => (byte)((x >> 24) & 0xFF);

    private static byte[] BuildQTable(byte[,] t)
    {
        var table = new byte[256];
        for (int x = 0; x < 256; x++)
        {
            int a0 = x >> 4;
            int b0 = x & 0xF;
            int a1 = a0 ^ b0;
            int b1 = a0 ^ ((b0 >> 1) | ((b0 & 1) << 3)) ^ ((8 * a0) & 0xF);
            int a2 = t[0, a1];
            int b2 = t[1, b1];
            int a3 = a2 ^ b2;
            int b3 = a2 ^ ((b2 >> 1) | ((b2 & 1) << 3)) ^ ((8 * a2) & 0xF);
            int a4 = t[2, a3];
            int b4 = t[3, b3];
            table[x] = (byte)((b4 << 4) | a4);
        }
        return table;
    }

    private static uint[,] BuildMds()
    {
        byte[,] m =
        {
            { 0x01, 0xEF, 0x5B, 0x5B },
            { 0x5B, 0xEF, 0xEF, 0x01 },
            { 0xEF, 0x5B, 0x01, 0xEF },
            { 0xEF, 0x01, 0xEF, 0x5B }
        };
        var res = new uint[4, 256];
        for (int col = 0; col < 4; col++)
        {
            for (int x = 0; x < 256; x++)
            {
                uint v0 = GfMul(m[0, col], (byte)x, 0x169);
                uint v1 = GfMul(m[1, col], (byte)x, 0x169);
                uint v2 = GfMul(m[2, col], (byte)x, 0x169);
                uint v3 = GfMul(m[3, col], (byte)x, 0x169);
                res[col, x] = v0 | (v1 << 8) | (v2 << 16) | (v3 << 24);
            }
        }
        return res;
    }

    private static uint RsMdsEncode(uint k0, uint k1)
    {
        uint r = k1;
        for (int i = 0; i < 4; i++) r = RsRem(r);
        r ^= k0;
        for (int i = 0; i < 4; i++) r = RsRem(r);
        return r;
    }

    private static uint RsRem(uint x)
    {
        uint b = (x >> 24) & 0xFF;
        uint g2 = ((b << 1) ^ ((b & 0x80) != 0 ? 0x14Du : 0u)) & 0xFF;
        uint g3 = ((b >> 1) ^ ((b & 1) != 0 ? (0x14Du >> 1) : 0u) ^ g2);
        return (x << 8) ^ (g3 << 24) ^ (g2 << 16) ^ (g3 << 8) ^ b;
    }

    private static byte GfMul(byte a, byte b, int poly)
    {
        int r = 0;
        int aa = a, bb = b;
        for (int i = 0; i < 8; i++)
        {
            if ((bb & 1) != 0) r ^= aa;
            bool hi = (aa & 0x80) != 0;
            aa <<= 1;
            if (hi) aa ^= poly;
            bb >>= 1;
        }
        return (byte)r;
    }
}
