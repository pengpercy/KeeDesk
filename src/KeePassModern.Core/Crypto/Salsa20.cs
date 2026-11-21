using System.Buffers.Binary;
using System.Numerics;

namespace KeePassModern.Core.Crypto;

/// <summary>
/// Pure-managed Salsa20 stream cipher (used by KDBX 3.1 / 4.x Inner Random Stream).
/// </summary>
public sealed class Salsa20Cipher
{
    private readonly uint[] _state = new uint[16];
    private readonly byte[] _keystream = new byte[64];
    private int _keystreamPos = 64;

    public Salsa20Cipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != 32) throw new ArgumentException("Salsa20 requires a 32-byte key.", nameof(key));
        if (nonce.Length != 8) throw new ArgumentException("Salsa20 requires an 8-byte nonce.", nameof(nonce));

        _state[0] = 0x61707865;
        _state[1] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(0, 4));
        _state[2] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4, 4));
        _state[3] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(8, 4));
        _state[4] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12, 4));
        _state[5] = 0x3320646e;
        _state[6] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));
        _state[7] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
        _state[8] = 0;
        _state[9] = 0;
        _state[10] = 0x79622d32;
        _state[11] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16, 4));
        _state[12] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20, 4));
        _state[13] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24, 4));
        _state[14] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28, 4));
        _state[15] = 0x6b206574;
    }

    public void ProcessBytes(ReadOnlySpan<byte> input, Span<byte> output)
    {
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
            x[4] ^= BitOperations.RotateLeft(x[0] + x[12], 7);
            x[8] ^= BitOperations.RotateLeft(x[4] + x[0], 9);
            x[12] ^= BitOperations.RotateLeft(x[8] + x[4], 13);
            x[0] ^= BitOperations.RotateLeft(x[12] + x[8], 18);

            x[9] ^= BitOperations.RotateLeft(x[5] + x[1], 7);
            x[13] ^= BitOperations.RotateLeft(x[9] + x[5], 9);
            x[1] ^= BitOperations.RotateLeft(x[13] + x[9], 13);
            x[5] ^= BitOperations.RotateLeft(x[1] + x[13], 18);

            x[14] ^= BitOperations.RotateLeft(x[10] + x[6], 7);
            x[2] ^= BitOperations.RotateLeft(x[14] + x[10], 9);
            x[6] ^= BitOperations.RotateLeft(x[2] + x[14], 13);
            x[10] ^= BitOperations.RotateLeft(x[6] + x[2], 18);

            x[3] ^= BitOperations.RotateLeft(x[15] + x[11], 7);
            x[7] ^= BitOperations.RotateLeft(x[3] + x[15], 9);
            x[11] ^= BitOperations.RotateLeft(x[7] + x[3], 13);
            x[15] ^= BitOperations.RotateLeft(x[11] + x[7], 18);

            x[1] ^= BitOperations.RotateLeft(x[0] + x[3], 7);
            x[2] ^= BitOperations.RotateLeft(x[1] + x[0], 9);
            x[3] ^= BitOperations.RotateLeft(x[2] + x[1], 13);
            x[0] ^= BitOperations.RotateLeft(x[3] + x[2], 18);

            x[6] ^= BitOperations.RotateLeft(x[5] + x[4], 7);
            x[7] ^= BitOperations.RotateLeft(x[6] + x[5], 9);
            x[4] ^= BitOperations.RotateLeft(x[7] + x[6], 13);
            x[5] ^= BitOperations.RotateLeft(x[4] + x[7], 18);

            x[11] ^= BitOperations.RotateLeft(x[10] + x[9], 7);
            x[8] ^= BitOperations.RotateLeft(x[11] + x[10], 9);
            x[9] ^= BitOperations.RotateLeft(x[8] + x[11], 13);
            x[10] ^= BitOperations.RotateLeft(x[9] + x[8], 18);

            x[12] ^= BitOperations.RotateLeft(x[15] + x[14], 7);
            x[13] ^= BitOperations.RotateLeft(x[12] + x[15], 9);
            x[14] ^= BitOperations.RotateLeft(x[13] + x[12], 13);
            x[15] ^= BitOperations.RotateLeft(x[14] + x[13], 18);
        }

        for (int i = 0; i < 16; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(_keystream.AsSpan(i * 4, 4), x[i] + _state[i]);

        _state[8]++;
        if (_state[8] == 0) _state[9]++;
    }
}
