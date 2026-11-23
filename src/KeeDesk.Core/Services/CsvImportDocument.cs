using System.Text;

namespace KeeDesk.Core.Services;

public enum CsvImportField { Ignore, Group, Title, UserName, Password, Url, Notes, Totp, Tags, Favorite }

public sealed class CsvImportDocument
{
    public IReadOnlyList<string[]> Rows { get; }
    public int ColumnCount { get; }
    public char Delimiter { get; }

    private CsvImportDocument(List<string[]> rows, char delimiter)
    {
        Rows = rows;
        Delimiter = delimiter;
        ColumnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
    }

    public static CsvImportDocument Parse(string content, char? delimiter = null)
    {
        content = content.TrimStart('\uFEFF');
        char separator = delimiter ?? DetectDelimiter(content);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
        }

        void EndRow()
        {
            EndField();
            if (row.Any(value => !string.IsNullOrWhiteSpace(value)))
                rows.Add(row.ToArray());
            row.Clear();
        }

        for (int index = 0; index < content.Length; index++)
        {
            char character = content[index];
            if (quoted)
            {
                if (character != '"')
                    field.Append(character);
                else if (index + 1 < content.Length && content[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                    quoted = false;
            }
            else if (character == '"' && field.Length == 0)
                quoted = true;
            else if (character == separator)
                EndField();
            else if (character is '\n' or '\r')
            {
                EndRow();
                if (character == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
                    index++;
            }
            else
                field.Append(character);
        }
        if (quoted) throw new FormatException("A quoted CSV field is incomplete.");
        if (field.Length > 0 || row.Count > 0) EndRow();
        return new CsvImportDocument(rows, separator);
    }

    public CsvImportField[] GuessMapping(bool hasHeader)
    {
        var mapping = new CsvImportField[ColumnCount];
        if (!hasHeader || Rows.Count == 0) return mapping;
        var used = new HashSet<CsvImportField>();
        for (int index = 0; index < Rows[0].Length; index++)
        {
            var name = Rows[0][index].Trim().ToLowerInvariant();
            var field = name switch
            {
                "group" or "folder" or "grouping" or "category" or "分组" => CsvImportField.Group,
                "title" or "name" or "account" or "service" or "标题" => CsvImportField.Title,
                "username" or "user name" or "login_username" or "user" or "login" or "email" or "用户名" => CsvImportField.UserName,
                "password" or "login_password" or "pass" or "密码" => CsvImportField.Password,
                "url" or "login_uri" or "website" or "uri" or "网址" => CsvImportField.Url,
                "notes" or "note" or "comments" or "extra" or "备注" => CsvImportField.Notes,
                "totp" or "login_totp" or "otpauth" or "otp" or "2fa" => CsvImportField.Totp,
                "tags" or "tag" or "标签" => CsvImportField.Tags,
                "favorite" or "starred" or "收藏" => CsvImportField.Favorite,
                _ => CsvImportField.Ignore
            };
            if (used.Add(field)) mapping[index] = field;
        }
        return mapping;
    }

    public static string Cell(string[] row, IReadOnlyList<CsvImportField> mapping, CsvImportField field)
    {
        for (int i = 0; i < Math.Min(row.Length, mapping.Count); i++)
            if (mapping[i] == field) return row[i];
        return string.Empty;
    }

    private static char DetectDelimiter(string text)
    {
        bool quoted = false;
        var separators = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0 };
        bool content = false;
        foreach (var ch in text)
        {
            if (ch == '"') quoted = !quoted;
            if (quoted) { content = true; continue; }
            if (separators.TryGetValue(ch, out int count))
            {
                separators[ch] = count + 1;
                content = true;
            }
            else if (ch is '\r' or '\n' && content) break;
            else if (!char.IsWhiteSpace(ch)) content = true;
        }
        return separators.MaxBy(pair => pair.Value).Key;
    }
}
