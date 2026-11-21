using System.Security.Cryptography;
using System.Text;

namespace KeePassModern.Core.Crypto;

/// <summary>
/// 内存保护字符串：使用每实例随机掩码对内存中的敏感数据进行异或混淆，
/// 并在释放或锁定时通过 CryptographicOperations.ZeroMemory 确定性清零。
/// </summary>
public sealed class ProtectedString : IDisposable
{
    public static readonly ProtectedString Empty = new(false, string.Empty);

    private byte[]? _data;
    private byte[]? _pad;
    private bool _disposed;

    public bool IsProtected { get; }

    public ProtectedString(bool isProtected, string? value)
    {
        IsProtected = isProtected;
        var bytes = string.IsNullOrEmpty(value) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
        if (isProtected && bytes.Length > 0)
        {
            _pad = RandomNumberGenerator.GetBytes(bytes.Length);
            _data = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                _data[i] = (byte)(bytes[i] ^ _pad[i]);
            CryptographicOperations.ZeroMemory(bytes);
        }
        else
        {
            _data = bytes;
        }
    }

    public ProtectedString(bool isProtected, ReadOnlySpan<byte> utf8Bytes)
    {
        IsProtected = isProtected;
        if (utf8Bytes.IsEmpty)
        {
            _data = Array.Empty<byte>();
            return;
        }

        if (isProtected)
        {
            _pad = RandomNumberGenerator.GetBytes(utf8Bytes.Length);
            _data = new byte[utf8Bytes.Length];
            for (int i = 0; i < utf8Bytes.Length; i++)
                _data[i] = (byte)(utf8Bytes[i] ^ _pad[i]);
        }
        else
        {
            _data = utf8Bytes.ToArray();
        }
    }

    public bool IsEmpty => _data is null || _data.Length == 0;

    public int Length => _data?.Length ?? 0;

    public string ReadString()
    {
        if (_disposed || _data is null || _data.Length == 0)
            return string.Empty;

        if (!IsProtected || _pad is null)
            return Encoding.UTF8.GetString(_data);

        var plain = new byte[_data.Length];
        try
        {
            for (int i = 0; i < _data.Length; i++)
                plain[i] = (byte)(_data[i] ^ _pad[i]);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public byte[] ReadUtf8()
    {
        if (_disposed || _data is null || _data.Length == 0)
            return Array.Empty<byte>();

        if (!IsProtected || _pad is null)
            return (byte[])_data.Clone();

        var plain = new byte[_data.Length];
        for (int i = 0; i < _data.Length; i++)
            plain[i] = (byte)(_data[i] ^ _pad[i]);
        return plain;
    }

    public ProtectedString Clone()
    {
        var bytes = ReadUtf8();
        try
        {
            return new ProtectedString(IsProtected, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ProtectedString WithProtection(bool isProtected)
    {
        if (IsProtected == isProtected) return Clone();
        var bytes = ReadUtf8();
        try
        {
            return new ProtectedString(isProtected, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_data is not null)
        {
            CryptographicOperations.ZeroMemory(_data);
            _data = null;
        }
        if (_pad is not null)
        {
            CryptographicOperations.ZeroMemory(_pad);
            _pad = null;
        }
    }
}

public sealed class ProtectedBinary : IDisposable
{
    private byte[]? _data;
    private byte[]? _pad;
    private bool _disposed;

    public bool IsProtected { get; }

    public ProtectedBinary(bool isProtected, ReadOnlySpan<byte> data)
    {
        IsProtected = isProtected;
        if (data.IsEmpty)
        {
            _data = Array.Empty<byte>();
            return;
        }

        if (isProtected)
        {
            _pad = RandomNumberGenerator.GetBytes(data.Length);
            _data = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                _data[i] = (byte)(data[i] ^ _pad[i]);
        }
        else
        {
            _data = data.ToArray();
        }
    }

    public int Length => _data?.Length ?? 0;

    public byte[] ReadData()
    {
        if (_disposed || _data is null || _data.Length == 0)
            return Array.Empty<byte>();

        if (!IsProtected || _pad is null)
            return (byte[])_data.Clone();

        var plain = new byte[_data.Length];
        for (int i = 0; i < _data.Length; i++)
            plain[i] = (byte)(_data[i] ^ _pad[i]);
        return plain;
    }

    public ProtectedBinary Clone()
    {
        var bytes = ReadData();
        try
        {
            return new ProtectedBinary(IsProtected, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_data is not null)
        {
            CryptographicOperations.ZeroMemory(_data);
            _data = null;
        }
        if (_pad is not null)
        {
            CryptographicOperations.ZeroMemory(_pad);
            _pad = null;
        }
    }
}
