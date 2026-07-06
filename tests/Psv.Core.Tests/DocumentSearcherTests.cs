using System.Text;

namespace Psv.Core.Tests;

public class DocumentSearcherTests
{
    private static DocumentSearcher Build(string content)
    {
        var source = new MutableByteSource(Encoding.UTF8.GetBytes(content));
        var index = new LineIndex();
        new LineIndexBuilder(source, TextEncodingKind.Utf8).Build(index);
        var locator = new LineLocator(index, source, TextEncodingKind.Utf8);
        return new DocumentSearcher(index, locator);
    }

    [Fact]
    public async Task FindNextFindsFirstOccurrenceFromStart()
    {
        var searcher = Build("alpha\nneedle here\nbeta\n");
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        var match = await searcher.FindNextAsync(matcher, 0, 0, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(1, match.Value.LineNumber);
        Assert.Equal(0, match.Value.Column);
    }

    [Fact]
    public async Task FindNextAdvancesPastCurrentMatchWhenStartingAtItsColumn()
    {
        var searcher = Build("needle needle needle\n");
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        var first = await searcher.FindNextAsync(matcher, 0, 0, CancellationToken.None);
        Assert.Equal(0, first!.Value.Column);

        var second = await searcher.FindNextAsync(matcher, first.Value.LineNumber, first.Value.Column + first.Value.Length, CancellationToken.None);
        Assert.Equal(7, second!.Value.Column);

        var third = await searcher.FindNextAsync(matcher, second.Value.LineNumber, second.Value.Column + second.Value.Length, CancellationToken.None);
        Assert.Equal(14, third!.Value.Column);
    }

    [Fact]
    public async Task FindNextWrapsAroundOnceFileIsComplete()
    {
        var searcher = Build("needle\nalpha\nbeta\n");
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        // Start searching from after the only match — should wrap around and find it again.
        var match = await searcher.FindNextAsync(matcher, 0, 6, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.LineNumber);
        Assert.Equal(0, match.Value.Column);
    }

    [Fact]
    public async Task FindNextReturnsNullWhenPatternDoesNotExistAnywhere()
    {
        var searcher = Build("alpha\nbeta\ngamma\n");
        var matcher = new SearchMatcher("zzz-not-there", SearchMode.Substring, caseSensitive: false);

        var match = await searcher.FindNextAsync(matcher, 0, 0, CancellationToken.None);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindPreviousFindsMatchBeforeCurrentPosition()
    {
        var searcher = Build("needle\nalpha\nbeta\n");
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        var match = await searcher.FindPreviousAsync(matcher, 2, 0, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.LineNumber);
    }

    [Fact]
    public async Task FindPreviousWrapsAroundToEndOnceFileIsComplete()
    {
        var searcher = Build("alpha\nbeta\nneedle\n");
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        var match = await searcher.FindPreviousAsync(matcher, 0, 0, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(2, match.Value.LineNumber);
    }

    [Fact]
    public async Task FindPreviousWrapsWithinASingleLineFile()
    {
        // Regression: wrapping used to exclude the boundary line entirely instead of just the
        // already-searched portion of it, so a single-line file could never wrap onto itself.
        var searcher = Build("zz needle stuff\n");
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        var match = await searcher.FindPreviousAsync(matcher, 0, 0, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.LineNumber);
        Assert.Equal(3, match.Value.Column);
    }

    [Fact]
    public async Task FindNextWaitsForLiveIndexingBeforeGivingUp()
    {
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("alpha\n"));
        var index = new LineIndex();
        var locator = new LineLocator(index, data, TextEncodingKind.Utf8);
        var searcher = new DocumentSearcher(index, locator);
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        // Simulate "indexing in progress": only line 0 confirmed, IsComplete still false.
        index.SeedInitialCheckpoint(0);
        index.AppendCheckpoint(new Checkpoint(1, 6), scannedByteOffset: 6, knownLineCount: 1);

        var searchTask = searcher.FindNextAsync(matcher, 0, 0, CancellationToken.None);

        await Task.Delay(150);
        Assert.False(searchTask.IsCompleted, "search should still be waiting for more content, not given up");

        // "Indexing" catches up with the line the search is looking for.
        data.Append(Encoding.UTF8.GetBytes("needle-here\n"));
        index.AppendCheckpoint(new Checkpoint(2, data.Length), scannedByteOffset: data.Length, knownLineCount: 2);
        index.Complete(2, data.Length, data.Length);

        var match = await searchTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(match);
        Assert.Equal(1, match.Value.LineNumber);
    }

    [Fact]
    public async Task SearchIsCancellable()
    {
        var data = new MutableByteSource(Encoding.UTF8.GetBytes("alpha\n"));
        var index = new LineIndex();
        var locator = new LineLocator(index, data, TextEncodingKind.Utf8);
        var searcher = new DocumentSearcher(index, locator);
        var matcher = new SearchMatcher("needle", SearchMode.Substring, caseSensitive: false);

        index.SeedInitialCheckpoint(0);
        index.AppendCheckpoint(new Checkpoint(1, 6), scannedByteOffset: 6, knownLineCount: 1);

        using var cts = new CancellationTokenSource();
        var searchTask = searcher.FindNextAsync(matcher, 0, 0, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searchTask);
    }
}
