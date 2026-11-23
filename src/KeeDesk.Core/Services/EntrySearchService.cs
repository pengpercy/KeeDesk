using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

/// <summary>
/// 高级搜索条件参数（对标原版 KeePass 2.x SearchParameters）。
/// </summary>
public sealed class EntrySearchParameters
{
    public string Query { get; set; } = string.Empty;
    public bool UseRegex { get; set; }
    public bool UseXPath { get; set; }
    public bool CaseSensitive { get; set; }
    public bool SearchTitles { get; set; } = true;
    public bool SearchUserNames { get; set; } = true;
    public bool SearchPasswords { get; set; }
    public bool SearchUrls { get; set; } = true;
    public bool SearchNotes { get; set; } = true;
    public bool SearchTags { get; set; } = true;
    public bool SearchCustomFields { get; set; } = true;
    public bool SearchUuid { get; set; }
    public bool SearchHistory { get; set; }
    public bool ExcludeExpired { get; set; }
    public bool IncludeRecycleBin { get; set; }
    [JsonIgnore] public KdbxGroup? ScopeGroup { get; set; }

    internal bool SearchesString(string key) => key switch
    {
        KdbxConstants.FieldTitle => SearchTitles,
        KdbxConstants.FieldUserName => SearchUserNames,
        KdbxConstants.FieldPassword => SearchPasswords,
        KdbxConstants.FieldUrl => SearchUrls,
        KdbxConstants.FieldNotes => SearchNotes,
        _ => SearchCustomFields
    };
}

/// <summary>
/// 对标原版 KeePass 2.x Find 菜单与 SearchForm 的高级检索引擎。
/// </summary>
public static class EntrySearchService
{
    public static List<KdbxEntry> Search(KdbxDatabase db, EntrySearchParameters p)
    {
        IEnumerable<KdbxEntry> source = p.ScopeGroup is not null
            ? EnumerateGroup(p.ScopeGroup).Where(entry => p.IncludeRecycleBin || !db.IsInRecycleBin(entry))
            : db.EnumerateAllEntries(includeRecycleBin: p.IncludeRecycleBin);

        if (p.ExcludeExpired)
            source = source.Where(e => !e.IsExpired);

        var query = p.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
            return source.ToList();

        if (p.UseXPath)
            return SearchXPath(db, p, source, query);

        if (p.UseRegex)
        {
            var opts = RegexOptions.CultureInvariant | (p.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            var regex = new Regex(query, opts, TimeSpan.FromSeconds(2));
            return source.Where(entry => GetSearchValues(entry, p).Any(value => value.Length > 0 && regex.IsMatch(value))).ToList();
        }

        var terms = SplitTerms(query).ToArray();
        var comparison = p.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return source.Where(entry => MatchesTerms(entry, p, terms, comparison)).ToList();
    }

    private static List<KdbxEntry> SearchXPath(KdbxDatabase database, EntrySearchParameters parameters,
        IEnumerable<KdbxEntry> source, string query)
    {
        var candidates = source.ToList();
        var xml = KdbxXmlSerializer.CreateSearchDocument(database, parameters, candidates.ToHashSet());
        if (xml.XPathEvaluate(query, new KeePassXPathContext()) is not IEnumerable<object> nodes)
            throw new XPathException("The XPath expression must select nodes.");

        var matches = new HashSet<KdbxEntry>();
        foreach (var node in nodes)
        {
            // A selected field or history snapshot belongs to the enclosing live entry.
            for (var element = node as XElement ?? (node as XObject)?.Parent;
                 element is not null; element = element.Parent)
            {
                if (element.Annotation<KdbxEntry>() is not { } entry) continue;
                matches.Add(entry);
                break;
            }
        }
        return candidates.Where(matches.Contains).ToList();
    }

    private static bool MatchesTerms(KdbxEntry entry, EntrySearchParameters p, SearchTerm[] terms,
        StringComparison comparison)
    {
        if (terms.Length == 0) return true;
        if (terms.Length == 1)
        {
            var term = terms[0];
            return GetSearchValues(entry, p).Any(value => value.Contains(term.Value, comparison)) != term.Exclude;
        }

        var values = GetSearchValues(entry, p).ToArray();
        return terms.All(term => values.Any(value => value.Contains(term.Value, comparison)) != term.Exclude);
    }

    private static IEnumerable<SearchTerm> SplitTerms(string query)
    {
        var term = new StringBuilder();
        bool quoted = false;
        foreach (char character in query)
        {
            if (character == '"')
                quoted = !quoted;
            else if (!quoted && char.IsWhiteSpace(character))
            {
                if (term.Length > 0)
                {
                    yield return SearchTerm.From(term.ToString());
                    term.Clear();
                }
            }
            else
                term.Append(character);
        }
        if (term.Length > 0)
            yield return SearchTerm.From(term.ToString());
    }

    private static IEnumerable<string> GetSearchValues(KdbxEntry entry, EntrySearchParameters parameters)
    {
        foreach (var value in GetEntryValues(entry, parameters))
            yield return value;

        if (parameters.SearchHistory)
            foreach (var snapshot in entry.History)
                foreach (var value in GetEntryValues(snapshot, parameters))
                    yield return value;
    }

    private static IEnumerable<string> GetEntryValues(KdbxEntry entry, EntrySearchParameters p)
    {
        if (p.SearchTitles) yield return entry.Title;
        if (p.SearchUserNames) yield return entry.UserName;
        if (p.SearchPasswords) yield return entry.Password;
        if (p.SearchUrls) yield return entry.Url;
        if (p.SearchNotes) yield return entry.Notes;
        if (p.SearchTags)
            foreach (var tag in entry.Tags)
                yield return tag;
        if (p.SearchUuid)
        {
            yield return entry.Uuid.ToString("N");
            yield return entry.Uuid.ToString("D");
        }
        if (p.SearchCustomFields)
            foreach (var (key, value) in entry.GetCustomStrings())
            {
                yield return key;
                yield return value.ReadString();
            }
    }

    private readonly record struct SearchTerm(string Value, bool Exclude)
    {
        public static SearchTerm From(string value) =>
            value.Length > 1 && value[0] == '-' ? new SearchTerm(value[1..], true) : new SearchTerm(value, false);
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
