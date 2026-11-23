using System.Text.RegularExpressions;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record AutoTypeMatchResult(KdbxEntry Entry, string Sequence, string MatchReason)
{
    public AutoTypeMatchKind Kind { get; init; }
    public string? MatchValue { get; init; }
    public KdbxDatabase? Vault { get; init; }
    public bool RequiresConfirmation { get; init; }
}

public enum AutoTypeMatchKind { Manual, Rule, Title, Domain }

/// <summary>
/// 对标原版 KeePass 2.x 全局自动输入窗口标题匹配引擎：
/// 按目标前台窗口标题匹配条目的 AutoType Associations 通配符规则、标题与网址主机名。
/// </summary>
public static class AutoTypeMatcher
{
    public static List<AutoTypeMatchResult> FindMatches(KdbxDatabase db, string targetWindowTitle) =>
        FindMatches([db], targetWindowTitle);

    public static List<AutoTypeMatchResult> FindMatches(IEnumerable<KdbxDatabase> vaults, string targetWindowTitle)
    {
        var results = new List<AutoTypeMatchResult>();
        if (string.IsNullOrWhiteSpace(targetWindowTitle)) return results;

        var title = targetWindowTitle.Trim();
        foreach (var vault in vaults)
        foreach (var entry in GetEligibleEntries(vault))
        {
            var associations = GetMatchingAssociations(entry, vault, title).ToArray();
            if (associations.Length > 0)
            {
                results.AddRange(associations);
                continue;
            }

            var entryTitle = FieldReferenceResolver.Resolve(entry.Title, entry, vault);
            if (!string.IsNullOrWhiteSpace(entryTitle) && title.Contains(entryTitle, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(CreateMatch(vault, entry, AutoTypeMatchKind.Title));
                continue;
            }

            var url = FieldReferenceResolver.Resolve(entry.Url, entry, vault);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
            {
                var hostParts = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
                var label = uri.HostNameType == UriHostNameType.Dns && hostParts.Length == 2 ? hostParts[0] : null;
                if (ContainsWholeToken(title, uri.Host) || (label is { Length: >= 3 } && ContainsWholeToken(title, label)))
                    results.Add(CreateMatch(vault, entry, AutoTypeMatchKind.Domain, uri.Host) with { RequiresConfirmation = true });
            }
        }

        return results;
    }

    public static List<AutoTypeMatchResult> FindManualChoices(IEnumerable<KdbxDatabase> vaults) =>
        vaults.SelectMany(vault => GetEligibleEntries(vault)
            .Select(entry => CreateMatch(vault, entry, AutoTypeMatchKind.Manual) with { RequiresConfirmation = true })).ToList();

    public static AutoTypeMatchResult? MatchSelectedEntry(KdbxDatabase vault, KdbxEntry entry, string targetTitle) =>
        IsEnabled(entry) && !vault.IsInRecycleBin(entry)
            ? GetMatchingAssociations(entry, vault, targetTitle).FirstOrDefault() ??
              CreateMatch(vault, entry, AutoTypeMatchKind.Manual)
            : null;

    public static bool IsEnabled(KdbxEntry entry)
    {
        if (!entry.AutoType.Enabled) return false;
        for (var group = entry.Parent; group is not null; group = group.Parent)
            if (group.EnableAutoType is { } enabled)
                return enabled;
        return true;
    }

    private static IEnumerable<KdbxEntry> GetEligibleEntries(KdbxDatabase vault) =>
        vault.EnumerateAllEntries(includeRecycleBin: false).Where(entry => !entry.IsExpired && IsEnabled(entry));

    private static IEnumerable<AutoTypeMatchResult> GetMatchingAssociations(KdbxEntry entry, KdbxDatabase vault, string title)
    {
        var sequences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var association in entry.AutoType.Associations)
        {
            var pattern = FieldReferenceResolver.Resolve(association.Window, entry, vault);
            if (string.IsNullOrWhiteSpace(title) || !WildcardMatch(title, pattern))
                continue;
            var sequence = string.IsNullOrWhiteSpace(association.KeystrokeSequence)
                ? AutoTypeSequenceParser.GetEffectiveSequence(entry)
                : association.KeystrokeSequence;
            if (sequences.Add(sequence))
                yield return CreateMatch(vault, entry, AutoTypeMatchKind.Rule, association.Window, sequence);
        }
    }

    private static AutoTypeMatchResult CreateMatch(KdbxDatabase vault, KdbxEntry entry, AutoTypeMatchKind kind,
        string? value = null, string? sequence = null) =>
        new(entry, sequence ?? AutoTypeSequenceParser.GetEffectiveSequence(entry), kind.ToString())
        { Vault = vault, Kind = kind, MatchValue = value };

    public static bool WildcardMatch(string input, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        bool explicitRegex = pattern.Length >= 4 && pattern.StartsWith("//") && pattern.EndsWith("//");
        var regexPattern = explicitRegex ? pattern[2..^2] :
            @"\A" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + @"\z";
        try
        {
            return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
                (explicitRegex ? RegexOptions.None : RegexOptions.NonBacktracking), TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ContainsWholeToken(string title, string value)
    {
        for (int index = title.IndexOf(value, StringComparison.OrdinalIgnoreCase); index >= 0;
             index = title.IndexOf(value, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = index + value.Length;
            if ((index == 0 || !char.IsLetterOrDigit(title[index - 1])) &&
                (end == title.Length || !char.IsLetterOrDigit(title[end])))
                return true;
        }
        return false;
    }
}
