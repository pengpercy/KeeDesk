using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record SimilarPasswordPair(KdbxEntry First, KdbxEntry Second, double Similarity);

public sealed class SecurityAuditReport
{
    public int TotalEntries { get; init; }
    public int SecurityScore { get; init; }
    public List<KdbxEntry> WeakPasswords { get; } = new();
    public List<KdbxEntry> ReusedPasswords { get; } = new();
    public List<KdbxEntry> ExpiredOrExpiringSoon { get; } = new();
    public List<KdbxEntry> MissingTwoFactor { get; } = new();
    public int TotpEnabledCount { get; init; }
    public int TotalIssues => WeakPasswords.Count + ReusedPasswords.Count + ExpiredOrExpiringSoon.Count;
}

/// <summary>
/// 密码库安全审计引擎：检测弱密码、重复密码、已过期/即将过期条目以及 2FA 覆盖率，并计算综合安全评分。
/// </summary>
public static class SecurityAuditService
{
    public static async Task<List<SimilarPasswordPair>> FindSimilarPasswordsAsync(KdbxDatabase db,
        CancellationToken cancellationToken = default)
    {
        // Capture on the caller's thread. The background scan can be cancelled while the vault locks.
        var passwords = new List<(KdbxEntry Entry, char[] Value)>();
        try
        {
            foreach (var entry in db.EnumerateAllEntries(includeRecycleBin: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.QualityCheck || !entry.Strings.TryGetValue(KdbxConstants.FieldPassword, out var password)
                    || password.IsEmpty)
                    continue;

                var bytes = password.ReadUtf8();
                try { passwords.Add((entry, Encoding.UTF8.GetChars(bytes))); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }

            return await Task.Run(() => FindBestPasswordMatches(passwords, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, value) in passwords)
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }

    private static List<SimilarPasswordPair> FindBestPasswordMatches(
        List<(KdbxEntry Entry, char[] Value)> passwords, CancellationToken cancellationToken)
    {
        if (passwords.Count < 2) return [];
        var row = new int[passwords.Max(password => password.Value.Length) + 1];
        var bestMatches = new SimilarPasswordPair?[passwords.Count];

        for (int i = 0; i < passwords.Count - 1; i++)
        {
            var (first, firstPassword) = passwords[i];
            for (int j = i + 1; j < passwords.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (second, secondPassword) = passwords[j];
                int longest = Math.Max(firstPassword.Length, secondPassword.Length);
                int limit = longest / 4;
                if (limit == 0 || Math.Abs(firstPassword.Length - secondPassword.Length) > limit)
                    continue;

                int distance = FindEditDistance(firstPassword, secondPassword, limit, row, cancellationToken);
                if (distance == 0 || distance > limit) continue;

                double similarity = 1 - (double)distance / longest;
                bool firstImproves = similarity > (bestMatches[i]?.Similarity ?? 0);
                bool secondImproves = similarity > (bestMatches[j]?.Similarity ?? 0);
                if (!firstImproves && !secondImproves) continue;

                var pair = new SimilarPasswordPair(first, second, similarity);
                if (firstImproves) bestMatches[i] = pair;
                if (secondImproves) bestMatches[j] = pair;
            }
        }

        return bestMatches.OfType<SimilarPasswordPair>().Distinct().OrderByDescending(pair => pair.Similarity).ToList();
    }

    private static int FindEditDistance(ReadOnlySpan<char> first, ReadOnlySpan<char> second, int limit,
        int[] row, CancellationToken cancellationToken)
    {
        int prefix = first.CommonPrefixLength(second);
        first = first[prefix..];
        second = second[prefix..];
        while (!first.IsEmpty && !second.IsEmpty && first[^1] == second[^1])
        {
            first = first[..^1];
            second = second[..^1];
        }
        if (first.IsEmpty || second.IsEmpty) return first.Length + second.Length;

        for (int j = 0; j <= second.Length; j++)
            row[j] = j;

        for (int i = 1; i <= first.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int diagonal = row[0];
            int minimum = row[0] = i;
            for (int j = 1; j <= second.Length; j++)
            {
                int previousRow = row[j];
                int substitution = diagonal + (first[i - 1] == second[j - 1] ? 0 : 1);
                row[j] = Math.Min(substitution, 1 + Math.Min(previousRow, row[j - 1]));
                minimum = Math.Min(minimum, row[j]);
                diagonal = previousRow;
            }
            if (minimum > limit) return limit + 1;
        }
        return row[second.Length];
    }

    public static SecurityAuditReport Analyze(KdbxDatabase db, int expiringSoonDays = 14)
    {
        var entries = db.EnumerateAllEntries(includeRecycleBin: false).ToList();
        var passwordGroups = new Dictionary<string, List<KdbxEntry>>(StringComparer.Ordinal);
        int totpCount = 0;

        var weak = new List<KdbxEntry>();
        var expiring = new List<KdbxEntry>();
        var no2fa = new List<KdbxEntry>();
        var now = DateTimeOffset.UtcNow;
        var threshold = now.AddDays(expiringSoonDays);

        foreach (var entry in entries)
        {
            if (entry.HasTotp()) totpCount++;
            else if (!string.IsNullOrWhiteSpace(entry.Url)) no2fa.Add(entry);

            if (entry.Expires && entry.ExpiryTime <= threshold)
                expiring.Add(entry);

            if (!entry.QualityCheck) continue;

            var pwd = entry.Password;
            if (!string.IsNullOrEmpty(pwd))
            {
                var strength = PasswordGenerator.EvaluateStrength(pwd);
                if (strength.Level is PasswordStrengthLevel.VeryWeak or PasswordStrengthLevel.Weak)
                    weak.Add(entry);

                if (!passwordGroups.TryGetValue(pwd, out var list))
                {
                    list = new List<KdbxEntry>();
                    passwordGroups[pwd] = list;
                }
                list.Add(entry);
            }
        }

        var reused = passwordGroups.Values
            .Where(g => g.Count > 1)
            .SelectMany(g => g)
            .Distinct()
            .ToList();

        int score = 100;
        if (entries.Count > 0)
        {
            score -= Math.Min(40, weak.Count * 8);
            score -= Math.Min(35, reused.Count * 6);
            score -= Math.Min(15, expiring.Count * 3);
            double totpRatio = (double)totpCount / entries.Count;
            if (totpRatio < 0.25) score -= 5;
        }
        score = Math.Clamp(score, 0, 100);

        var report = new SecurityAuditReport
        {
            TotalEntries = entries.Count,
            SecurityScore = score,
            TotpEnabledCount = totpCount
        };
        report.WeakPasswords.AddRange(weak);
        report.ReusedPasswords.AddRange(reused);
        report.ExpiredOrExpiringSoon.AddRange(expiring);
        report.MissingTwoFactor.AddRange(no2fa);
        return report;
    }
}
