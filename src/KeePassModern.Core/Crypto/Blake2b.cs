using System.Buffers.Binary;
using System.Numerics;

namespace KeePassModern.Core.Crypto;

/// <summary>
/// Pure-managed RFC 7693 BLAKE2b implementation (zero P/Invoke, NativeAOT safe).
/// </summary>
public sealed class Blake2b
{
    private static readonly ulong[] IV =
    [
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL,
        0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL,
        0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL
    ];

    private static readonly byte[,] Sigma =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 }
    };

    private readonly ulong[] _h = new ulong[8];
    private readonly byte[] _buf = new byte[128];
    private int _bufLen;
    private ulong _t0;
    private ulong _t1;
    private readonly int _outLen;

    public Blake2b(int outLen = 64)
    {
        if (outLen is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(outLen));
        _outLen = outLen;
        Array.Copy(IV, _h, 8);
        _h[0] ^= 0x01010000UL ^ (ulong)outLen;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (_bufLen == 128)
            {
                IncrementCounter(128);
                Compress(false);
                _bufLen = 0;
            }

            int take = Math.Min(128 - _bufLen, data.Length - offset);
            data.Slice(offset, take).CopyTo(_buf.AsSpan(_bufLen));
            _bufLen += take;
            offset += take;
        }
    }

    public void UpdateUInt32(uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        Update(tmp);
    }

    public byte[] FinalizeHash()
    {
        var output = new byte[_outLen];
        FinalizeHash(output);
        return output;
    }

    public void FinalizeHash(Span<byte> output)
    {
        if (output.Length < _outLen)
            throw new ArgumentException("Output span too small.", nameof(output));

        IncrementCounter((ulong)_bufLen);
        Array.Clear(_buf, _bufLen, 128 - _bufLen);
        Compress(true);

        Span<byte> full = stackalloc byte[64];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(full.Slice(i * 8, 8), _h[i]);

        full.Slice(0, _outLen).CopyTo(output);
    }

    private void IncrementCounter(ulong inc)
    {
        _t0 += inc;
        if (_t0 < inc) _t1++;
    }

    private void Compress(bool last)
    {
        Span<ulong> v = stackalloc ulong[16];
        Span<ulong> m = stackalloc ulong[16];

        for (int i = 0; i < 8; i++) v[i] = _h[i];
        for (int i = 0; i < 8; i++) v[i + 8] = IV[i];

        v[12] ^= _t0;
        v[13] ^= _t1;
        if (last) v[14] = ~v[14];

        for (int i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(_buf.AsSpan(i * 8, 8));

        for (int r = 0; r < 12; r++)
        {
            int sr = r % 10;
            G(v, 0, 4, 8, 12, m[Sigma[sr, 0]], m[Sigma[sr, 1]]);
            G(v, 1, 5, 9, 13, m[Sigma[sr, 2]], m[Sigma[sr, 3]]);
            G(v, 2, 6, 10, 14, m[Sigma[sr, 4]], m[Sigma[sr, 5]]);
            G(v, 3, 7, 11, 15, m[Sigma[sr, 6]], m[Sigma[sr, 7]]);
            G(v, 0, 5, 10, 15, m[Sigma[sr, 8]], m[Sigma[sr, 9]]);
            G(v, 1, 6, 11, 12, m[Sigma[sr, 10]], m[Sigma[sr, 11]]);
            G(v, 2, 7, 8, 13, m[Sigma[sr, 12]], m[Sigma[sr, 13]]);
            G(v, 3, 4, 9, 14, m[Sigma[sr, 14]], m[Sigma[sr, 15]]);
        }

        for (int i = 0; i < 8; i++)
            _h[i] ^= v[i] ^ v[i + 8];
    }

    private static void G(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
        v[c] = v[c] + v[d];
        v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
    }

    public static byte[] Hash(ReadOnlySpan<byte> input, int outLen = 64)
    {
        var b = new Blake2b(outLen);
        b.Update(input);
        return b.FinalizeHash();
    }
}
