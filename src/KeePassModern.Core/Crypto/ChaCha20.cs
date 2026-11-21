using System.Buffers.Binary;
using System.Numerics;

namespace KeePassModern.Core.Crypto;

/// <summary>
/// Pure-managed RFC 8439 ChaCha20 stream cipher (used by KDBX 4 outer cipher and inner stream).
/// </summary>
public sealed class ChaCha20Cipher
{
    private readonly uint[] _state = new uint[16];
    private readonly byte[] _keystream = new byte[64];
    private int _keystreamPos = 64;

    public ChaCha20Cipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter = 0)
    {
        if (key.Length != 32) throw new ArgumentException("ChaCha20 requires a 32-byte key.", nameof(key));
        if (nonce.Length != 12) throw new ArgumentException("ChaCha20 requires a 12-byte nonce.", nameof(nonce));

        _state[0] = 0x61707865;
        _state[1] = 0x3320646e;
        _state[2] = 0x79622d32;
        _state[3] = 0x6b206574;

        for (int i = 0; i < 8; i++)
            _state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));

        _state[12] = counter;
        _state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));
        _state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
        _state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));
    }

    public void ProcessBytes(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("Output buffer too small.", nameof(output));

        for (int i = 0; i < input.Length; i++)
        {
            if (_keystreamPos == 64)
            {
                GenerateBlock();
                _keystreamPos = 0;
            }
            output[i] = (byte)(input[i] ^ _keystream[_keystreamPos++]);
        }
    }

    public byte[] ProcessBytes(ReadOnlySpan<byte> input)
    {
        var output = new byte[input.Length];
        ProcessBytes(input, output);
        return output;
    }

    private void GenerateBlock()
    {
        Span<uint> x = stackalloc uint[16];
        _state.AsSpan().CopyTo(x);

        for (int i = 0; i < 10; i++)
        {
            QuarterRound(x, 0, 4, 8, 12);
            QuarterRound(x, 1, 5, 9, 13);
            QuarterRound(x, 2, 6, 10, 14);
            QuarterRound(x, 3, 7, 11, 15);
            QuarterRound(x, 0, 5, 10, 15);
            QuarterRound(x, 1, 6, 11, 12);
            QuarterRound(x, 2, 7, 8, 13);
            QuarterRound(x, 3, 4, 9, 14);
        }

        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(_keystream.AsSpan(i * 4, 4), x[i] + _state[i]);

        _state[12]++;
    }

    private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
}
