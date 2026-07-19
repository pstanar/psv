using Psv.Core;

namespace Psv.App.Tests;

public class CliArgsParserTests
{
    [Fact]
    public void ParsesABarePathWithNoEncoding()
    {
        bool ok = CliArgsParser.TryParse(["file.log"], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("file.log", parsed.Path);
        Assert.Null(parsed.Encoding);
    }

    [Fact]
    public void ParsesPathAndEncodingTogetherRegardlessOfOrder()
    {
        bool ok = CliArgsParser.TryParse(["--encoding=utf-16le", "file.log"], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("file.log", parsed.Path);
        Assert.Equal(TextEncodingKind.Utf16LE, parsed.Encoding);
    }

    [Fact]
    public void EncodingFlagIsCaseInsensitive()
    {
        bool ok = CliArgsParser.TryParse(["--ENCODING=Windows-1252", "file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(TextEncodingKind.Windows1252, parsed.Encoding);
    }

    [Fact]
    public void UnrecognizedEncodingFailsWithAHelpfulError()
    {
        bool ok = CliArgsParser.TryParse(["--encoding=bogus", "file.log"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("bogus", error);
        Assert.Contains("utf-8", error);
    }

    [Fact]
    public void FirstNonFlagArgumentWinsWhenMultiplePathsAreGiven()
    {
        bool ok = CliArgsParser.TryParse(["first.log", "second.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal("first.log", parsed.Path);
    }

    [Fact]
    public void UnknownFlagsAreIgnoredRatherThanTreatedAsAPath()
    {
        bool ok = CliArgsParser.TryParse(["--unknown-flag", "file.log"], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("file.log", parsed.Path);
    }

    [Fact]
    public void NoArgumentsProducesAnEmptyResult()
    {
        bool ok = CliArgsParser.TryParse([], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Null(parsed.Path);
        Assert.Null(parsed.Encoding);
        Assert.False(parsed.Tail);
    }

    [Fact]
    public void TailIsDisabledByDefault()
    {
        bool ok = CliArgsParser.TryParse(["file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.False(parsed.Tail);
    }

    [Fact]
    public void TailFlagEnablesTailingRegardlessOfArgumentOrder()
    {
        bool ok = CliArgsParser.TryParse(["--tail", "file.log"], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("file.log", parsed.Path);
        Assert.True(parsed.Tail);
    }

    [Fact]
    public void TailFlagIsCaseInsensitive()
    {
        bool ok = CliArgsParser.TryParse(["--TAIL", "file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.True(parsed.Tail);
    }

    [Fact]
    public void TailFlagCombinesWithEncoding()
    {
        bool ok = CliArgsParser.TryParse(["--encoding=utf-16le", "--tail", "file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(TextEncodingKind.Utf16LE, parsed.Encoding);
        Assert.True(parsed.Tail);
        Assert.Equal("file.log", parsed.Path);
    }

    [Fact]
    public void BinFlagIsDisabledByDefault()
    {
        bool ok = CliArgsParser.TryParse(["file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.Null(parsed.BinBytesPerRow);
    }

    [Theory]
    [InlineData("--bin16", 16)]
    [InlineData("--bin32", 32)]
    [InlineData("--bin64", 64)]
    public void BinFlagEnablesForcedHexViewAtTheRequestedRowWidthRegardlessOfArgumentOrder(string flag, int expectedBytesPerRow)
    {
        bool ok = CliArgsParser.TryParse([flag, "file.exe"], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("file.exe", parsed.Path);
        Assert.Equal(expectedBytesPerRow, parsed.BinBytesPerRow);
    }

    [Theory]
    [InlineData("--BIN16", 16)]
    [InlineData("--Bin32", 32)]
    [InlineData("--BIN64", 64)]
    public void BinFlagIsCaseInsensitive(string flag, int expectedBytesPerRow)
    {
        bool ok = CliArgsParser.TryParse([flag, "file.exe"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(expectedBytesPerRow, parsed.BinBytesPerRow);
    }

    [Fact]
    public void BinFlagCombinesWithEncodingAndTail()
    {
        bool ok = CliArgsParser.TryParse(["--encoding=utf-16le", "--tail", "--bin32", "file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(TextEncodingKind.Utf16LE, parsed.Encoding);
        Assert.True(parsed.Tail);
        Assert.Equal(32, parsed.BinBytesPerRow);
        Assert.Equal("file.log", parsed.Path);
    }

    [Fact]
    public void LastBinFlagWinsWhenMultipleAreGiven()
    {
        bool ok = CliArgsParser.TryParse(["--bin16", "--bin64", "file.exe"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(64, parsed.BinBytesPerRow);
    }
}
