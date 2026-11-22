using System.Text.RegularExpressions;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

/// <summary>
/// 高级搜索条件参数（对标原版 KeePass 2.x SearchParameters）。
/// </summary>
public sealed class EntrySearchParameters
{
    public string Query { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool CaseSensitive { get; set; }
    public bool SearchTitles { get; set; } = true;
    public bool SearchUserNames { get; set; } = true;
    public bool SearchPasswords { get; set; }
    public bool SearchUrls { get; set; } = true;
    public bool SearchNotes { get; set; } = true;
    public bool SearchTags { get; set; } = true;
    public bool SearchCustomFields { get; set; } = true;
    public bool SearchUuid { get; set; }
    public bool ExcludeExpired { get; set; }
    public bool IncludeRecycleBin { get; set; }
    public KdbxGroup? ScopeGroup { get; set; }
}

/// <summary>
/// 对标原版 KeePass 2.x Find 菜单与 SearchForm 的高级检索引擎。
/// </summary>
public static class EntrySearchService
{
    public static List<KdbxEntry> Search(KdbxDatabase db, EntrySearchParameters p)
    {
        IEnumerable<KdbxEntry> source = p.ScopeGroup is not null
            ? EnumerateGroup(p.ScopeGroup)
            : db.EnumerateAllEntries(includeRecycleBin: p.IncludeRecycleBin);

        if (p.ExcludeExpired)
            source = source.Where(e => !e.IsExpired);

        var query = p.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return source.ToList();

        Regex? regex = null;
        if (p.UseRegex)
        {
            var opts = RegexOptions.CultureInvariant | (p.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            regex = new Regex(query, opts, TimeSpan.FromSeconds(2));
        }

        var cmp = p.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool Match(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return regex is not null ? regex.IsMatch(text) : text.Contains(query, cmp);
        }

        var results = new List<KdbxEntry>();
        foreach (var e in source)
        {
            bool hit =
                (p.SearchTitles && Match(e.Title)) ||
                (p.SearchUserNames && Match(e.UserName)) ||
                (p.SearchPasswords && Match(e.Password)) ||
                (p.SearchUrls && Match(e.Url)) ||
                (p.SearchNotes && Match(e.Notes)) ||
                (p.SearchTags && e.Tags.Any(Match)) ||
                (p.SearchUuid && (Match(e.Uuid.ToString("N")) || Match(e.Uuid.ToString("D")))) ||
                (p.SearchCustomFields && e.GetCustomStrings().Any(kv => Match(kv.Key) || Match(kv.Value.ReadString())));

            if (hit) results.Add(e);
        }
        return results;
    }

    public static List<KdbxEntry> FindExpired(KdbxDatabase db)
        => db.EnumerateAllEntries(false).Where(e => e.IsExpired).ToList();

    public static List<KdbxEntry> FindExpiringWithinDays(KdbxDatabase db, int days)
    {
        var now = DateTimeOffset.UtcNow;
        var limit = now.AddDays(days);
        return db.EnumerateAllEntries(false)
            .Where(e => e.Expires && e.ExpiryTime > now && e.ExpiryTime <= limit)
            .OrderBy(e => e.ExpiryTime)
            .ToList();
    }

    public static List<KdbxEntry> FindLastModified(KdbxDatabase db, int take = 30)
        => db.EnumerateAllEntries(false)
            .OrderByDescending(e => e.LastModificationTime)
            .Take(take)
            .ToList();

    public static List<KdbxEntry> FindLargeEntries(KdbxDatabase db, int take = 30)
        => db.EnumerateAllEntries(false)
            .Where(e => e.Binaries.Count > 0 || e.History.Count > 0 || e.Notes.Length > 200)
            .OrderByDescending(EstimateEntryBytes)
            .Take(take)
            .ToList();

    private static long EstimateEntryBytes(KdbxEntry e)
    {
        long total = e.Binaries.Values.Sum(b => (long)b.Length);
        total += e.Notes.Length * 2L;
        total += e.History.Sum(h => h.Binaries.Values.Sum(b => (long)b.Length) + 512L);
        return total;
    }

    private static IEnumerable<KdbxEntry> EnumerateGroup(KdbxGroup g)
    {
        foreach (var e in g.Entries) yield return e;
        foreach (var sub in g.Groups)
            foreach (var e in EnumerateGroup(sub))
                yield return e;
    }
}
