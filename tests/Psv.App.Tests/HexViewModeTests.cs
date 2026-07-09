using Avalonia.Headless;
using Avalonia.Headless.XUnit;

namespace Psv.App.Tests;

public class HexViewModeTests
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

    // Real PE header prefix - decisive as binary via its embedded NUL bytes (BinaryContentDetector).
    private static readonly byte[] BinaryHeader =
        [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00];

    private static string WriteTempBinaryFile()
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, BinaryHeader);
        return path;
    }

    private static string WriteTempTextFile()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "line0\nline1\nline2\n");
        return path;
    }

    [AvaloniaFact]
    public void OpeningABinaryFileShowsHexViewAndHidesDocumentView()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempBinaryFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);

                Assert.True(window.DocumentForTests?.IsBinary);
                Assert.True(window.IsHexViewActiveForTests);
                Assert.True(window.IsHexViewMenuCheckedForTests);

                // Find/Go To Line/Cycle Encoding all operate on the line index a binary document
                // never builds - disabled rather than silently doing nothing.
                Assert.False(window.IsFindMenuEnabledForTests);
                Assert.False(window.IsGoToLineMenuEnabledForTests);
                Assert.False(window.IsCycleEncodingMenuEnabledForTests);
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
    public void OpeningATextFileShowsDocumentViewWithMenuItemsEnabled()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempTextFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);

                Assert.False(window.DocumentForTests?.IsBinary);
                Assert.False(window.IsHexViewActiveForTests);
                Assert.False(window.IsHexViewMenuCheckedForTests);
                Assert.True(window.IsFindMenuEnabledForTests);
                Assert.True(window.IsGoToLineMenuEnabledForTests);
                Assert.True(window.IsCycleEncodingMenuEnabledForTests);
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
    public async Task CtrlBTogglesBetweenViewsForTheSameOpenFile()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempTextFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                bool completed = await WaitUntilAsync(() => window.DocumentForTests?.Index.IsComplete == true, TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the initial index build to complete");
                Assert.False(window.IsHexViewActiveForTests);

                window.ToggleHexViewForTests();

                Assert.True(window.IsHexViewActiveForTests);
                Assert.True(window.DocumentForTests?.IsBinary);

                window.ToggleHexViewForTests();

                Assert.False(window.IsHexViewActiveForTests);
                Assert.False(window.DocumentForTests?.IsBinary);
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
    public void OpeningABinaryFileGivesTheHexViewKeyboardFocus()
    {
        // Regression: nothing focused HexV on open - DocView only ever got focus implicitly by
        // being the first focusable control - so a file opened straight into hex mode left every
        // navigation key dead until the user clicked inside the view.
        using var isolation = new SettingsIsolation();
        string path = WriteTempBinaryFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                window.OpenFile(path);

                Assert.True(window.IsHexViewActiveForTests);
                Assert.True(window.HexViewForTests.IsFocused, "expected the hex view to receive keyboard focus on open");
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
    public async Task TogglingHexViewMovesKeyboardFocusToTheVisibleView()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempTextFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.Show();
                window.OpenFile(path);
                bool completed = await WaitUntilAsync(() => window.DocumentForTests?.Index.IsComplete == true, TimeSpan.FromSeconds(5));
                Assert.True(completed, "expected the initial index build to complete");

                window.ToggleHexViewForTests();
                Assert.True(window.HexViewForTests.IsFocused, "expected focus to follow the toggle into hex view");

                window.ToggleHexViewForTests();
                Assert.True(window.DocumentViewForTests.IsFocused, "expected focus to follow the toggle back to the text view");
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
    public void OpeningWithBinCliFlagForcesHexViewEvenForTextContent()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempTextFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path, forcedEncoding: null, enableTailing: null, forceBinary: true);

                Assert.True(window.DocumentForTests?.IsBinary);
                Assert.True(window.IsHexViewActiveForTests);
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
    public async Task LiveTailInHexModeUpdatesSizeWithoutAnyIndexingStatusText()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempBinaryFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                Assert.True(window.DocumentForTests?.IsBinary);

                window.SetTailingEnabledForTests(true);
                bool startedTailing = await WaitUntilAsync(() => window.DocumentForTests!.IsTailingForTests, TimeSpan.FromSeconds(5));
                Assert.True(startedTailing, "expected tailing to start immediately for a binary document (no index build to race)");

                using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    append.Write([0x01, 0x02, 0x03, 0x04]);
                }

                bool grew = await WaitUntilAsync(
                    () => window.DocumentForTests!.FileSizeBytes == BinaryHeader.Length + 4,
                    TimeSpan.FromSeconds(5));
                Assert.True(grew, $"expected {BinaryHeader.Length + 4} bytes, got {window.DocumentForTests!.FileSizeBytes}");

                Assert.DoesNotContain("Indexing", window.StatusStateTextForTests);
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
