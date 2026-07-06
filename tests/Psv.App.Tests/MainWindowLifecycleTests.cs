using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

namespace Psv.App.Tests;

public class MainWindowLifecycleTests
{
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static string WriteTempFileWithLines(int lineCount)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lineCount; i++)
        {
            sb.Append("line ").Append(i).Append('\n');
        }

        string path = Path.GetTempFileName();
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [AvaloniaFact]
    public async Task OpenFileCompletesIndexingWithoutThrowing()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(3);
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);

                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));

                Assert.True(completed, "expected the initial index build to complete");
                Assert.Equal(3, window.DocumentForTests!.Index.KnownLineCount);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulIndexingDisposesTheIndexCancellationTokenSource()
    {
        // Regression test: _indexCts used to be replaced/cancelled on every OpenFile() call but
        // never disposed on the normal success path, leaking one CancellationTokenSource per file
        // opened for the lifetime of the window.
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(3);
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);

                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the initial index build to complete");

                // Disposal happens via a Dispatcher.Post from the background continuation, so it
                // can lag slightly behind Index.IsComplete becoming true.
                bool disposed = await WaitUntilAsync(() => !window.HasIndexCtsForTests, TimeSpan.FromSeconds(5));
                Assert.True(disposed, "expected _indexCts to be disposed and cleared after a successful build");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task RapidReopenLeavesTheSecondFileFullyAndCorrectlyIndexed()
    {
        // Opens a second (small) file before the first (large) file's background index build has
        // a chance to finish. Doesn't reproduce the exact original bug - that manifested as an
        // unobserved exception on a stale background task, not corrupted end state - but it is a
        // real exercise of the OpenFile() cancel-then-reopen path and guards against a future
        // change reintroducing cross-talk between the old and new document.
        using var isolation = new SettingsIsolation();
        string pathA = WriteTempFileWithLines(50_000);
        string pathB = WriteTempFileWithLines(2);
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(pathA);
                window.OpenFile(pathB);

                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));

                Assert.True(completed, "expected file B's index build to complete");
                Assert.Equal(2, window.DocumentForTests!.Index.KnownLineCount);
                Assert.Equal("line 0", window.DocumentForTests!.Locator.GetLineText(0));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [AvaloniaFact]
    public void OpeningANonExistentFileAsTheFirstOpenDoesNotThrowAndSurfacesAnError()
    {
        using var isolation = new SettingsIsolation();
        string path = Path.Combine(Path.GetTempPath(), $"psv-does-not-exist-{Guid.NewGuid():N}.log");

        var window = new MainWindow();
        try
        {
            var exception = Record.Exception(() => window.OpenFile(path));

            Assert.Null(exception);
            Assert.Null(window.DocumentForTests);
            Assert.Contains("Failed to open", window.StatusStateTextForTests);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task OpeningANonExistentFileLeavesTheCurrentlyOpenDocumentUntouched()
    {
        // The bug: OpenFile() used to dispose the current document before confirming the new path
        // could even be opened, so a failed reopen destroyed a perfectly good view instead of
        // leaving it alone.
        using var isolation = new SettingsIsolation();
        string goodPath = WriteTempFileWithLines(3);
        string badPath = Path.Combine(Path.GetTempPath(), $"psv-does-not-exist-{Guid.NewGuid():N}.log");
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(goodPath);
                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the good file's index build to complete");

                var documentBeforeFailedOpen = window.DocumentForTests;

                var exception = Record.Exception(() => window.OpenFile(badPath));

                Assert.Null(exception);
                Assert.Same(documentBeforeFailedOpen, window.DocumentForTests);
                Assert.Contains("Failed to open", window.StatusStateTextForTests);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(goodPath);
        }
    }

    [AvaloniaFact]
    public async Task StatusBarShowsLineAndColumnRangesAfterOpeningAFile()
    {
        // Regression: the status bar's "Col" field used to just report the leftmost visible
        // column (always 1 for files that fit the window, which is nearly always), with no
        // indication of how far right there was to scroll - showing the reachable range makes it
        // meaningful even before the user has scrolled at all.
        using var isolation = new SettingsIsolation();
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "short\nmedium line\nlast\n");
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);

                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the initial index build to complete");

                window.Show();
                window.CaptureRenderedFrame()?.Dispose();

                // "medium line" is the longest of the three lines (11 characters), so that's the
                // rightmost column reachable by scrolling.
                bool statusShowsColumnRange = await WaitUntilAsync(
                    () => window.StatusPositionTextForTests.Contains("Col 1 / 11"),
                    TimeSpan.FromSeconds(5));

                Assert.True(statusShowsColumnRange, $"expected a 'Col 1 / 11' range, got '{window.StatusPositionTextForTests}'");
                Assert.StartsWith("Line 1 / 3, ", window.StatusPositionTextForTests);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void ClosingWindowWhileTheInitialIndexIsStillBuildingDoesNotThrow()
    {
        // Only checks that Close() itself doesn't throw synchronously on the UI thread; it can't
        // observe whether the cancelled background build's continuation behaved (that's covered
        // directly by PsvDocumentTests.BuildIndexAsyncHonorsAnAlreadyCancelledToken and
        // PsvDocumentTailingTests.StartTailingAfterDisposeIsANoOp in Psv.Core.Tests).
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(200_000);
        try
        {
            var window = new MainWindow();
            window.OpenFile(path);

            var exception = Record.Exception(() => window.Close());

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
