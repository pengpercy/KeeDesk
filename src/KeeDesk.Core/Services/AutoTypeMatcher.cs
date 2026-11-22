using System.Text.RegularExpressions;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record AutoTypeMatchResult(KdbxEntry Entry, string Sequence, string MatchReason)
{
    public AutoTypeMatchKind Kind { get; init; }
    public string? MatchValue { get; init; }
}

public enum AutoTypeMatchKind { Manual, Rule, Title, Domain }

/// <summary>
/// 对标原版 KeePass 2.x 全局自动输入窗口标题匹配引擎：
/// 按目标前台窗口标题匹配条目的 AutoType Associations 通配符规则、标题与网址主机名。
/// </summary>
public static class AutoTypeMatcher
{
    public static List<AutoTypeMatchResult> FindMatches(KdbxDatabase db, string targetWindowTitle)
    {
        var results = new List<AutoTypeMatchResult>();
        if (string.IsNullOrWhiteSpace(targetWindowTitle)) return results;

        var titleNorm = targetWindowTitle.Trim();
        foreach (var entry in db.EnumerateAllEntries(includeRecycleBin: false))
        {
            if (entry.IsExpired || !entry.AutoType.Enabled) continue;

            // 1. 优先匹配自定义 AutoType 窗口关联规则（支持 * 和 ? 通配符）
            bool matchedAssoc = false;
            foreach (var assoc in entry.AutoType.Associations)
            {
                if (string.IsNullOrWhiteSpace(assoc.Window)) continue;
                if (WildcardMatch(titleNorm, assoc.Window))
                {
                    var seq = string.IsNullOrWhiteSpace(assoc.KeystrokeSequence)
                        ? AutoTypeSequenceParser.GetEffectiveSequence(entry)
                        : assoc.KeystrokeSequence;
                    results.Add(new AutoTypeMatchResult(entry, seq, $"规则匹配：{assoc.Window}")
                    { Kind = AutoTypeMatchKind.Rule, MatchValue = assoc.Window });
                    matchedAssoc = true;
                    break;
                }
            }
            if (matchedAssoc) continue;

            // 2. 匹配条目标题
            if (!string.IsNullOrWhiteSpace(entry.Title) &&
                titleNorm.Contains(entry.Title, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new AutoTypeMatchResult(entry, AutoTypeSequenceParser.GetEffectiveSequence(entry), "窗口标题匹配") { Kind = AutoTypeMatchKind.Title });
                continue;
            }

            // 3. 匹配 URL 域名关键字（如 github.com -> github）
            if (!string.IsNullOrWhiteSpace(entry.Url) &&
                Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host))
            {
                var hostParts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var mainDomain = hostParts.Length >= 2 ? hostParts[^2] : uri.Host;
                if (mainDomain.Length >= 3 && titleNorm.Contains(mainDomain, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new AutoTypeMatchResult(entry, AutoTypeSequenceParser.GetEffectiveSequence(entry), $"域名匹配：{uri.Host}")
                    { Kind = AutoTypeMatchKind.Domain, MatchValue = uri.Host });
                }
            }
        }

        return results;
    }

    public static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
