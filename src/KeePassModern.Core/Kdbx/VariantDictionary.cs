using System.Buffers.Binary;
using System.Text;

namespace KeePassModern.Core.Kdbx;

/// <summary>
/// KDBX 4 VariantDictionary serializer/deserializer for KDF parameters.
/// </summary>
public sealed class VariantDictionary
{
    private const ushort Version = 0x0100;

    private const byte TypeUInt32 = 0x04;
    private const byte TypeUInt64 = 0x05;
    private const byte TypeBool = 0x08;
    private const byte TypeInt32 = 0x0C;
    private const byte TypeInt64 = 0x0D;
    private const byte TypeString = 0x18;
    private const byte TypeByteArray = 0x42;

    private readonly Dictionary<string, (byte Type, object Value)> _items = new(StringComparer.Ordinal);

    public void SetUInt32(string key, uint value) => _items[key] = (TypeUInt32, value);
    public void SetUInt64(string key, ulong value) => _items[key] = (TypeUInt64, value);
    public void SetBool(string key, bool value) => _items[key] = (TypeBool, value);
    public void SetInt32(string key, int value) => _items[key] = (TypeInt32, value);
    public void SetInt64(string key, long value) => _items[key] = (TypeInt64, value);
    public void SetString(string key, string value) => _items[key] = (TypeString, value);
    public void SetByteArray(string key, byte[] value) => _items[key] = (TypeByteArray, value);

    public uint GetUInt32(string key, uint defaultValue = 0)
        => _items.TryGetValue(key, out var item) ? Convert.ToUInt32(item.Value) : defaultValue;

    public ulong GetUInt64(string key, ulong defaultValue = 0)
        => _items.TryGetValue(key, out var item) ? Convert.ToUInt64(item.Value) : defaultValue;

    public byte[]? GetByteArray(string key)
        => _items.TryGetValue(key, out var item) && item.Value is byte[] b ? b : null;

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write(Version);
        foreach (var (key, (type, value)) in _items)
        {
            bw.Write(type);
            var keyBytes = Encoding.UTF8.GetBytes(key);
            bw.Write(keyBytes.Length);
            bw.Write(keyBytes);

            byte[] valBytes = type switch
            {
                TypeUInt32 => BitConverter.GetBytes((uint)value),
                TypeUInt64 => BitConverter.GetBytes((ulong)value),
                TypeBool => [(bool)value ? (byte)1 : (byte)0],
                TypeInt32 => BitConverter.GetBytes((int)value),
                TypeInt64 => BitConverter.GetBytes((long)value),
                TypeString => Encoding.UTF8.GetBytes((string)value),
                TypeByteArray => (byte[])value,
                _ => throw new InvalidOperationException($"Unsupported VariantDictionary type: {type}")
            };

            bw.Write(valBytes.Length);
            bw.Write(valBytes);
        }
        bw.Write((byte)0); // Terminator
        return ms.ToArray();
    }

    public static VariantDictionary Deserialize(ReadOnlySpan<byte> data)
    {
        var dict = new VariantDictionary();
        int offset = 0;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;
        if ((version & 0xFF00) > (Version & 0xFF00))
            throw new InvalidDataException($"Unsupported VariantDictionary version: 0x{version:X4}");

        while (offset < data.Length)
        {
            byte type = data[offset++];
            if (type == 0) break;

            int keyLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;
            string key = Encoding.UTF8.GetString(data.Slice(offset, keyLen));
            offset += keyLen;

            int valLen = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            offset += 4;
            var valSpan = data.Slice(offset, valLen);
            offset += valLen;

            switch (type)
            {
                case TypeUInt32:
                    dict.SetUInt32(key, BinaryPrimitives.ReadUInt32LittleEndian(valSpan));
                    break;
                case TypeUInt64:
                    dict.SetUInt64(key, BinaryPrimitives.ReadUInt64LittleEndian(valSpan));
                    break;
                case TypeBool:
                    dict.SetBool(key, valSpan[0] != 0);
                    break;
                case TypeInt32:
                    dict.SetInt32(key, BinaryPrimitives.ReadInt32LittleEndian(valSpan));
                    break;
                case TypeInt64:
                    dict.SetInt64(key, BinaryPrimitives.ReadInt64LittleEndian(valSpan));
                    break;
                case TypeString:
                    dict.SetString(key, Encoding.UTF8.GetString(valSpan));
                    break;
                case TypeByteArray:
                    dict.SetByteArray(key, valSpan.ToArray());
                    break;
            }
        }
        return dict;
    }
}
