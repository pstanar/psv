namespace Psv.Core.Tests;

public class EncodingDetectorTests
{
    [Fact]
    public void DetectsUtf8Bom()
    {
        var result = EncodingDetector.Detect([0xEF, 0xBB, 0xBF, (byte)'a']);
        Assert.Equal(TextEncodingKind.Utf8, result.Kind);
        Assert.Equal(3, result.BomLength);
    }

    [Fact]
    public void DetectsUtf16LeBom()
    {
        var result = EncodingDetector.Detect([0xFF, 0xFE, (byte)'a', 0x00]);
        Assert.Equal(TextEncodingKind.Utf16LE, result.Kind);
        Assert.Equal(2, result.BomLength);
    }

    [Fact]
    public void DetectsUtf16BeBom()
    {
        var result = EncodingDetector.Detect([0xFE, 0xFF, 0x00, (byte)'a']);
        Assert.Equal(TextEncodingKind.Utf16BE, result.Kind);
        Assert.Equal(2, result.BomLength);
    }

    [Fact]
    public void DefaultsToUtf8WithNoBom()
    {
        var result = EncodingDetector.Detect([(byte)'a', (byte)'b', (byte)'c']);
        Assert.Equal(TextEncodingKind.Utf8, result.Kind);
        Assert.Equal(0, result.BomLength);
    }

    [Fact]
    public void HandlesEmptyHeader()
    {
        var result = EncodingDetector.Detect([]);
        Assert.Equal(TextEncodingKind.Utf8, result.Kind);
        Assert.Equal(0, result.BomLength);
    }

    [Fact]
    public void DetectsValidMultiByteUtf8WithNoBom()
    {
        // "café" - the é is a genuine 2-byte UTF-8 sequence (0xC3 0xA9), not a Windows-1252 byte.
        byte[] bytes = [(byte)'c', (byte)'a', (byte)'f', 0xC3, 0xA9];
        var result = EncodingDetector.Detect(bytes);
        Assert.Equal(TextEncodingKind.Utf8, result.Kind);
        Assert.Equal(0, result.BomLength);
    }

    [Fact]
    public void FallsBackToWindows1252ForASingleByteHighBitCharacterAtTheEndOfTheFile()
    {
        // "café" encoded as Windows-1252/Latin-1: é is the single byte 0xE9. Its high bits (1110)
        // happen to also match a valid UTF-8 3-byte sequence's lead byte, but since this sample is
        // the entire file, there are no continuation bytes coming - it must be rejected as UTF-8,
        // not treated as an inconclusive truncated sequence.
        byte[] bytes = [(byte)'c', (byte)'a', (byte)'f', 0xE9];
        var result = EncodingDetector.Detect(bytes, sampleIsEntireFile: true);
        Assert.Equal(TextEncodingKind.Windows1252, result.Kind);
        Assert.Equal(0, result.BomLength);
    }

    [Fact]
    public void TreatsTheSameTrailingByteAsInconclusiveWhenTheSampleIsOnlyAFilePrefix()
    {
        // Identical bytes to the test above, but flagged as a prefix of a larger file (the normal
        // case when sniffing a large file) - 0xE9 could still be the start of a real 3-byte UTF-8
        // character whose continuation bytes simply weren't included in the sample.
        byte[] bytes = [(byte)'c', (byte)'a', (byte)'f', 0xE9];
        var result = EncodingDetector.Detect(bytes, sampleIsEntireFile: false);
        Assert.Equal(TextEncodingKind.Utf8, result.Kind);
    }

    [Fact]
    public void RejectsAnUnexpectedContinuationByteAsUtf8()
    {
        byte[] bytes = [(byte)'a', 0x80, (byte)'b'];
        var result = EncodingDetector.Detect(bytes);
        Assert.Equal(TextEncodingKind.Windows1252, result.Kind);
    }

    [Fact]
    public void RejectsAnOverlongUtf8Encoding()
    {
        // 0xC0 0x80 is an overlong (invalid) encoding of NUL - a real UTF-8 decoder must reject it.
        byte[] bytes = [0xC0, 0x80];
        var result = EncodingDetector.Detect(bytes);
        Assert.Equal(TextEncodingKind.Windows1252, result.Kind);
    }

    [Fact]
    public void RejectsASurrogateEncodedAsUtf8()
    {
        // 0xED 0xA0 0x80 decodes to U+D800, a UTF-16 surrogate half - never a legal UTF-8 codepoint.
        byte[] bytes = [0xED, 0xA0, 0x80];
        var result = EncodingDetector.Detect(bytes);
        Assert.Equal(TextEncodingKind.Windows1252, result.Kind);
    }

    [Fact]
    public void DoesNotMisclassifyAMultiByteSequenceTruncatedAtTheSampleBoundary()
    {
        // The lead byte of a 2-byte sequence with no continuation byte because the sample ends
        // there - this must not be treated as invalid, since a real file would have the rest of
        // the character right after our sample cuts off.
        byte[] bytes = [(byte)'a', (byte)'b', 0xC3];
        var result = EncodingDetector.Detect(bytes);
        Assert.Equal(TextEncodingKind.Utf8, result.Kind);
    }
}
