using System.Xml;
using System.Xml.XPath;
using System.Xml.Xsl;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public static class VaultStylesheetExport
{
    public static byte[] Transform(KdbxDatabase database, Stream stylesheet)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var styleReader = XmlReader.Create(stylesheet, settings);
        // XSLT 1.0 interpreted execution also works on NativeAOT. Both resolvers are disabled.
#pragma warning disable CS0618
        var transform = new XslTransform();
#pragma warning restore CS0618
        transform.Load(styleReader, resolver: null);

        using var data = new StringReader(ImportExportService.ExportToKeePassXml(database));
        using var dataReader = XmlReader.Create(data, settings);
        using var output = new MemoryStream();
        transform.Transform(new XPathDocument(dataReader), args: null, output, resolver: null);
        return output.ToArray();
    }
}
