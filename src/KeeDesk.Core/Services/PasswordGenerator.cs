using System.Security.Cryptography;
using System.Text;

namespace KeeDesk.Core.Services;

public enum PasswordStrengthLevel
{
    VeryWeak,
    Weak,
    Fair,
    Strong,
    VeryStrong
}

public sealed record PasswordStrengthResult(double EntropyBits, PasswordStrengthLevel Level, string Label);

public sealed class PasswordGeneratorOptions
{
    public int Length { get; set; } = 20;
    public bool IncludeUppercase { get; set; } = true;
    public bool IncludeLowercase { get; set; } = true;
    public bool IncludeDigits { get; set; } = true;
    public bool IncludeSpecial { get; set; } = true;
    public bool ExcludeLookAlike { get; set; } = true;
    public string CustomCharacters { get; set; } = string.Empty;

    // Passphrase mode
    public int WordCount { get; set; } = 5;
    public string WordSeparator { get; set; } = "-";
    public bool CapitalizeWords { get; set; } = true;
    public bool AppendNumber { get; set; } = true;

    // Pattern mode (KeePass pattern syntax: u=upper, l=lower, d=digit, s=special, a=any, \x=literal)
    public string Pattern { get; set; } = "uullddss-uullddss";
}

/// <summary>
/// 密码学安全的密码生成器：支持随机字符集、KeePass Pattern 模式规则与助记词（Passphrase）三种模式，并提供实时熵值与强度评估。
/// </summary>
public static class PasswordGenerator
{
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Special = "!@#$%^&*()-_=+[]{}|;:,.<>?";
    private const string LookAlike = "O0oIl1|";

    private static readonly string[] WordList =
    [
        "alpine", "amber", "anchor", "apollo", "arctic", "atlas", "aurora", "autumn",
        "bamboo", "beacon", "breeze", "bridge", "cactus", "canyon", "carbon", "castle",
        "cedar", "cipher", "citadel", "cloud", "cobalt", "comet", "compass", "copper",
        "coral", "cosmos", "crater", "crystal", "cyclone", "delta", "desert", "diamond",
        "dolphin", "dragon", "eclipse", "emerald", "enigma", "falcon", "fjord", "forest",
        "fortress", "galaxy", "garnet", "glacier", "granite", "harbor", "harvest", "horizon",
        "indigo", "island", "ivory", "jasper", "journey", "jupiter", "kernel", "kingdom",
        "lagoon", "lantern", "laurel", "legend", "leopard", "liberty", "lotus", "lunar",
        "magnet", "mantis", "marble", "meadow", "mercury", "meteor", "mirage", "monarch",
        "nebula", "nectar", "neptune", "nimbus", "obsidian", "ocean", "olive", "omega",
        "onyx", "opal", "oracle", "orbit", "orchid", "osprey", "panther", "paradox",
        "pebble", "pelican", "phantom", "phoenix", "pioneer", "planet", "plasma", "prism",
        "pulsar", "quartz", "quiver", "radiant", "raptor", "raven", "redwood", "river",
        "rocket", "saffron", "sapphire", "saturn", "scarlet", "sequoia", "shadow", "shield",
        "silver", "solstice", "sparrow", "spectra", "sphinx", "spiral", "spruce", "starlight",
        "summit", "sunrise", "tempest", "thunder", "timber", "titanium", "topaz", "tornado",
        "trident", "tundra", "typhoon", "unicorn", "valkyrie", "valley", "vanguard", "velvet",
        "venture", "vertex", "voyager", "walnut", "whisper", "wildfire", "willow", "zephyr"
    ];

    public static string GenerateFromCharset(PasswordGeneratorOptions options)
    {
        var pools = new List<string>();
        if (options.IncludeUppercase) pools.Add(FilterLookAlike(Upper, options.ExcludeLookAlike));
        if (options.IncludeLowercase) pools.Add(FilterLookAlike(Lower, options.ExcludeLookAlike));
        if (options.IncludeDigits) pools.Add(FilterLookAlike(Digits, options.ExcludeLookAlike));
        if (options.IncludeSpecial) pools.Add(FilterLookAlike(Special, options.ExcludeLookAlike));
        if (!string.IsNullOrEmpty(options.CustomCharacters))
            pools.Add(FilterLookAlike(options.CustomCharacters, options.ExcludeLookAlike));

        pools.RemoveAll(string.IsNullOrEmpty);
        if (pools.Count == 0) pools.Add(Lower);

        string combined = string.Concat(pools).Distinct().Aggregate(new StringBuilder(), (sb, c) => sb.Append(c)).ToString();
        int len = Math.Clamp(options.Length, 4, 256);
        var chars = new char[len];

        // Guarantee at least one character from each enabled pool when length permits
        int idx = 0;
        foreach (var pool in pools)
        {
            if (idx >= len) break;
            chars[idx++] = pool[RandomNumberGenerator.GetInt32(pool.Length)];
        }
        while (idx < len)
        {
            chars[idx++] = combined[RandomNumberGenerator.GetInt32(combined.Length)];
        }

        // Fisher-Yates shuffle using CSPRNG
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    public static string GeneratePassphrase(PasswordGeneratorOptions options)
    {
        int count = Math.Clamp(options.WordCount, 3, 16);
        var words = new string[count];
        for (int i = 0; i < count; i++)
        {
            var w = WordList[RandomNumberGenerator.GetInt32(WordList.Length)];
            if (options.CapitalizeWords && w.Length > 0)
                w = char.ToUpperInvariant(w[0]) + w[1..];
            words[i] = w;
        }

        var result = string.Join(options.WordSeparator ?? "-", words);
        if (options.AppendNumber)
            result += RandomNumberGenerator.GetInt32(10, 999);
        return result;
    }

    public static string GenerateFromPattern(string pattern, bool excludeLookAlike = false)
    {
        if (string.IsNullOrEmpty(pattern)) return string.Empty;
        var sb = new StringBuilder();
        var upper = FilterLookAlike(Upper, excludeLookAlike);
        var lower = FilterLookAlike(Lower, excludeLookAlike);
        var digits = FilterLookAlike(Digits, excludeLookAlike);
        var special = FilterLookAlike(Special, excludeLookAlike);
        var any = upper + lower + digits + special;

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(pattern[++i]);
                continue;
            }

            string? pool = c switch
            {
                'u' or 'U' => upper,
                'l' or 'L' => lower,
                'd' or 'D' => digits,
                's' or 'S' => special,
                'a' or 'A' => any,
                'h' => "0123456789abcdef",
                'H' => "0123456789ABCDEF",
                _ => null
            };

            sb.Append(pool is not null ? pool[RandomNumberGenerator.GetInt32(pool.Length)] : c);
        }
        return sb.ToString();
    }

    public static PasswordStrengthResult EvaluateStrength(string? password)
    {
        if (string.IsNullOrEmpty(password))
            return new PasswordStrengthResult(0, PasswordStrengthLevel.VeryWeak, "空密码");

        bool hasLower = false, hasUpper = false, hasDigit = false, hasSpecial = false, hasUnicode = false;
        foreach (char c in password)
        {
            if (c is >= 'a' and <= 'z') hasLower = true;
            else if (c is >= 'A' and <= 'Z') hasUpper = true;
            else if (c is >= '0' and <= '9') hasDigit = true;
            else if (c <= 127) hasSpecial = true;
            else hasUnicode = true;
        }

        int poolSize = 0;
        if (hasLower) poolSize += 26;
        if (hasUpper) poolSize += 26;
        if (hasDigit) poolSize += 10;
        if (hasSpecial) poolSize += 32;
        if (hasUnicode) poolSize += 64;
        if (poolSize == 0) poolSize = 1;

        double charsetBits = password.Length * Math.Log2(poolSize);

        // Penalize repeated characters / low uniqueness
        int uniqueChars = password.Distinct().Count();
        double uniquenessFactor = Math.Clamp((double)uniqueChars / Math.Min(password.Length, 16), 0.35, 1.0);
        double bits = Math.Round(charsetBits * uniquenessFactor, 1);

        var (level, label) = bits switch
        {
            < 28 => (PasswordStrengthLevel.VeryWeak, "极弱"),
            < 45 => (PasswordStrengthLevel.Weak, "较弱"),
            < 65 => (PasswordStrengthLevel.Fair, "一般"),
            < 90 => (PasswordStrengthLevel.Strong, "强"),
            _ => (PasswordStrengthLevel.VeryStrong, "极强")
        };

        return new PasswordStrengthResult(bits, level, label);
    }

    private static string FilterLookAlike(string s, bool exclude)
        => exclude ? new string(s.Where(c => !LookAlike.Contains(c)).ToArray()) : s;
}
