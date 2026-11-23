using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class VaultMigrationTests
{
    [Fact]
    public void CsvMappingSupportsAlternateDelimitersQuotedLinesAndNestedGroups()
    {
        const string csv = "\uFEFFcustom;login;secret;tree;memo\r\n门户;alice;\"#;{} %\";工作/基础设施;\"line 1\nline 2 \"\"quoted\"\"\"";
        var document = CsvImportDocument.Parse(csv);
        Assert.Equal(';', document.Delimiter);
        Assert.Equal(CsvImportField.UserName, document.GuessMapping(true)[1]);
        using var vault = new KdbxDatabase();
        int count = ImportExportService.ImportFromCsv(vault, document,
            [CsvImportField.Title, CsvImportField.UserName, CsvImportField.Password, CsvImportField.Group, CsvImportField.Notes], true);
        Assert.Equal(1, count);
        var entry = Assert.Single(vault.EnumerateAllEntries());
        Assert.Equal("门户", entry.Title);
        Assert.Equal("#;{} %", entry.Password);
        Assert.Equal("line 1\nline 2 \"quoted\"", entry.Notes);
        Assert.Equal("基础设施", entry.Parent?.Name);
        Assert.Equal("工作", entry.Parent?.Parent?.Name);
    }

    [Fact]
    public void CsvRoundTripDoesNotFlattenGroupsOrTreatLiteralSlashesAsSeparators()
    {
        using var source = new KdbxDatabase();
        var parent = new KdbxGroup { Name = "Teams/Europe" };
        var child = new KdbxGroup { Name = "Dev\\Ops" };
        source.RootGroup.AddGroup(parent);
        parent.AddGroup(child);
        child.AddEntry(new KdbxEntry { Title = "Example" });

        using var target = new KdbxDatabase();
        Assert.Equal(1, ImportExportService.ImportFromCsv(target, ImportExportService.ExportToCsv(source)));
        var imported = Assert.Single(target.EnumerateAllEntries());
        Assert.Equal(parent.Name, imported.Parent?.Parent?.Name);
        Assert.Equal(child.Name, imported.Parent?.Name);
        Assert.Throws<FormatException>(() => CsvImportDocument.Parse("title,notes\rA,\"unfinished"));
    }

    [Fact]
    public void HeaderlessImportUsesTheSelectedColumnsWithoutImportingEmptyRows()
    {
        var document = CsvImportDocument.Parse("first\tuser\r\n\t\r\nlast\tadmin", '\t');
        using var vault = new KdbxDatabase();
        Assert.Equal(2, ImportExportService.ImportFromCsv(vault, document, [CsvImportField.Title, CsvImportField.UserName], false));
        Assert.Equal(new[] { "first", "last" }, vault.EnumerateAllEntries().Select(entry => entry.Title));
        Assert.Throws<ArgumentException>(() =>
            ImportExportService.ImportFromCsv(vault, document, [CsvImportField.Title, CsvImportField.Title], false));
        Assert.Equal(2, vault.EnumerateAllEntries().Count());
    }

    [Fact]
    public void PrintReportEscapesMarkupAndIncludesPasswordsOnlyOnRequest()
    {
        using var vault = new KdbxDatabase();
        vault.RootGroup.AddEntry(new KdbxEntry { Title = "<Example>", Notes = "<script>test</script>", Password = "example-secret" });
        var english = VaultPrintReport.ExportHtml("Test & Vault", vault.EnumerateAllEntries(), new(false, false, false, "en-US"));
        Assert.DoesNotContain("example-secret", english);
        Assert.Contains("<table>", english);
        Assert.Contains("&lt;Example&gt;", english);
        Assert.Contains("&lt;script&gt;test&lt;/script&gt;", english);
        Assert.DoesNotContain("<script>", english);
        var chinese = VaultPrintReport.ExportHtml("测试", vault.EnumerateAllEntries(), new(true, true, false, "zh-CN"));
        Assert.Contains("<article>", chinese);
        Assert.Contains("example-secret", chinese);
        Assert.Contains("打印", chinese);
    }

    [Fact]
    public void StylesheetExportReadsKeePassXmlAndBlocksExternalImports()
    {
        using var vault = new KdbxDatabase();
        vault.RootGroup.AddEntry(new KdbxEntry { Title = "Expected title" });
        const string stylesheet = """
            <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:value-of select="//Entry/String[Key='Title']/Value"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(stylesheet));
        var result = System.Text.Encoding.UTF8.GetString(VaultStylesheetExport.Transform(vault, input)).TrimStart('\uFEFF');
        Assert.Equal("Expected title", result);

        const string external = """
            <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:include href="file:///unused-keedesk-test.xsl"/>
            </xsl:stylesheet>
            """;
        using var blocked = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(external));
        Assert.ThrowsAny<System.Xml.Xsl.XsltException>(() => VaultStylesheetExport.Transform(vault, blocked));
    }
}
