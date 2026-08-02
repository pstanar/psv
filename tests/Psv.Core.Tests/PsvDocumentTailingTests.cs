using System.Reflection;

namespace Psv.Core.Tests;

public class PsvDocumentTailingTests
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

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task TailingPicksUpAppendedLinesWithoutReopeningTheDocument()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line1\nline2\n");

            using var doc = PsvDocument.Open(path);
            doc.BuildIndex();
            Assert.Equal(2, doc.Index.KnownLineCount);

            doc.StartTailing(TimeSpan.FromMilliseconds(100));
            try
            {
                using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(append))
                {
                    writer.Write("line3\nline4\n");
                }

                bool caughtUp = await WaitUntilAsync(() => doc.Index.KnownLineCount == 4, TimeSpan.FromSeconds(5));
                Assert.True(caughtUp, $"expected 4 lines, got {doc.Index.KnownLineCount}");
                Assert.Equal("line3", doc.Locator.GetLineText(2));
                Assert.Equal("line4", doc.Locator.GetLineText(3));
            }
            finally
            {
                doc.StopTailing();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailingHandlesTruncationByRebuildingFromScratch()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line1\nline2\nline3\n");

            using var doc = PsvDocument.Open(path);
            doc.BuildIndex();
            Assert.Equal(3, doc.Index.KnownLineCount);

            doc.StartTailing(TimeSpan.FromMilliseconds(100));
            try
            {
                // Rename-the-old-file-away rotation, not in-place truncation or rename-over:
                // Windows refuses to shrink, recreate (FileMode.Create), *or even replace-via-move*
                // a file with an active memory-mapped section — all three were tried while writing
                // this test and all three failed with mapped-section errors. Renaming the actively
                // mapped file *away* and creating a fresh file at the original path is the pattern
                // real rotation tools use on Windows for exactly this reason.
                string rotated = path + ".1";
                File.Move(path, rotated, overwrite: true);
                File.WriteAllText(path, "new-short-file\n");

                bool rebuilt = await WaitUntilAsync(
                    () => doc.Index.KnownLineCount == 1 && doc.Index.IsComplete,
                    TimeSpan.FromSeconds(5));
                Assert.True(rebuilt, $"expected 1 line after truncation, got {doc.Index.KnownLineCount}");
                Assert.Equal("new-short-file", doc.Locator.GetLineText(0));
            }
            finally
            {
                doc.StopTailing();
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".1");
        }
    }

    [Fact]
    public async Task StartTailingCatchesUpOnGrowthThatHappenedBeforeItWasCalled()
    {
        // Regression test: MappedFileByteSource.Length is cached at open time and only refreshed
        // by Remap/Reopen, so BuildIndex() alone won't see growth that happened after Open(). If
        // StartTailing's watcher baselined against the file's live length at construction time,
        // that gap would be silently missed forever, since the watcher would never see a "grew"
        // transition. StartTailing must force a catch-up check itself, not rely on the watcher.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line1\n");

            using var doc = PsvDocument.Open(path);

            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(append))
            {
                writer.Write("line2\nline3\n");
            }

            doc.BuildIndex();
            Assert.Equal(1, doc.Index.KnownLineCount); // confirms the gap exists before the fix's catch-up runs

            doc.StartTailing(TimeSpan.FromMilliseconds(100));
            try
            {
                bool caughtUp = await WaitUntilAsync(() => doc.Index.KnownLineCount == 3, TimeSpan.FromSeconds(5));
                Assert.True(caughtUp, $"expected 3 lines, got {doc.Index.KnownLineCount}");
                Assert.Equal("line2", doc.Locator.GetLineText(1));
                Assert.Equal("line3", doc.Locator.GetLineText(2));
            }
            finally
            {
                doc.StopTailing();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailingContinuesAcrossMultipleGrowthBursts()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "start\n");

            using var doc = PsvDocument.Open(path);
            doc.BuildIndex();
            doc.StartTailing(TimeSpan.FromMilliseconds(80));
            try
            {
                for (int burst = 0; burst < 5; burst++)
                {
                    using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(append))
                    {
                        writer.Write($"burst-{burst}\n");
                    }

                    await Task.Delay(30);
                }

                bool caughtUp = await WaitUntilAsync(() => doc.Index.KnownLineCount == 6, TimeSpan.FromSeconds(5));
                Assert.True(caughtUp, $"expected 6 lines, got {doc.Index.KnownLineCount}");
                for (int burst = 0; burst < 5; burst++)
                {
                    Assert.Equal($"burst-{burst}", doc.Locator.GetLineText(1 + burst));
                }
            }
            finally
            {
                doc.StopTailing();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailingInBinaryModeGrowsFileSizeWithoutTouchingTheLineIndex()
    {
        // Real PE header prefix - decisive as binary via its embedded NUL bytes.
        byte[] header = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, header);

            using var doc = PsvDocument.Open(path);
            Assert.True(doc.IsBinary);
            Assert.Equal(header.Length, doc.FileSizeBytes);

            doc.StartTailing(TimeSpan.FromMilliseconds(100));
            try
            {
                using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    append.Write([0x01, 0x02, 0x03, 0x04]);
                }

                bool grew = await WaitUntilAsync(() => doc.FileSizeBytes == header.Length + 4, TimeSpan.FromSeconds(5));
                Assert.True(grew, $"expected {header.Length + 4} bytes, got {doc.FileSizeBytes}");
                Assert.Equal(0, doc.Index.KnownLineCount);
                Assert.False(doc.Index.IsComplete);
            }
            finally
            {
                doc.StopTailing();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RapidAppendsCoalesceIntoFarFewerRemapsThanAppends()
    {
        // Regression test: MappedFileByteSource.Remap() fully unmaps and recreates the mapping
        // under an exclusive write lock on every call - a producer appending many times per second
        // used to force that teardown-and-recreate on every single append, stalling concurrent UI
        // reads. The tail loop now waits RemapCoalesceDelay between iterations (after the first) so
        // a burst of rapid writes collapses into far fewer remaps.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "start\n");

            using var doc = PsvDocument.Open(path);
            doc.BuildIndex();
            doc.StartTailing(TimeSpan.FromMilliseconds(20));
            try
            {
                const int appendCount = 60;
                using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(append) { AutoFlush = true })
                {
                    for (int i = 0; i < appendCount; i++)
                    {
                        writer.Write($"line-{i}\n");
                        await Task.Delay(2); // far faster than RemapCoalesceDelay
                    }
                }

                bool caughtUp = await WaitUntilAsync(() => doc.Index.KnownLineCount == 1 + appendCount, TimeSpan.FromSeconds(5));
                Assert.True(caughtUp, $"expected {1 + appendCount} lines, got {doc.Index.KnownLineCount}");

                Assert.True(
                    doc.RemapCountForTests < appendCount / 2,
                    $"expected far fewer remaps than appends ({appendCount}), got {doc.RemapCountForTests}");
            }
            finally
            {
                doc.StopTailing();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailErrorFiresAndStopsTailingOnAnUnexpectedNonIOException()
    {
        // Regression test: TryRunTailWork used to swallow only IOException, letting anything else
        // escape unobserved from the background Task.Run and silently kill tailing forever with no
        // trace. Reflection is used to force a NullReferenceException from the catch-up path - the
        // only deterministic way to inject a non-IOException fault, since LineIndexBuilder is sealed
        // and PsvDocument doesn't expose a seam for a fake one.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line1\n");

            using var doc = PsvDocument.Open(path);
            doc.BuildIndex();

            Exception? observed = null;
            doc.TailError += ex => observed = ex;

            doc.StartTailing(TimeSpan.FromMilliseconds(100));

            typeof(PsvDocument).GetField("_builder", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(doc, null);

            using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(append))
            {
                writer.Write("line2\n");
            }

            bool stopped = await WaitUntilAsync(() => !doc.IsTailingForTests, TimeSpan.FromSeconds(5));
            Assert.True(stopped, "expected tailing to stop after an unexpected exception");
            Assert.IsType<NullReferenceException>(observed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartTailingBeforeInitialBuildCompletesThrows()
    {
        // Regression test: "safe to call only after the initial build has completed" used to be
        // documentation-only - nothing stopped a caller from violating it and racing the unguarded
        // initial Build() against a mutation-lock-guarded Continue(), corrupting checkpoint
        // ordering. Now enforced as a hard invariant instead of a convention every call site has to
        // remember.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line1\n");

            using var doc = PsvDocument.Open(path);
            Assert.False(doc.Index.IsComplete); // BuildIndex() deliberately not called yet

            Assert.Throws<InvalidOperationException>(() => doc.StartTailing(TimeSpan.FromMilliseconds(100)));
            Assert.False(doc.IsTailingForTests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartTailingBeforeInitialBuildCompletesIsAllowedForBinaryDocuments()
    {
        // A binary document never builds an index (see PsvDocument.IsBinary), so Index.IsComplete
        // never becomes true - the invariant above must not apply to it.
        byte[] header = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, header);

            using var doc = PsvDocument.Open(path);
            Assert.True(doc.IsBinary);
            Assert.False(doc.Index.IsComplete);

            doc.StartTailing(TimeSpan.FromMilliseconds(100));

            Assert.True(doc.IsTailingForTests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StartTailingAfterDisposeIsANoOp()
    {
        // Regression test: an async index-build continuation calling StartTailing() can race
        // against the document being disposed (e.g. the app reopening a different file the
        // instant the initial build finishes). Without the disposed guard, StartTailing() doesn't
        // throw here either (the watcher and its background catch-up loop only misbehave later,
        // asynchronously) - so IsTailingForTests, not "did this throw", is what actually
        // distinguishes fixed from broken.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line1\n");

            var doc = PsvDocument.Open(path);
            doc.BuildIndex();
            doc.Dispose();

            doc.StartTailing(TimeSpan.FromMilliseconds(50));

            Assert.False(doc.IsTailingForTests);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
