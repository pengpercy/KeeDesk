using System.Buffers.Binary;
using System.Security.Cryptography;
using KeePassModern.Core.Kdbx;

namespace KeePassModern.Core.Services;

public sealed record TotpSettings(
    string SecretBase32,
    int Digits = 6,
    int Period = 30,
    string Algorithm = "SHA1",
    string Issuer = "",
    string Account = "");

public sealed record TotpResult(string Code, string FormattedCode, int RemainingSeconds, int Period);

/// <summary>
/// RFC 6238 TOTP 与 RFC 4226 HOTP 动态验证码生成器，同时兼容 KeePass 2.x (TimeOtp-*) 与 KeePassXC (otpauth://) 格式。
/// </summary>
public static class TotpGenerator
{
    public static TotpSettings? TryExtractFromEntry(KdbxEntry entry)
    {
        var otpUri = entry.GetString(KdbxConstants.FieldOtp);
        if (!string.IsNullOrWhiteSpace(otpUri))
        {
            var parsed = TryParseOtpAuthUri(otpUri);
            if (parsed is not null) return parsed;
        }

        var secret = entry.GetString(KdbxConstants.FieldTimeOtpSecretBase32);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            int digits = int.TryParse(entry.GetString(KdbxConstants.FieldTimeOtpLength), out var d) ? d : 6;
            int period = int.TryParse(entry.GetString(KdbxConstants.FieldTimeOtpPeriod), out var p) ? p : 30;
            var algo = entry.GetString(KdbxConstants.FieldTimeOtpAlgorithm);
            if (string.IsNullOrWhiteSpace(algo)) algo = "SHA1";
            algo = algo.Replace("HMAC-SHA-", "SHA", StringComparison.OrdinalIgnoreCase);
            return new TotpSettings(secret.Trim(), digits, period, algo);
        }

        return null;
    }

    public static TotpSettings? TryParseOtpAuthUri(string uriText)
    {
        uriText = uriText.Trim();
        if (!uriText.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            // Treat bare Base32 secret as default TOTP
            if (Base32Decode(uriText).Length > 0)
                return new TotpSettings(uriText);
            return null;
        }

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            return null;

        var query = ParseQueryString(uri.Query);
        if (!query.TryGetValue("secret", out var secret) || string.IsNullOrWhiteSpace(secret))
            return null;

        int digits = query.TryGetValue("digits", out var dStr) && int.TryParse(dStr, out var d) ? d : 6;
        int period = query.TryGetValue("period", out var pStr) && int.TryParse(pStr, out var p) ? p : 30;
        string algo = query.TryGetValue("algorithm", out var aStr) && !string.IsNullOrWhiteSpace(aStr) ? aStr.ToUpperInvariant() : "SHA1";
        string issuer = query.TryGetValue("issuer", out var iStr) ? iStr : string.Empty;
        string label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

        return new TotpSettings(secret, digits, period, algo, issuer, label);
    }

    public static string BuildOtpAuthUri(TotpSettings settings, string label = "KeePassModern")
    {
        var encLabel = Uri.EscapeDataString(string.IsNullOrWhiteSpace(settings.Account) ? label : settings.Account);
        var uri = $"otpauth://totp/{encLabel}?secret={settings.SecretBase32.Replace(" ", "").ToUpperInvariant()}&period={settings.Period}&digits={settings.Digits}&algorithm={settings.Algorithm.ToUpperInvariant()}";
        if (!string.IsNullOrWhiteSpace(settings.Issuer))
            uri += $"&issuer={Uri.EscapeDataString(settings.Issuer)}";
        return uri;
    }

    public static TotpResult Generate(TotpSettings settings, DateTimeOffset? now = null)
    {
        var utc = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        int period = Math.Max(1, settings.Period);
        long counter = utc / period;
        int remaining = period - (int)(utc % period);

        byte[] key = Base32Decode(settings.SecretBase32);
        string code = ComputeHotp(key, counter, settings.Digits, settings.Algorithm);
        string formatted = code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
        return new TotpResult(code, formatted, remaining, period);
    }

    public static string ComputeHotp(byte[] key, long counter, int digits = 6, string algorithm = "SHA1")
    {
        if (key.Length == 0) return new string('0', digits);

        byte[] counterBytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        byte[] hash = algorithm.ToUpperInvariant() switch
        {
            "SHA256" => HMACSHA256.HashData(key, counterBytes),
            "SHA512" => HMACSHA512.HashData(key, counterBytes),
            _ => HMACSHA1.HashData(key, counterBytes)
        };

        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24)
                   | ((hash[offset + 1] & 0xFF) << 16)
                   | ((hash[offset + 2] & 0xFF) << 8)
                   | (hash[offset + 3] & 0xFF);

        int mod = (int)Math.Pow(10, Math.Clamp(digits, 4, 10));
        return (binary % mod).ToString().PadLeft(digits, '0');
    }

    public static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var cleaned = new List<int>(input.Length);
        foreach (char ch in input.ToUpperInvariant())
        {
            if (ch is ' ' or '-' or '=') continue;
            int idx = alphabet.IndexOf(ch);
            if (idx < 0) return Array.Empty<byte>();
            cleaned.Add(idx);
        }

        if (cleaned.Count == 0) return Array.Empty<byte>();
        var result = new List<byte>(cleaned.Count * 5 / 8);
        int buffer = 0, bitsLeft = 0;

        foreach (int val in cleaned)
        {
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return result.ToArray();
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var k = Uri.UnescapeDataString(part[..idx]);
            var v = Uri.UnescapeDataString(part[(idx + 1)..]);
            dict[k] = v;
        }
        return dict;
    }
}
