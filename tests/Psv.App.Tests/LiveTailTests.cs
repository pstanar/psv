using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

namespace Psv.App.Tests;

public class LiveTailTests
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
    public async Task TailingIsDisabledByDefaultAfterOpeningAFile()
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

                // Give a would-be tail watcher time to have started if the default were wrong.
                await Task.Delay(200);

                Assert.False(window.TailingEnabledForTests);
                Assert.False(window.DocumentForTests!.IsTailingForTests);
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
    public async Task EnablingLiveTailAfterIndexingCompletesStartsTailingAndPicksUpGrowth()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(2);
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

                window.SetTailingEnabledForTests(true);

                Assert.True(window.TailingEnabledForTests);
                Assert.True(window.DocumentForTests!.IsTailingForTests);

                using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(append))
                {
                    writer.Write("line2\n");
                }

                bool grew = await WaitUntilAsync(
                    () => window.DocumentForTests!.Index.KnownLineCount == 3,
                    TimeSpan.FromSeconds(5));
                Assert.True(grew, "expected the appended line to be picked up once tailing was enabled");
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
    public async Task EnablingLiveTailJumpsTheViewToTheEndOfTheFile()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(500);
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

                Assert.Equal(0, window.TopLineForTests);

                window.SetTailingEnabledForTests(true);

                Assert.True(window.TopLineForTests > 0, "expected enabling live tail to scroll the view down");
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
    public async Task OpenFileWithTailOverrideJumpsTheViewToTheEndOfTheFile()
    {
        // Mirrors EnablingLiveTailJumpsTheViewToTheEndOfTheFile, but for the --tail CLI switch
        // path: the jump must happen on open, not only when toggled on later via the View menu.
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(500);
        try
        {
            var window = new MainWindow();
            try
            {
                window.Show();

                window.OpenFile(path, forcedEncoding: null, enableTailing: true);

                bool jumped = await WaitUntilAsync(() => window.TopLineForTests > 0, TimeSpan.FromSeconds(5));
                Assert.True(jumped, "expected --tail to scroll the view down once indexing completed");
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
    public async Task DisablingLiveTailStopsTailingOnTheCurrentDocument()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(2);
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                await WaitUntilAsync(() => window.DocumentForTests?.Index.IsComplete == true, TimeSpan.FromSeconds(5));

                window.SetTailingEnabledForTests(true);
                Assert.True(window.DocumentForTests!.IsTailingForTests);

                window.SetTailingEnabledForTests(false);

                Assert.False(window.TailingEnabledForTests);
                Assert.False(window.DocumentForTests!.IsTailingForTests);
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
    public async Task EnablingLiveTailWhileIndexingIsStillInProgressStartsTailingOnceItCompletes()
    {
        // Toggling the checkbox on before the initial build finishes must not call StartTailing()
        // immediately (that would race the still-running Build() against Continue()) - it should
        // only take effect once OpenFile()'s own completion continuation checks the flag.
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(200_000);
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                Assert.False(window.DocumentForTests!.Index.IsComplete, "test assumes indexing is still running");

                window.SetTailingEnabledForTests(true);
                Assert.False(window.DocumentForTests!.IsTailingForTests, "must not start tailing before the index build completes");

                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the initial index build to complete");

                bool startedTailing = await WaitUntilAsync(() => window.DocumentForTests!.IsTailingForTests, TimeSpan.FromSeconds(5));
                Assert.True(startedTailing, "expected tailing to start once the deferred build completed");
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
    public async Task OpenFileWithTailOverrideEnablesTailingForThatOpen()
    {
        // Mirrors what App.axaml.cs does for the --tail CLI switch.
        using var isolation = new SettingsIsolation();
        string path = WriteTempFileWithLines(2);
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path, forcedEncoding: null, enableTailing: true);

                bool completed = await WaitUntilAsync(
                    () => window.DocumentForTests?.Index.IsComplete == true,
                    TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the initial index build to complete");

                bool startedTailing = await WaitUntilAsync(() => window.DocumentForTests!.IsTailingForTests, TimeSpan.FromSeconds(5));
                Assert.True(startedTailing, "expected --tail's enableTailing override to start tailing");
                Assert.True(window.TailingEnabledForTests);
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
}
