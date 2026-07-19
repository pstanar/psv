using System.Diagnostics;

namespace Psv.App.Tests;

public class SettingsStoreTests
{
    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }

        public override void WriteLine(string? message) => Write(message);

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (message is not null)
            {
                Messages.Add(message);
            }
        }
    }

    [Fact]
    public void LoadReturnsDefaultsWhenNoFileExistsYet()
    {
        using var isolation = new SettingsIsolation();

        var settings = SettingsStore.Load();

        Assert.Equal(new AppSettings().WindowWidth, settings.WindowWidth);
        Assert.True(settings.ShowLineNumbers);
    }

    [Fact]
    public void SaveThenLoadRoundTripsAllFields()
    {
        using var isolation = new SettingsIsolation();

        var saved = new AppSettings
        {
            WindowX = 12,
            WindowY = 34,
            WindowWidth = 1024,
            WindowHeight = 768,
            WindowMaximized = true,
            ShowLineNumbers = false,
            ShowColumnRuler = false,
            WordWrap = true,
            ZebraStriping = false,
            FollowSystemTheme = false,
            TailingEnabled = true,
            HexBytesPerRow = 64,
            FontFamily = "Consolas",
            FontSize = 16,
            TextColor = "#FF112233",
            ZebraEvenColor = "#FF445566",
            ZebraOddColor = "#FF778899",
        };

        SettingsStore.Save(saved);
        var loaded = SettingsStore.Load();

        Assert.Equal(saved.WindowX, loaded.WindowX);
        Assert.Equal(saved.WindowY, loaded.WindowY);
        Assert.Equal(saved.WindowWidth, loaded.WindowWidth);
        Assert.Equal(saved.WindowHeight, loaded.WindowHeight);
        Assert.Equal(saved.WindowMaximized, loaded.WindowMaximized);
        Assert.Equal(saved.ShowLineNumbers, loaded.ShowLineNumbers);
        Assert.Equal(saved.ShowColumnRuler, loaded.ShowColumnRuler);
        Assert.Equal(saved.WordWrap, loaded.WordWrap);
        Assert.Equal(saved.ZebraStriping, loaded.ZebraStriping);
        Assert.Equal(saved.FollowSystemTheme, loaded.FollowSystemTheme);
        Assert.Equal(saved.TailingEnabled, loaded.TailingEnabled);
        Assert.Equal(saved.HexBytesPerRow, loaded.HexBytesPerRow);
        Assert.Equal(saved.FontFamily, loaded.FontFamily);
        Assert.Equal(saved.FontSize, loaded.FontSize);
        Assert.Equal(saved.TextColor, loaded.TextColor);
        Assert.Equal(saved.ZebraEvenColor, loaded.ZebraEvenColor);
        Assert.Equal(saved.ZebraOddColor, loaded.ZebraOddColor);
    }

    [Fact]
    public void LoadFallsBackToDefaultsWhenTheFileIsCorruptJson()
    {
        string path = Path.Combine(Path.GetTempPath(), $"psv-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not valid json");
        SettingsStore.OverridePathForTests = path;
        try
        {
            var settings = SettingsStore.Load();

            Assert.Equal(new AppSettings().WindowWidth, settings.WindowWidth);
        }
        finally
        {
            SettingsStore.OverridePathForTests = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadTracesAWarningWhenTheFileIsCorruptJsonInsteadOfFailingSilently()
    {
        string path = Path.Combine(Path.GetTempPath(), $"psv-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not valid json");
        SettingsStore.OverridePathForTests = path;
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            SettingsStore.Load();

            Assert.Contains(listener.Messages, m => m.Contains("failed to load settings", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            SettingsStore.OverridePathForTests = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveTracesAWarningWhenTheTargetPathCannotBeCreated()
    {
        // Point the settings "directory" at a path that's actually a file, so
        // Directory.CreateDirectory throws IOException instead of succeeding.
        string blockingFile = Path.Combine(Path.GetTempPath(), $"psv-settings-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blockingFile, "not a directory");
        string path = Path.Combine(blockingFile, "settings.json");
        SettingsStore.OverridePathForTests = path;
        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            SettingsStore.Save(new AppSettings());

            Assert.Contains(listener.Messages, m => m.Contains("failed to save settings", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            SettingsStore.OverridePathForTests = null;
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public void SaveDoesNotLeaveATempFileBehindWhenTheFinalRenameFails()
    {
        // The write-to-temp-then-rename succeeds at writing the temp file but fails at the rename
        // step because the destination is a directory, not a file - Move() can't replace that.
        string path = Path.Combine(Path.GetTempPath(), $"psv-settings-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        SettingsStore.OverridePathForTests = path;
        string tempPath = path + ".tmp";
        try
        {
            // AppSettings' default WindowX/WindowY is NaN (the "no saved position yet" sentinel -
            // never actually written by MainWindow.CaptureSettings() in real usage), which
            // JsonSerializer can't emit; use finite values so the write step itself succeeds and
            // it's genuinely the rename that fails.
            SettingsStore.Save(new AppSettings { WindowX = 0, WindowY = 0 });

            Assert.False(File.Exists(tempPath), "the intermediate .tmp file must be cleaned up after a failed save");
        }
        finally
        {
            SettingsStore.OverridePathForTests = null;
            File.Delete(tempPath);
            Directory.Delete(path);
        }
    }
}
