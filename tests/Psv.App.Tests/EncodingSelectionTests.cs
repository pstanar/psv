using System.Text;
using Avalonia.Headless.XUnit;
using Psv.Core;

namespace Psv.App.Tests;

public class EncodingSelectionTests
{
    private static string WriteTempFile(byte[] content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        condition();
    }

    [AvaloniaFact]
    public async Task SelectingAnEncodingDirectlyAppliesItAndMarksItManual()
    {
        // The point of direct selection: jump straight to Windows-1252 without cycling through
        // UTF-16LE/UTF-16BE/ASCII first.
        using var isolation = new SettingsIsolation();
        string path = WriteTempFile("alpha\nbeta\n"u8.ToArray());
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                await WaitUntilAsync(() => window.DocumentForTests?.Index.IsComplete == true, TimeSpan.FromSeconds(5));
                Assert.Equal(TextEncodingKind.Utf8, window.DocumentForTests!.Encoding);
                Assert.False(window.DocumentForTests!.IsManualEncoding);

                await window.SelectEncodingForTests(TextEncodingKind.Windows1252);

                Assert.Equal(TextEncodingKind.Windows1252, window.DocumentForTests!.Encoding);
                Assert.True(window.DocumentForTests!.IsManualEncoding);
                Assert.Contains("manual", window.StatusEncodingTextForTests, StringComparison.OrdinalIgnoreCase);
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
    public async Task SelectingAnEncodingAcrossTheUtf16BoundaryRebuildsTheIndexAndResetsScrollPosition()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempFile(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                await WaitUntilAsync(() => window.DocumentForTests?.Index.IsComplete == true, TimeSpan.FromSeconds(5));

                await window.SelectEncodingForTests(TextEncodingKind.Utf16LE);

                Assert.Equal(TextEncodingKind.Utf16LE, window.DocumentForTests!.Encoding);
                Assert.True(window.DocumentForTests!.Index.IsComplete);
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
    public async Task SelectingTheAlreadyActiveEncodingIsANoOp()
    {
        using var isolation = new SettingsIsolation();
        string path = WriteTempFile("alpha\nbeta\n"u8.ToArray());
        try
        {
            var window = new MainWindow();
            try
            {
                window.OpenFile(path);
                await WaitUntilAsync(() => window.DocumentForTests?.Index.IsComplete == true, TimeSpan.FromSeconds(5));

                var exception = await Record.ExceptionAsync(() => window.SelectEncodingForTests(TextEncodingKind.Utf8));

                Assert.Null(exception);
                Assert.Equal(TextEncodingKind.Utf8, window.DocumentForTests!.Encoding);
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
