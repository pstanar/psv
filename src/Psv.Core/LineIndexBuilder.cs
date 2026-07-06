namespace Psv.Core;

/// <summary>
/// Builds a <see cref="LineIndex"/> by walking a byte source, recording a checkpoint every
/// <paramref name="checkpointLineInterval"/> lines or <paramref name="checkpointByteInterval"/>
/// bytes, whichever comes first (plan §2.4). <see cref="Continue"/> resumes from the index's
/// last confirmed position instead of rescanning from the start, for live-tail catch-up.
/// </summary>
public sealed class LineIndexBuilder(
    IByteSource source,
    TextEncodingKind encoding,
    long startOffset = 0,
    int chunkSizeBytes = LineIndexBuilder.DefaultChunkSizeBytes,
    long checkpointLineInterval = LineIndexBuilder.DefaultCheckpointLineInterval,
    long checkpointByteInterval = LineIndexBuilder.DefaultCheckpointByteInterval)
{
    public const int DefaultChunkSizeBytes = 8 * 1024 * 1024;
    public const long DefaultCheckpointLineInterval = 4096;
    public const long DefaultCheckpointByteInterval = 1024 * 1024;

    public void Build(LineIndex index, CancellationToken cancellationToken = default)
    {
        index.SeedInitialCheckpoint(startOffset);
        Scan(index, 0, startOffset, cancellationToken);
    }

    /// <summary>
    /// Resumes scanning from <see cref="LineIndex.ConfirmedScanOffset"/>. Correct even if the
    /// previous scan ended mid-line (no trailing terminator yet): that provisional line is not
    /// counted in <see cref="LineIndex.ConfirmedLineCount"/>, so it gets re-scanned together with
    /// whatever bytes were appended after it, rather than treated as two separate lines.
    /// </summary>
    public void Continue(LineIndex index, CancellationToken cancellationToken = default)
    {
        Scan(index, index.ConfirmedLineCount, index.ConfirmedScanOffset, cancellationToken);
    }

    private void Scan(LineIndex index, long fromLineNumber, long fromOffset, CancellationToken cancellationToken)
    {
        long lineNumber = fromLineNumber;
        long lineStart = fromOffset;
        long linesSinceCheckpoint = 0;
        long bytesSinceCheckpoint = 0;

        LineScanWalker.Walk(
            source,
            encoding,
            fromOffset,
            boundary =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                lineNumber++;
                linesSinceCheckpoint++;
                index.RecordLineEnding(boundary.Kind);

                long newLineStart = boundary.Offset + boundary.TerminatorLength;
                bytesSinceCheckpoint += newLineStart - lineStart;
                lineStart = newLineStart;

                if (linesSinceCheckpoint >= checkpointLineInterval || bytesSinceCheckpoint >= checkpointByteInterval)
                {
                    index.AppendCheckpoint(new Checkpoint(lineNumber, lineStart), lineStart, lineNumber);
                    linesSinceCheckpoint = 0;
                    bytesSinceCheckpoint = 0;
                }

                return false;
            },
            chunkSizeBytes);

        index.Complete(lineNumber, lineStart, source.Length);
    }

    public (LineIndex Index, Task Completion) StartAsync(CancellationToken cancellationToken = default)
    {
        var index = new LineIndex();
        var completion = Task.Run(() => Build(index, cancellationToken), cancellationToken);
        return (index, completion);
    }
}
