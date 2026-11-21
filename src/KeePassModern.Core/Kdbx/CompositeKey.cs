using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace KeePassModern.Core.Kdbx;

/// <summary>
/// KeePass 复合主密钥：支持主密码与密钥文件（XML v1.0、带哈希校验的 XML v2.0、32 字节原始二进制、64 字节十六进制或任意文件 SHA-256）。
/// </summary>
public sealed class CompositeKey : IDisposable
{
    private readonly List<byte[]> _keyParts = new();
    private bool _disposed;

    public bool HasPassword { get; private set; }
    public bool HasKeyFile { get; private set; }
    public bool HasWindowsUserAccount { get; private set; }

    public static CompositeKey FromPassword(string password)
    {
        var key = new CompositeKey();
        key.AddPassword(password);
        return key;
    }

    public static CompositeKey FromPasswordAndKeyFile(string? password, string? keyFilePath, bool useWindowsUserAccount = false)
    {
        var key = new CompositeKey();
        if (!string.IsNullOrEmpty(password))
            key.AddPassword(password);
        if (!string.IsNullOrWhiteSpace(keyFilePath))
            key.AddKeyFile(keyFilePath);
        if (useWindowsUserAccount)
            key.AddWindowsUserAccountKey();
        if (key._keyParts.Count == 0)
            key.AddPassword(string.Empty);
        return key;
    }

    public void AddWindowsUserAccountKey()
    {
        _keyParts.Add(WindowsUserAccountKey.GetOrCreateKeyHash());
        HasWindowsUserAccount = true;
    }

    public void AddPassword(string password)
    {
        var utf8 = Encoding.UTF8.GetBytes(password ?? string.Empty);
        try
        {
            _keyParts.Add(SHA256.HashData(utf8));
            HasPassword = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
        }
    }

    public void AddKeyFile(string filePath)
    {
        byte[] raw = File.ReadAllBytes(filePath);
        try
        {
            AddKeyFileBytes(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public void AddKeyFileBytes(byte[] raw)
    {
        if (TryParseXmlKeyFile(raw, out var xmlKey))
        {
            _keyParts.Add(xmlKey);
            HasKeyFile = true;
            return;
        }

        if (raw.Length == 32)
        {
            _keyParts.Add((byte[])raw.Clone());
            HasKeyFile = true;
            return;
        }

        if (raw.Length == 64 && TryParseHex64(raw, out var hexKey))
        {
            _keyParts.Add(hexKey);
            HasKeyFile = true;
            return;
        }

        _keyParts.Add(SHA256.HashData(raw));
        HasKeyFile = true;
    }

    public byte[] ComputeCompositeHash()
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in _keyParts)
            sha.AppendData(part);
        return sha.GetHashAndReset();
    }

    /// <summary>
    /// Generates a standard KeePass 2.x XML v2.0 key file with SHA-256 prefix hash verification.
    /// </summary>
    public static string GenerateXmlKeyFileV2()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        byte[] hash = SHA256.HashData(secret);
        string hashHex = Convert.ToHexString(hash.AsSpan(0, 4));
        string dataHex = Convert.ToHexString(secret);
        CryptographicOperations.ZeroMemory(secret);

        var formatted = new StringBuilder();
        for (int i = 0; i < dataHex.Length; i += 8)
        {
            if (i > 0) formatted.Append(i % 32 == 0 ? "\n\t\t\t" : " ");
            formatted.Append(dataHex.AsSpan(i, Math.Min(8, dataHex.Length - i)));
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <KeyFile>
            	<Meta>
            		<Version>2.0</Version>
            	</Meta>
            	<Key>
            		<Data Hash="{hashHex}">
            			{formatted}
            		</Data>
            	</Key>
            </KeyFile>
            """;
    }

    private static bool TryParseXmlKeyFile(byte[] raw, out byte[] keyBytes)
    {
        keyBytes = Array.Empty<byte>();
        try
        {
            using var ms = new MemoryStream(raw, writable: false);
            var doc = XDocument.Load(ms);
            var root = doc.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "KeyFile", StringComparison.OrdinalIgnoreCase))
                return false;

            var version = root.Element("Meta")?.Element("Version")?.Value?.Trim() ?? "1.0";
            var dataEl = root.Element("Key")?.Element("Data");
            if (dataEl is null) return false;

            var dataText = dataEl.Value.Trim();
            if (version.StartsWith("2.", StringComparison.Ordinal))
            {
                var hexOnly = new StringBuilder(dataText.Length);
                foreach (char c in dataText)
                {
                    if (!char.IsWhiteSpace(c)) hexOnly.Append(c);
                }
                var decoded = Convert.FromHexString(hexOnly.ToString());
                var expectedHash = dataEl.Attribute("Hash")?.Value?.Trim();
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    var actualHash = Convert.ToHexString(SHA256.HashData(decoded).AsSpan(0, 4));
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new CryptographicException("Key file XML v2.0 hash verification failed.");
                }
                keyBytes = decoded;
                return true;
            }
            else
            {
                keyBytes = Convert.FromBase64String(dataText);
                return true;
            }
        }
        catch (CryptographicException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseHex64(byte[] raw, out byte[] keyBytes)
    {
        keyBytes = Array.Empty<byte>();
        try
        {
            var text = Encoding.ASCII.GetString(raw);
            keyBytes = Convert.FromHexString(text);
            return keyBytes.Length == 32;
        }
        catch
        {
            return false;
        }
    }

    public CompositeKey Clone()
    {
        var copy = new CompositeKey
        {
            HasPassword = HasPassword,
            HasKeyFile = HasKeyFile
        };
        foreach (var part in _keyParts)
            copy._keyParts.Add((byte[])part.Clone());
        return copy;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var part in _keyParts)
            CryptographicOperations.ZeroMemory(part);
        _keyParts.Clear();
    }
}
