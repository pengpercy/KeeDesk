using System.Net;
using System.Text;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

/// <summary>
/// 导入与导出服务：支持通用/Chrome/Bitwarden/1Password CSV 格式导入导出，以及可打印的 HTML 密码库应急表导出。
/// </summary>
public static class ImportExportService
{
    public static string ExportToCsv(KdbxDatabase db)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Group,Title,UserName,Password,URL,Notes,TOTP,Tags");

        foreach (var entry in db.EnumerateAllEntries(includeRecycleBin: false))
        {
            string groupPath = ExportGroupPath(entry.Parent, db.RootGroup);
            string totp = entry.GetString(KdbxConstants.FieldOtp);
            if (string.IsNullOrEmpty(totp)) totp = entry.GetString(KdbxConstants.FieldTimeOtpSecretBase32);

            sb.Append(EscapeCsv(groupPath)).Append(',')
              .Append(EscapeCsv(entry.Title)).Append(',')
              .Append(EscapeCsv(entry.UserName)).Append(',')
              .Append(EscapeCsv(entry.Password)).Append(',')
              .Append(EscapeCsv(entry.Url)).Append(',')
              .Append(EscapeCsv(entry.Notes)).Append(',')
              .Append(EscapeCsv(totp)).Append(',')
              .AppendLine(EscapeCsv(string.Join(";", entry.Tags)));
        }

        return sb.ToString();
    }

    public static int ImportFromCsv(KdbxDatabase db, string csvContent, KdbxGroup? targetGroup = null)
    {
        var document = CsvImportDocument.Parse(csvContent);
        return ImportFromCsv(db, document, document.GuessMapping(hasHeader: true), hasHeader: true, targetGroup);
    }

    public static int ImportFromCsv(KdbxDatabase db, CsvImportDocument document, IReadOnlyList<CsvImportField> mapping,
        bool hasHeader, KdbxGroup? targetGroup = null, Func<int, string>? untitled = null)
    {
        var selectedFields = mapping.Where(field => field != CsvImportField.Ignore).ToArray();
        if (selectedFields.Length == 0) return 0;
        if (mapping.Count != document.ColumnCount || selectedFields.Distinct().Count() != selectedFields.Length)
            throw new ArgumentException("Each destination field should be mapped once.", nameof(mapping));

        var root = targetGroup ?? db.RootGroup;
        int imported = 0;
        foreach (var row in document.Rows.Skip(hasHeader ? 1 : 0))
        {
            string Get(CsvImportField field) => CsvImportDocument.Cell(row, mapping, field);
            if (selectedFields.All(field => string.IsNullOrWhiteSpace(Get(field)))) continue;
            var entry = new KdbxEntry
            {
                Title = Get(CsvImportField.Title),
                UserName = Get(CsvImportField.UserName),
                Password = Get(CsvImportField.Password),
                Url = Get(CsvImportField.Url),
                Notes = Get(CsvImportField.Notes)
            };
            if (string.IsNullOrWhiteSpace(entry.Title))
                entry.Title = !string.IsNullOrWhiteSpace(entry.Url) ? entry.Url :
                    (untitled?.Invoke(imported + 1) ?? $"Imported entry #{imported + 1}");

            string totp = Get(CsvImportField.Totp);
            if (!string.IsNullOrWhiteSpace(totp)) entry.SetString(KdbxConstants.FieldOtp, totp, true);
            entry.Tags = Get(CsvImportField.Tags).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var favorite = Get(CsvImportField.Favorite).Trim();
            if (favorite == "1" || favorite.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                favorite.Equals("yes", StringComparison.OrdinalIgnoreCase)) entry.IsFavorite = true;

            var destination = root;
            foreach (var name in SplitGroupPath(Get(CsvImportField.Group)))
                destination = destination.Groups.FirstOrDefault(group => group.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !db.IsInRecycleBin(group)) ?? CreateSubGroup(destination, name);
            destination.AddEntry(entry);
            imported++;
        }
        if (imported > 0) db.IsModified = true;
        return imported;
    }

    private static string ExportGroupPath(KdbxGroup? group, KdbxGroup root)
    {
        var names = new Stack<string>();
        for (; group is not null && group != root; group = group.Parent)
            names.Push(group.Name.Replace(@"\", @"\\").Replace("/", @"\/"));
        return string.Join("/", names);
    }

    private static IEnumerable<string> SplitGroupPath(string path)
    {
        var name = new StringBuilder();
        for (int i = 0; i < path.Length; i++)
        {
            char ch = path[i];
            if (ch == '\\' && i + 1 < path.Length && path[i + 1] is '\\' or '/')
                name.Append(path[++i]);
            else if (ch == '/')
            {
                if (name.Length > 0) yield return name.ToString();
                name.Clear();
            }
            else
                name.Append(ch);
        }
        if (name.Length > 0) yield return name.ToString();
    }

    public static string ExportEmergencySheetHtml(KdbxDatabase db)
    {
        var entries = db.EnumerateAllEntries(includeRecycleBin: false).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{WebUtility.HtmlEncode(db.Meta.DatabaseName)} — KeeDesk 应急表</title>");
        sb.AppendLine("<style>body{font-family:-apple-system,Segoe UI,sans-serif;margin:32px;color:#111}h1{color:#047857}table{width:100%;border-collapse:collapse;margin-top:16px}th,td{border:1px solid #d1d5db;padding:8px 10px;text-align:left;font-size:13px}th{background:#f3f4f6}code{font-family:monospace}</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>KeeDesk 密码库应急表 — {WebUtility.HtmlEncode(db.Meta.DatabaseName)}</h1>");
        sb.AppendLine($"<p>导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · 条目总数：{entries.Count} · 请妥善保管纸质副本。</p>");
        sb.AppendLine("<table><thead><tr><th>分组</th><th>标题</th><th>用户名</th><th>密码</th><th>网址</th><th>备注</th></tr></thead><tbody>");
        foreach (var e in entries)
        {
            sb.Append("<tr>")
              .Append($"<td>{WebUtility.HtmlEncode(e.Parent?.Name ?? "")}</td>")
              .Append($"<td>{WebUtility.HtmlEncode(e.Title)}</td>")
              .Append($"<td><code>{WebUtility.HtmlEncode(e.UserName)}</code></td>")
              .Append($"<td><code>{WebUtility.HtmlEncode(e.Password)}</code></td>")
              .Append($"<td>{WebUtility.HtmlEncode(e.Url)}</td>")
              .Append($"<td>{WebUtility.HtmlEncode(e.Notes)}</td>")
              .AppendLine("</tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// 导出标准 KeePass 2.x 明文 XML（完整保留分组树、自定义字段、标签、附件与历史记录）。
    /// </summary>
    public static string ExportToKeePassXml(KdbxDatabase db)
    {
        var binaryPool = new List<KeeDesk.Core.Crypto.ProtectedBinary>();
        var bytes = KdbxXmlSerializer.WriteXml(db, isKdbx4: false, innerCipher: null, binaryPool);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 导入标准 KeePass 2.x XML 并按 UUID 无损合并到当前密码库。
    /// </summary>
    public static MergeSummary ImportFromKeePassXml(KdbxDatabase targetDb, string xmlContent)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(xmlContent));
        using var tempDb = new KdbxDatabase();
        var binaryPool = new List<KeeDesk.Core.Crypto.ProtectedBinary>();
        KdbxXmlSerializer.ReadXml(ms, tempDb, isKdbx4: false, innerCipher: null, binaryPool);
        return DatabaseMergeService.Merge(targetDb, tempDb);
    }

    private static KdbxGroup CreateSubGroup(KdbxGroup parent, string name)
    {
        var g = new KdbxGroup { Name = name, IconId = 48 };
        parent.AddGroup(g);
        return g;
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

}
