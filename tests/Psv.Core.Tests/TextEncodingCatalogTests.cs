namespace Psv.Core.Tests;

public class TextEncodingCatalogTests
{
    [Fact]
    public void Windows1252AndLatin1DecodeByte0x80Differently()
    {
        // 0x80 is '€' in Windows-1252 but the C1 control U+0080 in Latin-1 — a common source of
        // silently-wrong text if these two get confused.
        byte[] bytes = [0x80];

        string cp1252 = TextEncodingCatalog.Resolve(TextEncodingKind.Windows1252).GetString(bytes);
        string latin1 = TextEncodingCatalog.Resolve(TextEncodingKind.Latin1).GetString(bytes);

        Assert.Equal("€", cp1252);
        Assert.Equal("", latin1);
    }

    [Fact]
    public void Utf8RoundTripsWithoutEmittingBom()
    {
        var encoding = TextEncodingCatalog.Resolve(TextEncodingKind.Utf8);
        byte[] bytes = encoding.GetBytes("hello");
        Assert.Equal("hello"u8.ToArray(), bytes);
    }

    [Fact]
    public void Utf16LeAndBeAreDistinct()
    {
        var le = TextEncodingCatalog.Resolve(TextEncodingKind.Utf16LE);
        var be = TextEncodingCatalog.Resolve(TextEncodingKind.Utf16BE);

        Assert.Equal([(byte)'a', 0x00], le.GetBytes("a"));
        Assert.Equal([0x00, (byte)'a'], be.GetBytes("a"));
    }
}
