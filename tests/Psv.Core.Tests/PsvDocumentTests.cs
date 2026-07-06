using System.Text;

namespace Psv.Core.Tests;

public class PsvDocumentTests
{
    [Fact]
    public void OpenAutoDetectsUtf8NoBom()
    {
        string path = WriteTempFile("line1\nline2\nline3\n"u8.ToArray());
        try
        {
            using var doc = PsvDocument.Open(path);
            Assert.Equal(TextEncodingKind.Utf8, doc.Encoding);
            Assert.Equal(0, doc.BomLength);

            doc.BuildIndex();
            Assert.Equal(3, doc.Index.KnownLineCount);
            Assert.Equal("line2", doc.Locator.GetLineText(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenAutoDetectsWindows1252ForABomLessNonUtf8File()
    {
        // "café" written as Windows-1252 (é = 0xE9), the case the review flagged: BOM-less
        // non-UTF-8 files used to always default to UTF-8, decoding as mojibake until the user
        // manually cycled the encoding.
        byte[] content = [(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'\n'];
        string path = WriteTempFile(content);
        try
        {
            using var doc = PsvDocument.Open(path);
            Assert.Equal(TextEncodingKind.Windows1252, doc.Encoding);
            Assert.Equal(0, doc.BomLength);

            doc.BuildIndex();
            Assert.Equal("café", doc.Locator.GetLineText(0));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenAutoDetectsUtf16LeBomAndSkipsIt()
    {
        byte[] bom = [0xFF, 0xFE];
        byte[] content = Encoding.Unicode.GetBytes("hello\nworld");
        string path = WriteTempFile([.. bom, .. content]);
        try
        {
            using var doc = PsvDocument.Open(path);
            Assert.Equal(TextEncodingKind.Utf16LE, doc.Encoding);
            Assert.Equal(2, doc.BomLength);

            doc.BuildIndex();
            Assert.Equal(2, doc.Index.KnownLineCount);
            Assert.Equal("hello", doc.Locator.GetLineText(0));
            Assert.Equal("world", doc.Locator.GetLineText(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ForcedEncodingSkipsBomSniffingEntirely()
    {
        // Content that happens to start with UTF-8 BOM bytes, but we force ASCII, which must
        // treat those bytes as plain content, not a BOM to skip (plan §2.2.1).
        byte[] content = [0xEF, 0xBB, 0xBF, (byte)'x', (byte)'\n', (byte)'y'];
        string path = WriteTempFile(content);
        try
        {
            using var doc = PsvDocument.Open(path, TextEncodingKind.Ascii);
            Assert.Equal(TextEncodingKind.Ascii, doc.Encoding);
            Assert.Equal(0, doc.BomLength);

            doc.BuildIndex();
            Assert.Equal(2, doc.Index.KnownLineCount);
            // the "BOM" bytes decode as ASCII replacement characters, proving they were not skipped
            Assert.Equal(4, doc.Locator.GetLineText(0).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildIndexAsyncMakesIndexProgressivelyReadableBeforeCompletion()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50_000; i++)
        {
            sb.Append("line ").Append(i).Append('\n');
        }

        string path = WriteTempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = PsvDocument.Open(path);
            var task = doc.BuildIndexAsync();
            await task;

            Assert.True(doc.Index.IsComplete);
            Assert.Equal(50_000, doc.Index.KnownLineCount);
            Assert.Equal("line 0", doc.Locator.GetLineText(0));
            Assert.Equal("line 49999", doc.Locator.GetLineText(49_999));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildIndexAsyncHonorsAnAlreadyCancelledToken()
    {
        // Mirrors how MainWindow reuses this: it cancels the previous open's token before
        // starting a new one, and its continuation must be able to tell a cancelled build apart
        // from a completed one so it never starts tailing against a stale document.
        string path = WriteTempFile("line1\nline2\n"u8.ToArray());
        try
        {
            using var doc = PsvDocument.Open(path);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = doc.BuildIndexAsync(cts.Token);

            await Assert.ThrowsAsync<TaskCanceledException>(() => task);
            Assert.False(task.IsCompletedSuccessfully);
            Assert.False(doc.Index.IsComplete);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(byte[] content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }
}
