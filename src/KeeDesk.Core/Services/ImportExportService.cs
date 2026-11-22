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
            string groupPath = entry.Parent?.GetFullPath("/") ?? string.Empty;
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
        var rows = ParseCsv(csvContent);
        if (rows.Count < 2) return 0;

        var header = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int idxTitle = FindColumn(header, "title", "name", "account", "service");
        int idxUser = FindColumn(header, "username", "login_username", "user", "login", "email");
        int idxPass = FindColumn(header, "password", "login_password", "pass");
        int idxUrl = FindColumn(header, "url", "login_uri", "website", "uri");
        int idxNotes = FindColumn(header, "notes", "note", "comments", "extra");
        int idxTotp = FindColumn(header, "totp", "login_totp", "otpauth", "otp", "2fa");
        int idxGroup = FindColumn(header, "group", "folder", "grouping", "category");
        int idxTags = FindColumn(header, "tags", "tag");
        int idxFav = FindColumn(header, "favorite", "starred");

        var destRoot = targetGroup ?? db.RootGroup;
        int imported = 0;

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            string Get(int idx) => idx >= 0 && idx < row.Count ? row[idx] : string.Empty;

            var entry = new KdbxEntry
            {
                Title = Get(idxTitle),
                UserName = Get(idxUser),
                Password = Get(idxPass),
                Url = Get(idxUrl),
                Notes = Get(idxNotes)
            };

            if (string.IsNullOrWhiteSpace(entry.Title))
                entry.Title = !string.IsNullOrWhiteSpace(entry.Url) ? entry.Url : $"导入条目 #{i}";

            var totp = Get(idxTotp);
            if (!string.IsNullOrWhiteSpace(totp))
                entry.SetString(KdbxConstants.FieldOtp, totp, true);

            var tags = Get(idxTags);
            if (!string.IsNullOrWhiteSpace(tags))
                entry.Tags = tags.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            var fav = Get(idxFav).Trim();
            if (fav is "1" or "true" or "True" or "TRUE" or "yes")
                entry.IsFavorite = true;

            var groupName = Get(idxGroup).Trim();
            KdbxGroup dest = destRoot;
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                var leafName = groupName.Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? groupName;
                dest = destRoot.Groups.FirstOrDefault(g => string.Equals(g.Name, leafName, StringComparison.OrdinalIgnoreCase))
                       ?? CreateSubGroup(destRoot, leafName);
            }

            dest.AddEntry(entry);
            imported++;
        }

        if (imported > 0) db.IsModified = true;
        return imported;
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

    private static int FindColumn(List<string> headers, params string[] candidates)
    {
        foreach (var c in candidates)
        {
            int idx = headers.IndexOf(c);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static List<List<string>> ParseCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                }
                else if (c == '\r') continue;
                else if (c == '\n')
                {
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                }
                else cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
