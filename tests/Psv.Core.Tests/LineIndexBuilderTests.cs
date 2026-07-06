using System.Text;

namespace Psv.Core.Tests;

public class LineIndexBuilderTests
{
    private static LineIndex BuildIndex(byte[] data, TextEncodingKind encoding = TextEncodingKind.Utf8, int chunkSizeBytes = 8 * 1024 * 1024, long checkpointLineInterval = 4096, long checkpointByteInterval = 1024 * 1024)
    {
        var source = new MutableByteSource(data);
        var index = new LineIndex();
        var builder = new LineIndexBuilder(source, encoding, chunkSizeBytes: chunkSizeBytes, checkpointLineInterval: checkpointLineInterval, checkpointByteInterval: checkpointByteInterval);
        builder.Build(index);
        return index;
    }

    [Fact]
    public void EmptyFileHasZeroLines()
    {
        var index = BuildIndex([]);
        Assert.Equal(0, index.KnownLineCount);
        Assert.True(index.IsComplete);
    }

    [Fact]
    public void SingleNewlineIsOneEmptyLine()
    {
        var index = BuildIndex("\n"u8.ToArray());
        Assert.Equal(1, index.KnownLineCount);
        Assert.Equal(LineEndingKind.Lf, index.DominantLineEnding);
    }

    [Fact]
    public void UnterminatedContentCountsAsOneLine()
    {
        var index = BuildIndex("abc"u8.ToArray());
        Assert.Equal(1, index.KnownLineCount);
    }

    [Fact]
    public void TerminatedThenUnterminatedIsTwoLines()
    {
        var index = BuildIndex("a\nb"u8.ToArray());
        Assert.Equal(2, index.KnownLineCount);
    }

    [Fact]
    public void LfOnlyFileCountsLinesCorrectly()
    {
        var index = BuildIndex("a\nb\nc\n"u8.ToArray());
        Assert.Equal(3, index.KnownLineCount);
        Assert.Equal(LineEndingKind.Lf, index.DominantLineEnding);
    }

    [Fact]
    public void CrLfOnlyFileCountsLinesCorrectly()
    {
        var index = BuildIndex("a\r\nb\r\nc\r\n"u8.ToArray());
        Assert.Equal(3, index.KnownLineCount);
        Assert.Equal(LineEndingKind.CrLf, index.DominantLineEnding);
    }

    [Fact]
    public void CrOnlyFileCountsLinesCorrectly()
    {
        var index = BuildIndex("a\rb\rc\r"u8.ToArray());
        Assert.Equal(3, index.KnownLineCount);
        Assert.Equal(LineEndingKind.Cr, index.DominantLineEnding);
    }

    [Fact]
    public void TrailingCrAtEofWithNothingAfterIsOneCrTerminatedLine()
    {
        var index = BuildIndex("abc\r"u8.ToArray());
        Assert.Equal(1, index.KnownLineCount);
        Assert.Equal(LineEndingKind.Cr, index.DominantLineEnding);
    }

    [Fact]
    public void MixedLineEndingsAreAllCountedAndDominantIsCorrect()
    {
        // 3x CrLf vs 1x Lf — CrLf should win clearly (not just via tie-break).
        var index = BuildIndex("a\r\nb\r\nc\r\nd\n"u8.ToArray());
        Assert.Equal(4, index.KnownLineCount);
        Assert.Equal(LineEndingKind.CrLf, index.DominantLineEnding);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CrLfSplitAcrossChunkBoundaryIsStillOneCrLfLine(int chunkSize)
    {
        // "ab\r\ncd" — force tiny chunks so the \r and \n frequently land in different reads.
        var index = BuildIndex("ab\r\ncd"u8.ToArray(), chunkSizeBytes: chunkSize);
        Assert.Equal(2, index.KnownLineCount);
        Assert.Equal(LineEndingKind.CrLf, index.DominantLineEnding);
    }

    [Fact]
    public void Utf16LeLineEndingsAreDetectedAtCorrectAlignment()
    {
        byte[] data = Encoding.Unicode.GetBytes("a\r\nb\nc");
        var index = BuildIndex(data, TextEncodingKind.Utf16LE);
        Assert.Equal(3, index.KnownLineCount);
    }

    [Fact]
    public void Utf16BeLineEndingsAreDetectedAtCorrectAlignment()
    {
        byte[] data = Encoding.BigEndianUnicode.GetBytes("a\r\nb\nc");
        var index = BuildIndex(data, TextEncodingKind.Utf16BE);
        Assert.Equal(3, index.KnownLineCount);
    }

    [Fact]
    public void CheckpointsAreInsertedAtLineIntervalForManyShortLines()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10_000; i++)
        {
            sb.Append('x').Append('\n');
        }

        var index = BuildIndex(Encoding.UTF8.GetBytes(sb.ToString()), checkpointLineInterval: 100, checkpointByteInterval: long.MaxValue);
        Assert.Equal(10_000, index.KnownLineCount);
        // seed checkpoint (line 0) + one every 100 lines = 1 + 100
        Assert.Equal(101, index.CheckpointCount);
    }

    [Fact]
    public void CheckpointsAreInsertedAtByteIntervalForFewLongLines()
    {
        string longLine = new string('x', 1000);
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++)
        {
            sb.Append(longLine).Append('\n');
        }

        var index = BuildIndex(Encoding.UTF8.GetBytes(sb.ToString()), checkpointLineInterval: long.MaxValue, checkpointByteInterval: 5000);
        Assert.Equal(50, index.KnownLineCount);
        Assert.True(index.CheckpointCount > 1);
    }
}
