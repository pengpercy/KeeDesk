using System.Text;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class AttachmentTextDocumentTests
{
    public static TheoryData<string, string> Encodings => new()
    {
        { "utf-8", "" },
        { "utf-8", "EFBBBF" },
        { "utf-16", "FFFE" },
        { "utf-16BE", "FEFF" },
        { "utf-32", "FFFE0000" },
        { "utf-32BE", "0000FEFF" }
    };

    [Theory]
    [MemberData(nameof(Encodings))]
    public void EditingPreservesEncodingBomAndExistingLineEndings(string encodingName, string bom)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var preamble = Convert.FromHexString(bom);
        const string source = "第一行\r\nSecond line\n";
        var bytes = preamble.Concat(encoding.GetBytes(source)).ToArray();

        var document = Assert.IsType<AttachmentTextDocument>(AttachmentTextDocument.TryDecode(bytes));
        Assert.Equal(source, document.Text);
        Assert.Equal("\r\n", document.NewLine);
        Assert.Equal(bytes, document.Encode(document.Text));

        var edited = source + "第三行" + document.NewLine;
        Assert.Equal(preamble.Concat(encoding.GetBytes(edited)), document.Encode(edited));
    }

    [Fact]
    public void InvalidEncodingAndBinaryControlsAreRejected()
    {
        Assert.Null(AttachmentTextDocument.TryDecode([0xC3, 0x28]));
        Assert.Null(AttachmentTextDocument.TryDecode([0xFF, 0xFE, 0x61]));
        Assert.Null(AttachmentTextDocument.TryDecode([0x61, 0x00, 0x62]));
        var document = Assert.IsType<AttachmentTextDocument>(AttachmentTextDocument.TryDecode([]));
        Assert.Empty(document.Text);
        Assert.Throws<EncoderFallbackException>(() => document.Encode("a\0b"));
        Assert.Throws<EncoderFallbackException>(() => document.Encode("\uD800"));
    }
}
