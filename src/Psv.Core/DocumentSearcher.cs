namespace Psv.Core;

/// <summary>
/// Searches a document forward or backward from a given position (plan §2.6). Forward search
/// runs "live, as-you-scan": if it reaches the currently-known end of the file while indexing or
/// tailing is still in progress, it waits for more lines rather than reporting "not found"
/// prematurely. Both directions wrap around exactly once, and only once the file is fully known —
/// wrapping before that would make "not found" unstable as more of the file gets scanned.
/// Lines are fetched in batches via <see cref="LineLocator.GetLineRanges"/>: resolving them one
/// at a time re-scans from the nearest checkpoint for every line, which turns a full-file search
/// into checkpoint-interval × line-count work (measured ~15s for a 200k-line file before batching).
/// </summary>
public sealed class DocumentSearcher(LineIndex index, LineLocator locator)
{
    private static readonly TimeSpan LivePollInterval = TimeSpan.FromMilliseconds(50);

    private const int SearchBatchSize = 256;

    public async Task<SearchMatch?> FindNextAsync(SearchMatcher matcher, long fromLine, int fromColumn, CancellationToken cancellationToken)
    {
        long searchStartLine = Math.Max(0, fromLine);

        var match = await ScanForwardAsync(matcher, searchStartLine, fromColumn, stopAtLine: null, stopBeforeColumn: 0, cancellationToken);
        if (match is not null)
        {
            return match;
        }

        if (!index.IsComplete)
        {
            return null;
        }

        // Wrap around: search from the top back up to (and including, up to the original column)
        // the starting line — not "before the starting line", which would skip it entirely when
        // the search began at line 0.
        return await ScanForwardAsync(matcher, 0, 0, stopAtLine: searchStartLine, stopBeforeColumn: fromColumn, cancellationToken);
    }

    public async Task<SearchMatch?> FindPreviousAsync(SearchMatcher matcher, long fromLine, int beforeColumn, CancellationToken cancellationToken)
    {
        long searchStartLine = fromLine;

        var match = ScanBackward(matcher, fromLine, beforeColumn, stopAtLine: null, stopAfterColumn: 0, cancellationToken);
        if (match is not null)
        {
            return match;
        }

        if (!index.IsComplete)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return ScanBackward(matcher, index.KnownLineCount - 1, int.MaxValue, stopAtLine: searchStartLine, stopAfterColumn: beforeColumn, cancellationToken);
    }

    private async Task<SearchMatch?> ScanForwardAsync(
        SearchMatcher matcher, long fromLine, int fromColumn, long? stopAtLine, int stopBeforeColumn, CancellationToken cancellationToken)
    {
        long line = fromLine;
        int column = fromColumn;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stopAtLine is { } stop && line > stop)
            {
                return null;
            }

            if (line >= index.KnownLineCount)
            {
                if (index.IsComplete)
                {
                    return null;
                }

                await Task.Delay(LivePollInterval, cancellationToken);
                continue;
            }

            long batchEndExclusive = Math.Min(line + SearchBatchSize, index.KnownLineCount);
            if (stopAtLine is { } stopLine)
            {
                batchEndExclusive = Math.Min(batchEndExclusive, stopLine + 1);
            }

            var ranges = locator.GetLineRanges(line, (int)(batchEndExclusive - line));
            if (ranges.Count == 0)
            {
                if (index.IsComplete)
                {
                    return null;
                }

                await Task.Delay(LivePollInterval, cancellationToken);
                continue;
            }

            foreach (var range in ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string text = locator.DecodeLine(range);
                int upperBound = stopAtLine == line ? Math.Min(stopBeforeColumn, text.Length) : text.Length;

                if (column < upperBound)
                {
                    string searchable = text[column..upperBound];
                    if (searchable.Length > 0 && matcher.Match(searchable) is { } m)
                    {
                        return new SearchMatch(line, column + m.Start, m.Length);
                    }
                }

                if (stopAtLine == line)
                {
                    return null;
                }

                line++;
                column = 0;
            }
        }
    }

    private SearchMatch? ScanBackward(
        SearchMatcher matcher, long fromLine, int beforeColumnExclusive, long? stopAtLine, int stopAfterColumn, CancellationToken cancellationToken)
    {
        long line = Math.Min(fromLine, index.KnownLineCount - 1);

        while (line >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stopAtLine is { } stop && line < stop)
            {
                return null;
            }

            long batchStart = Math.Max(0, line - SearchBatchSize + 1);
            if (stopAtLine is { } stopLine)
            {
                batchStart = Math.Max(batchStart, stopLine);
            }

            var ranges = locator.GetLineRanges(batchStart, (int)(line - batchStart + 1));

            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long currentLine = batchStart + i;
                string text = locator.DecodeLine(ranges[i]);
                int lowerBound = stopAtLine == currentLine ? Math.Min(stopAfterColumn, text.Length) : 0;
                int upperBound = beforeColumnExclusive < text.Length ? Math.Max(lowerBound, beforeColumnExclusive) : text.Length;

                if (lowerBound < upperBound)
                {
                    string searchable = text[lowerBound..upperBound];
                    if (searchable.Length > 0 && FindLastMatch(matcher, searchable) is { } m)
                    {
                        return new SearchMatch(currentLine, lowerBound + m.Start, m.Length);
                    }
                }

                if (stopAtLine == currentLine)
                {
                    return null;
                }

                beforeColumnExclusive = int.MaxValue;
            }

            line = batchStart - 1;
        }

        return null;
    }

    private static (int Start, int Length)? FindLastMatch(SearchMatcher matcher, string text)
    {
        (int Start, int Length)? last = null;
        int searchFrom = 0;

        while (searchFrom <= text.Length)
        {
            string remaining = text[searchFrom..];
            var m = matcher.Match(remaining);
            if (m is null)
            {
                break;
            }

            last = (searchFrom + m.Value.Start, m.Value.Length);
            searchFrom += m.Value.Start + Math.Max(1, m.Value.Length);
        }

        return last;
    }
}
