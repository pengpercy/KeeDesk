using System.Text.RegularExpressions;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

/// <summary>
/// 解析 KeePass 2.x 占位符（{USERNAME}、{PASSWORD}、{TOTP}、{S:自定义字段} 等）与跨条目字段引用（{REF:P@I:UUID}）。
/// </summary>
public static partial class FieldReferenceResolver
{
    [GeneratedRegex(@"\{REF:([TUPAN])@([TUPANI]):([^\}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"\{S:([^\}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex CustomStringRegex();

    public static string Resolve(string input, KdbxEntry entry, KdbxDatabase? db = null, int maxDepth = 5)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('{') || maxDepth <= 0)
            return input;

        string result = input;

        // 1. Standard placeholders
        result = result.Replace("{TITLE}", entry.Title, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{USERNAME}", entry.UserName, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{PASSWORD}", entry.Password, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{URL}", entry.Url, StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{NOTES}", entry.Notes, StringComparison.OrdinalIgnoreCase);

        if (result.Contains("{TOTP}", StringComparison.OrdinalIgnoreCase) ||
            result.Contains("{TIMEOTP}", StringComparison.OrdinalIgnoreCase))
        {
            var totpSettings = TotpGenerator.TryExtractFromEntry(entry);
            var code = totpSettings is not null ? TotpGenerator.Generate(totpSettings).Code : string.Empty;
            result = result.Replace("{TOTP}", code, StringComparison.OrdinalIgnoreCase);
            result = result.Replace("{TIMEOTP}", code, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Custom string placeholders {S:Key}
        result = CustomStringRegex().Replace(result, m =>
        {
            var key = m.Groups[1].Value;
            return entry.GetString(key);
        });

        // 3. Cross-entry references {REF:Wanted@SearchIn:Text}
        if (db is not null && result.Contains("{REF:", StringComparison.OrdinalIgnoreCase))
        {
            result = RefRegex().Replace(result, m =>
            {
                char wanted = char.ToUpperInvariant(m.Groups[1].Value[0]);
                char searchIn = char.ToUpperInvariant(m.Groups[2].Value[0]);
                string searchText = m.Groups[3].Value;

                var target = FindEntry(db, searchIn, searchText);
                if (target is null) return m.Value;

                string val = wanted switch
                {
                    'T' => target.Title,
                    'U' => target.UserName,
                    'P' => target.Password,
                    'A' => target.Url,
                    'N' => target.Notes,
                    _ => string.Empty
                };

                return Resolve(val, target, db, maxDepth - 1);
            });
        }

        return result;
    }

    private static KdbxEntry? FindEntry(KdbxDatabase db, char searchIn, string text)
    {
        foreach (var e in db.EnumerateAllEntries(includeRecycleBin: false))
        {
            bool matched = searchIn switch
            {
                'I' => string.Equals(e.Uuid.ToString("N"), text, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(e.Uuid.ToString("D"), text, StringComparison.OrdinalIgnoreCase),
                'T' => string.Equals(e.Title, text, StringComparison.OrdinalIgnoreCase),
                'U' => string.Equals(e.UserName, text, StringComparison.OrdinalIgnoreCase),
                'P' => string.Equals(e.Password, text, StringComparison.Ordinal),
                'A' => string.Equals(e.Url, text, StringComparison.OrdinalIgnoreCase),
                'N' => e.Notes.Contains(text, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (matched) return e;
        }
        return null;
    }
}
