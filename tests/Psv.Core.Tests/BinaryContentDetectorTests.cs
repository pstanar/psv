namespace Psv.Core.Tests;

public class BinaryContentDetectorTests
{
    [Fact]
    public void EmptyHeaderIsNotBinary()
    {
        Assert.False(BinaryContentDetector.LooksBinary([]));
    }

    [Fact]
    public void PureAsciiTextIsNotBinary()
    {
        byte[] bytes = "The quick brown fox jumps over the lazy dog."u8.ToArray();
        Assert.False(BinaryContentDetector.LooksBinary(bytes));
    }

    [Fact]
    public void Utf8TextWithMultiByteCharactersIsNotBinary()
    {
        // "café" - the é is a genuine 2-byte UTF-8 sequence (0xC3 0xA9), same sample as
        // EncodingDetectorTests.DetectsValidMultiByteUtf8WithNoBom.
        byte[] bytes = [(byte)'c', (byte)'a', (byte)'f', 0xC3, 0xA9];
        Assert.False(BinaryContentDetector.LooksBinary(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void SingleNulByteAnywhereInSampleIsBinary(int nulIndex)
    {
        byte[] bytes = [(byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e'];
        bytes[nulIndex] = 0;
        Assert.True(BinaryContentDetector.LooksBinary(bytes));
    }

    [Fact]
    public void HighRatioOfControlBytesIsBinary()
    {
        // 4 of 10 bytes (40%) are non-text control bytes - over the 30% threshold.
        byte[] bytes = [0x01, 0x02, 0x03, 0x04, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', (byte)'f'];
        Assert.True(BinaryContentDetector.LooksBinary(bytes));
    }

    [Fact]
    public void LowRatioOfControlBytesIsNotBinary()
    {
        // A single stray control byte in an otherwise long text sample stays under the threshold.
        byte[] bytes = new byte[100];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)'a';
        }

        bytes[50] = 0x01;
        Assert.False(BinaryContentDetector.LooksBinary(bytes));
    }

    [Fact]
    public void TabNewlineCarriageReturnAreNotCountedAsNonTextControls()
    {
        byte[] bytes = "line one\r\n\tindented\r\nline two\r\n"u8.ToArray();
        Assert.False(BinaryContentDetector.LooksBinary(bytes));
    }

    [Fact]
    public void TypicalExecutableHeaderIsBinary()
    {
        // Real PE header prefix ("MZ" DOS stub start) - decisive via the embedded NUL bytes alone.
        byte[] bytes = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00];
        Assert.True(BinaryContentDetector.LooksBinary(bytes));
    }
}
