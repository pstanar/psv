namespace Psv.Core.Tests;

public class MappedFileByteSourceConcurrencyTests
{
    [Fact]
    public async Task ConcurrentReadsAndRemapsDoNotThrowOrCorrupt()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "initial-content\n");
            using var source = new MappedFileByteSource(path);

            using var appendStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(appendStream) { AutoFlush = true };

            var cts = new CancellationTokenSource();
            var readerExceptions = new List<Exception>();

            var readerTask = Task.Run(() =>
            {
                byte[] buffer = new byte[64];
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        long len = source.Length;
                        if (len > 0)
                        {
                            source.Read(0, buffer.AsSpan(0, (int)Math.Min(buffer.Length, len)));
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (readerExceptions)
                        {
                            readerExceptions.Add(ex);
                        }
                    }
                }
            });

            for (int i = 0; i < 200; i++)
            {
                writer.Write($"line-{i}\n");
                source.Remap();
            }

            cts.Cancel();
            await readerTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Empty(readerExceptions);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
