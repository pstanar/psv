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
        Assert.False(parsed.Bin);
    }

    [Fact]
    public void BinFlagEnablesForcedHexViewRegardlessOfArgumentOrder()
    {
        bool ok = CliArgsParser.TryParse(["--bin", "file.exe"], out var parsed, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("file.exe", parsed.Path);
        Assert.True(parsed.Bin);
    }

    [Fact]
    public void BinFlagIsCaseInsensitive()
    {
        bool ok = CliArgsParser.TryParse(["--BIN", "file.exe"], out var parsed, out _);

        Assert.True(ok);
        Assert.True(parsed.Bin);
    }

    [Fact]
    public void BinFlagCombinesWithEncodingAndTail()
    {
        bool ok = CliArgsParser.TryParse(["--encoding=utf-16le", "--tail", "--bin", "file.log"], out var parsed, out _);

        Assert.True(ok);
        Assert.Equal(TextEncodingKind.Utf16LE, parsed.Encoding);
        Assert.True(parsed.Tail);
        Assert.True(parsed.Bin);
        Assert.Equal("file.log", parsed.Path);
    }
}
