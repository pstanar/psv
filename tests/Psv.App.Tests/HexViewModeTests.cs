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
    public void NarrowingTheWindowRevealsTheHorizontalScrollBarForWideHexRows()
    {
        // Regression: BytesPerRow used to be a fixed 16, which always fit a normal window - once it
        // became configurable (32/64), nothing scrolled the hex/ASCII panes into view past the
        // right edge, and MainWindow's shared HScrollBar stayed forced off in hex mode.
        using var isolation = new SettingsIsolation();
        string path = WriteTempBinaryFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.Width = 1700;
                window.Height = 1400;
                window.Show();
                window.OpenFile(path);
                window.HexViewForTests.BytesPerRow = 64;

                window.Width = 300;
                window.Height = 600;

                Assert.True(window.IsHScrollBarVisibleForTests);
                Assert.True(window.HScrollBarMaximumForTests > 0);
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
    public void ChangingBytesPerRowRevealsTheVerticalScrollBarWithoutAFileLengthChange()
    {
        // Regression: OnProgressTick's vertical-scrollbar refresh used to be gated entirely on the
        // file's byte length changing (an optimization to skip redraw work on a static file) - but
        // row count depends on BytesPerRow too, which the View menu can change at any time with the
        // file's length completely unchanged. That left VScrollBar.IsVisible stuck at whatever it
        // was computed as before the change, even once the new row width plainly needed scrolling.
        using var isolation = new SettingsIsolation();
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[64 * 10]); // 10 rows at 64 bytes/row, 40 rows at 16 bytes/row.
        try
        {
            var window = new MainWindow();
            try
            {
                window.Width = 900;
                window.Height = 600;
                window.Show();
                window.CaptureRenderedFrame()?.Dispose();
                window.OpenFile(path, forceBinary: true);
                window.CaptureRenderedFrame()?.Dispose();

                window.HexViewForTests.BytesPerRow = 64;
                Assert.False(window.IsVScrollBarVisibleForTests, "test setup: expected 10 rows to fit without scrolling");

                window.HexViewForTests.BytesPerRow = 16;

                Assert.True(window.IsVScrollBarVisibleForTests);
                Assert.True(window.VScrollBarMaximumForTests > 0);
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
    public void OpeningWithForcedBytesPerRowSetsHexViewRowWidthAndTheMenuSelection()
    {
        // The --bin16/--bin32/--bin64 CLI flags plumb through to here as forcedBytesPerRow.
        using var isolation = new SettingsIsolation();
        string path = WriteTempTextFile();
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path, forcedEncoding: null, enableTailing: null, forceBinary: true, forcedBytesPerRow: 64);

                Assert.Equal(64, window.HexViewForTests.BytesPerRow);
                Assert.True(window.BytesPerRow64MenuItemCheckedForTests);
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
