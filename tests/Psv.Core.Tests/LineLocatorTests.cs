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
}
