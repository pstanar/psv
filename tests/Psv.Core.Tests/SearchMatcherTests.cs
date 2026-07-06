using System.Diagnostics;

namespace Psv.Core.Tests;

public class SearchMatcherTests
{
    [Fact]
    public void SubstringCaseSensitiveDoesNotMatchDifferentCase()
    {
        var matcher = new SearchMatcher("Needle", SearchMode.Substring, caseSensitive: true);
        Assert.Null(matcher.Match("a needle in a haystack"));
        Assert.Equal((2, 6), matcher.Match("a Needle in a haystack"));
    }

    [Fact]
    public void SubstringCaseInsensitiveMatchesAnyCase()
    {
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);
        Assert.Equal((2, 6), matcher.Match("a NEEDLE in a haystack"));
    }

    [Fact]
    public void RegexMatchesPattern()
    {
        var matcher = new SearchMatcher(@"\d{3}-\d{4}", SearchMode.Regex, caseSensitive: true);
        var result = matcher.Match("call 555-1234 now");
        Assert.Equal((5, 8), result);
    }

    [Fact]
    public void InvalidRegexThrowsAtConstruction()
    {
        Assert.ThrowsAny<Exception>(() => new SearchMatcher("(unclosed", SearchMode.Regex, caseSensitive: true));
    }

    [Fact]
    public void EmptyPatternThrows()
    {
        Assert.Throws<ArgumentException>(() => new SearchMatcher("", SearchMode.Substring, caseSensitive: true));
    }

    [Fact]
    public void RegexTimeoutProtectsAgainstCatastrophicBacktrackingAndReturnsNoMatch()
    {
        var matcher = new SearchMatcher(@"(a+)+$", SearchMode.Regex, caseSensitive: true, regexTimeout: TimeSpan.FromMilliseconds(200));
        string evil = new string('a', 40) + "!";

        var sw = Stopwatch.StartNew();
        var result = matcher.Match(evil);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"expected the timeout to bound this, took {sw.Elapsed}");
    }
}
