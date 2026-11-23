using System.Text;

namespace KeeDesk.Core.Services;

public sealed class AttachmentTextDocument
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding[] EncodingsWithBom =
    [
        new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
        new UTF8Encoding(true, true),
        new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)
    ];

    private readonly Encoding _encoding;
    public string Text { get; }
    public string NewLine { get; }

    private AttachmentTextDocument(string text, Encoding encoding)
    {
        Text = text;
        _encoding = encoding;
        var index = text.AsSpan().IndexOfAny('\r', '\n');
        NewLine = index < 0 ? Environment.NewLine :
            text[index] == '\n' ? "\n" :
            index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }

    public static AttachmentTextDocument? TryDecode(ReadOnlySpan<byte> bytes)
    {
        var encoding = Utf8;
        foreach (var candidate in EncodingsWithBom)
        {
            if (!bytes.StartsWith(candidate.Preamble)) continue;
            encoding = candidate;
            bytes = bytes[candidate.Preamble.Length..];
            break;
        }

        try
        {
            var text = encoding.GetString(bytes);
            return ContainsBinaryControls(text) ? null : new AttachmentTextDocument(text, encoding);
        }
        catch (DecoderFallbackException) { return null; }
    }

    public byte[] Encode(string text)
    {
        if (ContainsBinaryControls(text))
            throw new EncoderFallbackException("The attachment text contains unsupported control characters.");
        var preamble = _encoding.Preamble;
        var bytes = new byte[preamble.Length + _encoding.GetByteCount(text)];
        preamble.CopyTo(bytes);
        _encoding.GetBytes(text, bytes.AsSpan(preamble.Length));
        return bytes;
    }

    private static bool ContainsBinaryControls(string text)
    {
        foreach (var character in text)
            if (char.IsControl(character) && character is not ('\r' or '\n' or '\t' or '\f'))
                return true;
        return false;
    }
}
