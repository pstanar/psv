using System.Text;

namespace Psv.Core.Tests;

public class LineIndexBuilderContinueTests
{
    [Fact]
    public void ContinueAfterAppendingWholeNewLinesExtendsIndexCorrectly()
    {
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("line1\nline2\n"));
        var index = new LineIndex();
        var builder = new LineIndexBuilder(data, TextEncodingKind.Utf8);

        builder.Build(index);
        Assert.Equal(2, index.KnownLineCount);

        data.Append(Encoding.UTF8.GetBytes("line3\nline4\n"));
        builder.Continue(index);

        Assert.Equal(4, index.KnownLineCount);
        var locator = new LineLocator(index, data, TextEncodingKind.Utf8);
        Assert.Equal("line1", locator.GetLineText(0));
        Assert.Equal("line2", locator.GetLineText(1));
        Assert.Equal("line3", locator.GetLineText(2));
        Assert.Equal("line4", locator.GetLineText(3));
    }

    [Fact]
    public void ContinueMergesPreviouslyUnterminatedTrailingLineWithNewBytes()
    {
        // "line2" has no trailing newline yet — it's a provisional/virtual last line.
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("line1\nline2"));
        var index = new LineIndex();
        var builder = new LineIndexBuilder(data, TextEncodingKind.Utf8);

        builder.Build(index);
        Assert.Equal(2, index.KnownLineCount);
        var locator = new LineLocator(index, data, TextEncodingKind.Utf8);
        Assert.Equal("line2", locator.GetLineText(1));

        // More bytes arrive continuing that same line, then a real terminator, then a new line.
        data.Append(Encoding.UTF8.GetBytes("-more\nline3"));
        builder.Continue(index);

        Assert.Equal(3, index.KnownLineCount);
        Assert.Equal("line2-more", locator.GetLineText(1));
        Assert.Equal("line3", locator.GetLineText(2));
    }

    [Fact]
    public void ContinueWithNoNewDataIsANoOp()
    {
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("a\nb\nc\n"));
        var index = new LineIndex();
        var builder = new LineIndexBuilder(data, TextEncodingKind.Utf8);

        builder.Build(index);
        long before = index.KnownLineCount;

        builder.Continue(index);

        Assert.Equal(before, index.KnownLineCount);
    }

    [Fact]
    public void ResetThenBuildOnShorterDataReflectsOnlyNewContent()
    {
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("line1\nline2\nline3\n"));
        var index = new LineIndex();
        var builder = new LineIndexBuilder(data, TextEncodingKind.Utf8);

        builder.Build(index);
        Assert.Equal(3, index.KnownLineCount);
        Assert.True(index.IsComplete);

        // Simulate truncation/rotation: a shorter file replaces the old one.
        data.Replace(Encoding.UTF8.GetBytes("short\n"));
        index.Reset();
        Assert.Equal(0, index.KnownLineCount);
        Assert.False(index.IsComplete);
        Assert.Equal(0, index.CheckpointCount);

        builder.Build(index);
        Assert.Equal(1, index.KnownLineCount);
        var locator = new LineLocator(index, data, TextEncodingKind.Utf8);
        Assert.Equal("short", locator.GetLineText(0));
    }

    [Fact]
    public void DominantLineEndingAccumulatesAcrossContinueCalls()
    {
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("a\r\nb\r\n"));
        var index = new LineIndex();
        var builder = new LineIndexBuilder(data, TextEncodingKind.Utf8);

        builder.Build(index);
        Assert.Equal(LineEndingKind.CrLf, index.DominantLineEnding);

        data.Append(Encoding.UTF8.GetBytes("c\r\nd\r\n"));
        builder.Continue(index);

        Assert.Equal(LineEndingKind.CrLf, index.DominantLineEnding);
        Assert.Equal(4, index.KnownLineCount);
    }
}
