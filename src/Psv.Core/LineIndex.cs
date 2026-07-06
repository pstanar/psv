namespace Psv.Core;

/// <summary>
/// Sparse, checkpoint-based line index. Safe to read from one thread while
/// <see cref="LineIndexBuilder"/> appends to it from another (see plan §2.4/§4) — including while
/// a live-tail catch-up (<see cref="LineIndexBuilder.Continue"/>) or a full rebuild after
/// truncation (<see cref="Reset"/>) runs concurrently with UI reads.
/// </summary>
public sealed class LineIndex
{
    private readonly List<Checkpoint> _checkpoints = [];
    private readonly Lock _lock = new();
    private LineEndingCounts _counts;

    /// <summary>
    /// Lines confirmed by an actual terminator. Unlike <see cref="KnownLineCount"/>, this never
    /// includes the trailing unterminated "virtual" line, which makes it the correct resume point
    /// for <see cref="LineIndexBuilder.Continue"/> — that trailing content isn't actually finished
    /// and must be re-scanned together with whatever bytes were appended after it.
    /// </summary>
    internal long ConfirmedLineCount { get; private set; }

    /// <summary>Byte offset immediately after the last confirmed terminator — the resume point.</summary>
    internal long ConfirmedScanOffset { get; private set; }

    public long ScannedByteOffset { get; private set; }

    /// <summary>
    /// Total addressable lines, including a trailing unterminated line if the source currently
    /// ends without one (true both for a static file with no final newline, and for a growing
    /// file whose latest line hasn't been terminated yet).
    /// </summary>
    public long KnownLineCount { get; private set; }

    /// <summary>
    /// True once scanning has caught up to the source's current length. For a tailed file this
    /// does not mean "will never change again" — just "not currently catching up".
    /// </summary>
    public bool IsComplete { get; private set; }

    public LineEndingKind? DominantLineEnding
    {
        get
        {
            lock (_lock)
            {
                return _counts.Dominant();
            }
        }
    }

    public int CheckpointCount
    {
        get
        {
            lock (_lock)
            {
                return _checkpoints.Count;
            }
        }
    }

    internal void SeedInitialCheckpoint(long startOffset)
    {
        lock (_lock)
        {
            _checkpoints.Add(new Checkpoint(0, startOffset));
        }
    }

    internal void RecordLineEnding(LineEndingKind kind)
    {
        lock (_lock)
        {
            _counts.Record(kind);
        }
    }

    internal void AppendCheckpoint(Checkpoint checkpoint, long scannedByteOffset, long knownLineCount)
    {
        lock (_lock)
        {
            _checkpoints.Add(checkpoint);
            ScannedByteOffset = scannedByteOffset;
            KnownLineCount = knownLineCount;
            ConfirmedLineCount = knownLineCount;
            ConfirmedScanOffset = scannedByteOffset;
        }
    }

    /// <summary>
    /// Called once a scan (initial build or tail catch-up) has reached the source's current
    /// length. <paramref name="confirmedLineCount"/>/<paramref name="confirmedOffset"/> reflect
    /// only terminator-confirmed lines; if the source extends past <paramref name="confirmedOffset"/>,
    /// an extra virtual line is exposed via <see cref="KnownLineCount"/> for the unterminated tail.
    /// </summary>
    internal void Complete(long confirmedLineCount, long confirmedOffset, long sourceLength)
    {
        lock (_lock)
        {
            ConfirmedLineCount = confirmedLineCount;
            ConfirmedScanOffset = confirmedOffset;
            KnownLineCount = sourceLength > confirmedOffset ? confirmedLineCount + 1 : confirmedLineCount;
            ScannedByteOffset = sourceLength;
            IsComplete = true;
        }
    }

    /// <summary>Discards all state for a full rebuild — used when the file is truncated/replaced.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _checkpoints.Clear();
            _counts = default;
            ConfirmedLineCount = 0;
            ConfirmedScanOffset = 0;
            KnownLineCount = 0;
            ScannedByteOffset = 0;
            IsComplete = false;
        }
    }

    public bool TryGetNearestCheckpoint(long lineNumber, out Checkpoint checkpoint)
    {
        lock (_lock)
        {
            if (_checkpoints.Count == 0)
            {
                checkpoint = default;
                return false;
            }

            int lo = 0;
            int hi = _checkpoints.Count - 1;
            int best = 0;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (_checkpoints[mid].LineNumber <= lineNumber)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            checkpoint = _checkpoints[best];
            return true;
        }
    }
}
