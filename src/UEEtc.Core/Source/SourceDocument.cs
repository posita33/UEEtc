using System.Text;

namespace UEEtc.Core.Source;

internal sealed record SourceDocument(byte[] Bytes, string Text, Encoding Encoding, int PreambleLength)
{
    public static SourceDocument Read(byte[] bytes)
    {
        Encoding encoding;
        int preamble;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) ||
            bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            throw new InvalidDataException("UTF-32 は対象外です。");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            (encoding, preamble) = (new UTF8Encoding(true, true), 3);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            (encoding, preamble) = (new UnicodeEncoding(false, true, true), 2);
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            (encoding, preamble) = (new UnicodeEncoding(true, true, true), 2);
        else
            (encoding, preamble) = (new UTF8Encoding(false, true), 0);
        var text = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        if (text.Contains('\0')) throw new InvalidDataException("NUL を含むソースは対象外です。");
        return new(bytes, text, encoding, preamble);
    }

    public byte[] Encode(string text) => [.. Bytes.AsSpan(0, PreambleLength), .. Encoding.GetBytes(text)];
}
