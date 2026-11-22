using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

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
