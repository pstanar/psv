using System.Text;

namespace Psv.Core.Tests;

public class PsvDocumentEncodingChangeTests
{
    private static string WriteTempFile(byte[] content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task ChangingWithinSingleByteFamilyDoesNotRebuildIndex()
    {
        string path = WriteTempFile("café\n"u8.ToArray());
        try
        {
            using var doc = PsvDocument.Open(path, TextEncodingKind.Utf8);
            doc.BuildIndex();
            int checkpointsBefore = doc.Index.CheckpointCount;
            long linesBefore = doc.Index.KnownLineCount;

            bool rebuilt = await doc.ChangeEncodingAsync(TextEncodingKind.Windows1252);

            Assert.False(rebuilt);
            Assert.Equal(TextEncodingKind.Windows1252, doc.Encoding);
            Assert.Equal(linesBefore, doc.Index.KnownLineCount);
            Assert.Equal(checkpointsBefore, doc.Index.CheckpointCount);
            Assert.True(doc.IsManualEncoding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChangeEncodingDisposesItsCancellationTokenSourceOnCompletion()
    {
        // Regression test: _reindexCts used to be replaced/cancelled on every width-changing call
        // but never disposed, leaking a CancellationTokenSource per rebuild over the app's
        // lifetime (e.g. repeatedly cycling encoding with Ctrl+Shift+E).
        string path = WriteTempFile(Encoding.UTF8.GetBytes("alpha\nbeta\n"));
        try
        {
            using var doc = PsvDocument.Open(path, TextEncodingKind.Utf8);
            doc.BuildIndex();

            bool rebuilt = await doc.ChangeEncodingAsync(TextEncodingKind.Utf16LE);

            Assert.True(rebuilt);
            Assert.False(doc.HasReindexCtsForTests, "the CancellationTokenSource must be cleared once the rebuild completes");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChangingAcrossUtf16BoundaryRebuildsIndexWithNewEncoding()
    {
        string path = WriteTempFile(Encoding.UTF8.GetBytes("alpha\nbeta\ngamma\n"));
        try
        {
            using var doc = PsvDocument.Open(path, TextEncodingKind.Utf8);
            doc.BuildIndex();
            Assert.Equal(3, doc.Index.KnownLineCount);

            bool rebuilt = await doc.ChangeEncodingAsync(TextEncodingKind.Utf16LE);

            Assert.True(rebuilt);
            Assert.Equal(TextEncodingKind.Utf16LE, doc.Encoding);
            Assert.True(doc.Index.IsComplete);
            // UTF-8 bytes reinterpreted as UTF-16LE produce garbled but deterministic content —
            // what matters is that the index reflects a real rebuild under the new width, not a
            // crash or stale UTF-8-derived state.
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChangeEncodingResetsBomLengthToZero()
    {
        byte[] bom = [0xFF, 0xFE];
        byte[] content = Encoding.Unicode.GetBytes("hello\nworld");
        string path = WriteTempFile([.. bom, .. content]);
        try
        {
            using var doc = PsvDocument.Open(path); // auto-detects UTF-16LE, BomLength == 2
            doc.BuildIndex();
            Assert.Equal(2, doc.BomLength);

            await doc.ChangeEncodingAsync(TextEncodingKind.Utf8);

            Assert.Equal(0, doc.BomLength);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RapidSuccessiveEncodingChangesCancelPreviousRebuildRatherThanRace()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 20_000; i++)
        {
            sb.Append("line ").Append(i).Append('\n');
        }

        string path = WriteTempFile(Encoding.UTF8.GetBytes(sb.ToString()));
        try
        {
            using var doc = PsvDocument.Open(path, TextEncodingKind.Utf8);
            doc.BuildIndex();

            // Fire several width-crossing changes back-to-back without awaiting each one — this
            // is the "holding the cycle key down" scenario the plan calls out. None should throw,
            // and the final state should be internally consistent (complete, matching the last
            // requested encoding).
            var t1 = doc.ChangeEncodingAsync(TextEncodingKind.Utf16LE);
            var t2 = doc.ChangeEncodingAsync(TextEncodingKind.Utf8);
            var t3 = doc.ChangeEncodingAsync(TextEncodingKind.Utf16BE);

            await Task.WhenAll(t1, t2, t3);

            Assert.Equal(TextEncodingKind.Utf16BE, doc.Encoding);
            Assert.True(doc.Index.IsComplete);
            Assert.False(doc.HasReindexCtsForTests, "every superseded call's CancellationTokenSource must still get disposed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ChangeEncodingDuringActiveTailingDoesNotCorruptIndex()
    {
        string path = WriteTempFile(Encoding.UTF8.GetBytes("line1\nline2\n"));
        try
        {
            using var doc = PsvDocument.Open(path, TextEncodingKind.Utf8);
            doc.BuildIndex();
            doc.StartTailing(TimeSpan.FromMilliseconds(50));

            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(append))
            {
                writer.Write("line3\n");
            }

            // Change encoding while a tail catch-up could plausibly be in flight.
            bool rebuilt = await doc.ChangeEncodingAsync(TextEncodingKind.Utf16LE);

            Assert.True(rebuilt);
            Assert.True(doc.Index.IsComplete);

            doc.StopTailing();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
