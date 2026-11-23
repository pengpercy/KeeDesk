using System.Net;
using System.Text;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record VaultPrintOptions(bool Detailed, bool IncludePasswords, bool CurrentSelection, string Language);

public static class VaultPrintReport
{
    public static string ExportHtml(string vaultName, IEnumerable<KdbxEntry> entries, VaultPrintOptions options)
    {
        bool en = options.Language.Equals("en-US", StringComparison.OrdinalIgnoreCase);
        var headings = en
            ? new[] { "Group", "Entry", "User name", "Website", "Notes", "Password" }
            : new[] { "分组", "条目", "用户名", "网址", "备注", "密码" };
        var labels = options.IncludePasswords ? headings : headings[..^1];
        string title = Html(vaultName);
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"").Append(en ? "en" : "zh-CN")
            .Append("\"><head><meta charset=\"utf-8\"><title>").Append(title)
            .AppendLine("</title><style>");
        html.AppendLine("""
            body { margin: 2rem; font: 14px/1.4 system-ui, sans-serif; color: #111; }
            h1 { font-size: 1.6rem; }
            h2 { font-size: 1.15rem; margin: 0 0 .4rem; }
            table { width: 100%; border-collapse: collapse; }
            th, td { border: 1px solid #aaa; padding: .35rem .5rem; text-align: left; vertical-align: top; }
            td, dd { overflow-wrap: anywhere; white-space: pre-wrap; }
            article { border-bottom: 1px solid #aaa; padding: .7rem 0; }
            dl { display: grid; grid-template-columns: 8rem 1fr; margin: 0; gap: .15rem .5rem; }
            dt { font-weight: 600; } dd { margin: 0; }
            button { margin-bottom: 1rem; }
            @media print { body { margin: 0; } button { display: none; } thead { display: table-header-group; }
                article, tr { break-inside: avoid; } }
            </style></head><body>
            """);
        html.Append("<h1>").Append(title).AppendLine("</h1>");
        html.Append("<button type=\"button\" onclick=\"window.print()\">")
            .Append(en ? "Print" : "打印").AppendLine("</button>");

        if (!options.Detailed)
        {
            html.Append("<table><thead><tr>");
            foreach (var label in labels) html.Append("<th scope=\"col\">").Append(Html(label)).Append("</th>");
            html.AppendLine("</tr></thead><tbody>");
        }

        foreach (var entry in entries)
        {
            string group = entry.Parent?.GetFullPath() ?? string.Empty;
            string[] fields = options.IncludePasswords
                ? [group, entry.Title, entry.UserName, entry.Url, entry.Notes, entry.Password]
                : [group, entry.Title, entry.UserName, entry.Url, entry.Notes];
            if (!options.Detailed)
            {
                html.Append("<tr>");
                foreach (var field in fields) html.Append("<td>").Append(Html(field)).Append("</td>");
                html.AppendLine("</tr>");
                continue;
            }

            html.Append("<article><h2>").Append(Html(entry.Title)).AppendLine("</h2><dl>");
            for (int i = 0; i < labels.Length; i++)
            {
                if (i == 1 || fields[i].Length == 0) continue;
                html.Append("<dt>").Append(Html(labels[i])).Append("</dt><dd>")
                    .Append(Html(fields[i])).AppendLine("</dd>");
            }
            html.AppendLine("</dl></article>");
        }

        if (!options.Detailed) html.AppendLine("</tbody></table>");
        return html.AppendLine("</body></html>").ToString();
    }

    private static string Html(string value) => WebUtility.HtmlEncode(value);
}
