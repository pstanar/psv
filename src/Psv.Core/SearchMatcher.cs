using System.Text.RegularExpressions;

namespace Psv.Core;

/// <summary>
/// Tests a single decoded line against a substring or regex pattern (plan §2.6). Regex is bounded
/// by a match timeout so a catastrophic-backtracking pattern can't hang the search — a timeout is
/// treated as "no match on this line" rather than propagated, so one pathological line doesn't
/// abort the whole search.
/// </summary>
public sealed class SearchMatcher
{
    private static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromSeconds(2);

    private readonly SearchMode _mode;
    private readonly string _pattern;
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;

    public SearchMatcher(string pattern, SearchMode mode, bool caseSensitive, TimeSpan? regexTimeout = null)
    {
        if (pattern.Length == 0)
        {
            throw new ArgumentException("Search pattern must not be empty.", nameof(pattern));
        }

        _mode = mode;
        _pattern = pattern;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (mode == SearchMode.Regex)
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            _regex = new Regex(pattern, options, regexTimeout ?? DefaultRegexTimeout);
        }
    }

    /// <summary>Finds the first match in <paramref name="text"/>, or null if there isn't one.</summary>
    public (int Start, int Length)? Match(string text)
    {
        if (_mode == SearchMode.Substring)
        {
            int index = text.IndexOf(_pattern, _comparison);
            return index >= 0 ? (index, _pattern.Length) : null;
        }

        try
        {
            var m = _regex!.Match(text);
            return m.Success ? (m.Index, m.Length) : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
