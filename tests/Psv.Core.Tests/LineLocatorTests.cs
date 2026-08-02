using System.Text;

namespace Psv.Core.Tests;

public class LineLocatorTests
{
    private static (LineIndex Index, LineLocator Locator) BuildLocator(string content,
        TextEncodingKind encoding = TextEncodingKind.Utf8,
        long checkpointLineInterval = 4096,
        long checkpointByteInterval = 1024 * 1024)
    {
        byte[] data = encoding switch
        {
            TextEncodingKind.Utf16LE => Encoding.Unicode.GetBytes(content),
            TextEncodingKind.Utf16BE => Encoding.BigEndianUnicode.GetBytes(content),
            _ => Encoding.UTF8.GetBytes(content),
        };

        var source = new MutableByteSource(data);
        var index = new LineIndex();
        var builder = new LineIndexBuilder(source, encoding, checkpointLineInterval: checkpointLineInterval, checkpointByteInterval: checkpointByteInterval);
        builder.Build(index);
        var locator = new LineLocator(index, source, encoding);
        return (index, locator);
    }

    [Fact]
    public void GetLineTextReturnsEachLineWithoutTerminator()
    {
        var (_, locator) = BuildLocator("alpha\nbeta\r\ngamma\rdelta");

        Assert.Equal("alpha", locator.GetLineText(0));
        Assert.Equal("beta", locator.GetLineText(1));
        Assert.Equal("gamma", locator.GetLineText(2));
        Assert.Equal("delta", locator.GetLineText(3));
    }

    [Fact]
    public void TryGetLineRangeReturnsFalseForOutOfRangeLine()
    {
        var (_, locator) = BuildLocator("only one line");
        Assert.False(locator.TryGetLineRange(5, out _));
        Assert.False(locator.TryGetLineRange(-1, out _));
    }

    [Fact]
    public void GetLineRangesReturnsContiguousRangeAcrossACheckpointBoundary()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            sb.Append('L').Append(i).Append('\n');
        }

        var (index, locator) = BuildLocator(sb.ToString(), checkpointLineInterval: 100, checkpointByteInterval: long.MaxValue);
        Assert.True(index.CheckpointCount > 1);

        // request a range that straddles a checkpoint at line 100
        var ranges = locator.GetLineRanges(95, 10);
        Assert.Equal(10, ranges.Count);

        for (int i = 0; i < ranges.Count; i++)
        {
            string text = locator.DecodeLine(ranges[i]);
            Assert.Equal($"L{95 + i}", text);
        }
    }

    [Fact]
    public void GetLineRangesAtEndOfFileReturnsFewerThanRequested()
    {
        var (_, locator) = BuildLocator("a\nb\nc");
        var ranges = locator.GetLineRanges(1, 10);
        Assert.Equal(2, ranges.Count);
        Assert.Equal("b", locator.DecodeLine(ranges[0]));
        Assert.Equal("c", locator.DecodeLine(ranges[1]));
    }

    [Fact]
    public void RandomAccessMatchesSequentialDecodeForManyLines()
    {
        var expected = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            string line = $"row-{i}-{new string('y', i % 7)}";
            expected.Add(line);
            sb.Append(line).Append('\n');
        }

        var (_, locator) = BuildLocator(sb.ToString(), checkpointLineInterval: 64, checkpointByteInterval: 4096);

        var rng = new Random(1234);
        for (int trial = 0; trial < 200; trial++)
        {
            int lineNumber = rng.Next(expected.Count);
            Assert.Equal(expected[lineNumber], locator.GetLineText(lineNumber));
        }
    }

    [Fact]
    public void Utf16LeLocatorDecodesLinesCorrectly()
    {
        var (_, locator) = BuildLocator("alpha\nbeta\ngamma", TextEncodingKind.Utf16LE);
        Assert.Equal("alpha", locator.GetLineText(0));
        Assert.Equal("beta", locator.GetLineText(1));
        Assert.Equal("gamma", locator.GetLineText(2));
    }

    [Fact]
    public void PathologicalHugeUnterminatedLineIsStillOneCorrectLine()
    {
        string huge = new string('z', 500_000);
        var (index, locator) = BuildLocator(huge);
        Assert.Equal(1, index.KnownLineCount);
        Assert.Equal(huge, locator.GetLineText(0));
    }

    /// <summary>
    /// Wraps a byte source whose Length getter appends extra bytes to the underlying data as a
    /// side effect of its Nth call, returning the pre-growth value for that call - simulating growth
    /// landing exactly in the window right after LineScanWalker.Walk's own internal length check
    /// decides there's nothing more to scan.
    /// </summary>
    private sealed class GrowsAfterNthLengthAccess(MutableByteSource inner, int growAfterCall, byte[] extra) : IByteSource
    {
        private int _calls;

        public long Length
        {
            get
            {
                _calls++;
                long value = inner.Length;
                if (_calls == growAfterCall)
                {
                    inner.Append(extra);
                }

                return value;
            }
        }

        public int Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);
    }

    [Fact]
    public void GetLineRangesTrailingLineIgnoresGrowthThatHappensAfterScanning()
    {
        // Regression test: the trailing unterminated-line fallback used to re-read source.Length
        // fresh after Walk had already returned (which captured its own, earlier, length snapshot
        // internally), so growth landing in that gap could make the synthesized last-line range
        // absorb bytes appended after the scan had conceptually finished. "line1\n" is one full
        // line; "line2" (5 bytes) is the trailing unterminated content Walk stops on.
        var mutable = new MutableByteSource(Encoding.UTF8.GetBytes("line1\nline2"));
        var index = new LineIndex();
        new LineIndexBuilder(mutable, TextEncodingKind.Utf8).Build(index);
        Assert.Equal(2, index.KnownLineCount);

        // Walk calls source.Length exactly twice for this input: once to read the 11 available
        // bytes, once more to observe no bytes remain and stop - growAfterCall: 2 lands the
        // simulated append right after that second, scan-ending call returns its (correct, 11-byte)
        // value, mimicking concurrent growth arriving just as the scan wraps up.
        var growing = new GrowsAfterNthLengthAccess(mutable, growAfterCall: 2, "EXTRA"u8.ToArray());
        var locator = new LineLocator(index, growing, TextEncodingKind.Utf8);

        var ranges = locator.GetLineRanges(0, 2);

        Assert.Equal(2, ranges.Count);
        Assert.Equal("line2", locator.DecodeLine(ranges[1]));
    }

    [Fact]
    public void FindLineNumberForOffsetReturnsOwningLineForEveryByteIncludingTerminators()
    {
        // "aa\n" (line 0, offsets 0-2), "bbb\n" (line 1, offsets 3-6), "c" (line 2, offset 7).
        var (_, locator) = BuildLocator("aa\nbbb\nc");

        Assert.Equal(0, locator.FindLineNumberForOffset(0));
        Assert.Equal(0, locator.FindLineNumberForOffset(1));
        Assert.Equal(0, locator.FindLineNumberForOffset(2)); // the '\n' itself still belongs to line 0
        Assert.Equal(1, locator.FindLineNumberForOffset(3));
        Assert.Equal(1, locator.FindLineNumberForOffset(6));
        Assert.Equal(2, locator.FindLineNumberForOffset(7)); // trailing unterminated line
    }

    [Fact]
    public void FindLineNumberForOffsetClampsOutOfRangeOffsets()
    {
        var (_, locator) = BuildLocator("alpha\nbeta\ngamma");

        Assert.Equal(0, locator.FindLineNumberForOffset(-100));
        Assert.Equal(2, locator.FindLineNumberForOffset(long.MaxValue));
    }

    [Fact]
    public void FindLineNumberForOffsetMatchesGetLineRangesAcrossACheckpointBoundary()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            sb.Append('L').Append(i).Append('\n');
        }

        var (index, locator) = BuildLocator(sb.ToString(), checkpointLineInterval: 100, checkpointByteInterval: long.MaxValue);
        Assert.True(index.CheckpointCount > 1);

        // Line 150 straddles a checkpoint recorded at line 100 - resolve its start offset via
        // GetLineRanges, then confirm the reverse lookup lands back on the same line.
        var range = locator.GetLineRanges(150, 1)[0];
        Assert.Equal(150, locator.FindLineNumberForOffset(range.StartOffset));
        Assert.Equal(150, locator.FindLineNumberForOffset(range.StartOffset + range.ContentLength));
    }

    [Fact]
    public void FindLineNumberForOffsetOnEmptyFileReturnsLineZero()
    {
        var (_, locator) = BuildLocator(string.Empty);
        Assert.Equal(0, locator.FindLineNumberForOffset(0));
    }
}
