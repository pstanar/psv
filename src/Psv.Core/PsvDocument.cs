namespace Psv.Core;

/// <summary>
/// Top-level entry point tying file access, encoding detection, indexing, and live-tail
/// catch-up together. Pass <paramref name="forcedEncoding"/> to skip BOM sniffing entirely
/// (manual override, plan §2.2.1); otherwise the encoding is auto-detected from the file's
/// leading bytes. <see cref="ChangeEncodingAsync"/> supports changing it later (the manual
/// encoding cycle keybinding).
/// </summary>
public sealed class PsvDocument : IDisposable
{
    private static readonly TimeSpan DefaultTailPollInterval = TimeSpan.FromMilliseconds(400);

    // MappedFileByteSource.Remap() fully unmaps and recreates the memory mapping under an exclusive
    // write lock (a read-only mapping can't be created with slack capacity beyond the file's actual
    // on-disk size, so there's no cheaper way to "leave room to grow into"). A producer appending
    // many times per second would otherwise force that teardown-and-recreate on every single append,
    // stalling the UI thread's concurrent reads. Waiting this long between iterations (after the
    // first, which stays immediate for responsiveness) lets a burst of rapid writes coalesce into
    // one remap instead of one per append, trading a small amount of freshness latency for far fewer
    // remaps under sustained fast-write workloads.
    private static readonly TimeSpan RemapCoalesceDelay = TimeSpan.FromMilliseconds(50);

    // How much of the file EncodingDetector gets to work with for its BOM-less UTF-8 validity
    // check - enough to catch non-ASCII bytes that appear a little way into typical log lines,
    // without reading a meaningful fraction of a multi-gigabyte file just to open it.
    private const int EncodingSniffBytes = 64 * 1024;

    private readonly string _path;
    private readonly MappedFileByteSource _source;

    // Guards _builder/Index/_source against a live-tail catch-up and a manual encoding change
    // running at the same time — both mutate the same LineIndex, and without this a tail catch-up
    // could interleave with an encoding-triggered rebuild and corrupt the index.
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    private LineIndexBuilder _builder;
    private FileTailWatcher? _tailWatcher;
    private CancellationTokenSource? _reindexCts;
    private int _tailBusy;
    private volatile bool _pendingGrow;
    private volatile bool _pendingReplace;
    private volatile bool _disposed;

    /// <summary>
    /// Raised when the background tail catch-up loop hits an exception other than a transient
    /// <see cref="IOException"/> (see <see cref="TryRunTailWork"/>). Tailing is stopped before this
    /// fires - since <see cref="LineIndexBuilder.Continue"/> always resumes from the exact offset
    /// that just failed, silently retrying it on every subsequent growth signal would very likely
    /// hit the identical exception forever, which is worse than surfacing it once and stopping.
    /// </summary>
    public event Action<Exception>? TailError;

    /// <summary>
    /// Raised whenever this document's visible state actually changes - the initial index build
    /// progressing or completing, live-tail catch-up picking up growth or a truncation/rebuild, or
    /// an encoding change rebuilding the index. For a text document this is a relay of
    /// <see cref="LineIndex.Changed"/> (checkpoint granularity, not once per line); for a binary
    /// document, which never builds an index (see <see cref="IsBinary"/>), it's raised directly by
    /// <see cref="TryRunTailWork"/> after a successful tail catch-up. Always raised on whatever
    /// thread performed the mutation - subscribers that touch UI state must marshal themselves.
    /// </summary>
    public event Action? Changed;

    private PsvDocument(string path, MappedFileByteSource source, TextEncodingKind encoding, int bomLength, bool isManualEncoding, bool isBinary)
    {
        _path = path;
        _source = source;
        Encoding = encoding;
        BomLength = bomLength;
        IsManualEncoding = isManualEncoding;
        IsBinary = isBinary;
        Index = new LineIndex();
        Index.Changed += () => Changed?.Invoke();
        Locator = new LineLocator(Index, source, encoding);
        _builder = new LineIndexBuilder(source, encoding, bomLength);
    }

    public TextEncodingKind Encoding { get; private set; }

    public int BomLength { get; private set; }

    /// <summary>True if the encoding was forced (CLI flag or the cycle keybinding) rather than auto-detected.</summary>
    public bool IsManualEncoding { get; private set; }

    /// <summary>
    /// True if the file is being viewed as binary/hex rather than text - either detected from its
    /// leading bytes (<see cref="BinaryContentDetector"/>) or forced via the <c>--bin16</c>/
    /// <c>--bin32</c>/<c>--bin64</c> CLI flags or the Ctrl+B view-mode toggle. <see cref="Index"/>/
    /// <see cref="Locator"/> are never built for
    /// a binary document (see <see cref="BuildIndex"/>) - hex rendering reads <see cref="ByteSource"/>
    /// directly instead, since a fixed-width byte grid needs no line-boundary scanning at all.
    /// </summary>
    public bool IsBinary { get; private set; }

    public long FileSizeBytes => _source.Length;

    /// <summary>Direct byte access for hex-mode rendering - bypasses the line-index machinery entirely.</summary>
    public IByteSource ByteSource => _source;

    public LineIndex Index { get; }

    public LineLocator Locator { get; }

    internal bool HasReindexCtsForTests => _reindexCts is not null;

    internal long RemapCountForTests => _source.RemapCount;

    public static PsvDocument Open(string path, TextEncodingKind? forcedEncoding = null, bool? forceBinary = null)
    {
        var source = new MappedFileByteSource(path);
        try
        {
            TextEncodingKind encoding;
            int bomLength;
            bool isBinary;

            if (forcedEncoding is { } forced)
            {
                encoding = forced;
                bomLength = 0;
                isBinary = forceBinary ?? false;
            }
            else
            {
                byte[] header = new byte[Math.Min(EncodingSniffBytes, source.Length)];
                int read = source.Read(0, header);
                bool sampleIsEntireFile = read == source.Length;
                var detection = EncodingDetector.Detect(header.AsSpan(0, read), sampleIsEntireFile);
                encoding = detection.Kind;
                bomLength = detection.BomLength;

                // A matched BOM is decisive proof of UTF-16 text - a legitimate UTF-16 file is
                // dense with 0x00 bytes by construction and must never be misclassified as binary
                // from that alone, so the sniff only runs when no BOM was found.
                isBinary = forceBinary ?? (bomLength == 0 && BinaryContentDetector.LooksBinary(header.AsSpan(0, read)));
            }

            return new PsvDocument(path, source, encoding, bomLength, isManualEncoding: forcedEncoding is not null, isBinary);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>No-op for a binary document - see <see cref="IsBinary"/>.</summary>
    public void BuildIndex(CancellationToken cancellationToken = default)
    {
        if (!IsBinary)
        {
            _builder.Build(Index, cancellationToken);
        }
    }

    /// <summary>Already-completed for a binary document - see <see cref="IsBinary"/>.</summary>
    public Task BuildIndexAsync(CancellationToken cancellationToken = default) =>
        IsBinary ? Task.CompletedTask : Task.Run(() => _builder.Build(Index, cancellationToken), cancellationToken);

    /// <summary>
    /// Switches to a manually-chosen encoding (plan §2.2.1 cycle keybinding / CLI flag). Switching
    /// within the single-byte-compatible family (UTF-8/ASCII/Windows-1252/Latin-1) is a cheap
    /// re-render — those all agree on where line-boundary bytes are, so the existing index stays
    /// valid. Crossing into/out of UTF-16 changes the byte width of a line boundary, so the index
    /// must be rebuilt from scratch; a rapid second call cancels the first rebuild rather than
    /// letting both run. Returns true if a rebuild was triggered.
    /// </summary>
    public async Task<bool> ChangeEncodingAsync(TextEncodingKind newEncoding)
    {
        if (newEncoding == Encoding)
        {
            return false;
        }

        bool widthChanged = TextEncodingCatalog.IsWideEncoding(Encoding) != TextEncodingCatalog.IsWideEncoding(newEncoding);

        Encoding = newEncoding;
        BomLength = 0;
        IsManualEncoding = true;
        Locator.Encoding = newEncoding;

        if (!widthChanged)
        {
            return false;
        }

        StopTailing();
        _reindexCts?.Cancel();

        var cts = new CancellationTokenSource();
        _reindexCts = cts;

        try
        {
            await _mutationLock.WaitAsync();
            try
            {
                Index.Reset();
                var builder = new LineIndexBuilder(_source, newEncoding);
                _builder = builder;
                await Task.Run(() => builder.Build(Index, cts.Token), cts.Token);
            }
            finally
            {
                _mutationLock.Release();
            }

            if (!cts.IsCancellationRequested)
            {
                StartTailing();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer encoding change, or genuinely cancelled mid-rebuild.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Document was disposed while this rebuild was in flight (mutation lock, source, or the
            // rebuild's own scan torn down mid-flight, e.g. the app reopening a different file the
            // instant a width-changing encoding switch was requested) - mirrors TryRunTailWork's
            // equivalent guard. Without this, the exception propagated out of ChangeEncodingAsync
            // uncaught, and MainWindow's caller invokes it fire-and-forget, turning it into another
            // silently unobserved task exception.
        }
        finally
        {
            // Whichever call created cts is the one that disposes it, exactly once, whether it
            // ran to completion, faulted, or was itself superseded by a newer call cancelling it.
            // The ReferenceEquals guard stops a superseded call from nulling out a newer call's
            // _reindexCts - only the call that's still current clears the field.
            if (ReferenceEquals(_reindexCts, cts))
            {
                _reindexCts = null;
            }

            cts.Dispose();
        }

        return true;
    }

    /// <summary>
    /// Starts watching the file for growth (incremental catch-up via
    /// <see cref="LineIndexBuilder.Continue"/>) or truncation/replacement (full rebuild). Must not
    /// be called before the initial index build has completed - enforced below, since a violation
    /// would race the unguarded initial <see cref="BuildIndexAsync"/>/<see cref="BuildIndex"/>
    /// against a mutation-lock-guarded <see cref="LineIndexBuilder.Continue"/>, corrupting
    /// <see cref="LineIndex"/>'s checkpoint ordering.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document is a text document whose index hasn't finished its initial build yet.
    /// </exception>
    public void StartTailing(TimeSpan? pollInterval = null)
    {
        StopTailing();

        // Guards against a caller racing an async index-build continuation against Dispose() (e.g.
        // the app reopening a different file the instant the initial build finishes) — without
        // this, a watcher could be created for a document that is already torn down and never get
        // cleaned up.
        if (_disposed)
        {
            return;
        }

        // A binary document never builds an index (see IsBinary) - Index.IsComplete would never
        // become true, so that half of the check only applies to text documents. Every current
        // caller already gates on this (MainWindow's OpenFile/SyncTailingToCurrentDocument, and
        // ChangeEncodingAsync calling this only after its own rebuild completes) - this makes that a
        // hard invariant instead of a convention every future call site has to remember correctly.
        if (!IsBinary && !Index.IsComplete)
        {
            throw new InvalidOperationException(
                "StartTailing must not be called before the initial index build has completed.");
        }

        var watcher = new FileTailWatcher(_path, pollInterval ?? DefaultTailPollInterval);
        watcher.FileGrew += () =>
        {
            _pendingGrow = true;
            TryRunTailWork();
        };
        watcher.FileReplacedOrTruncated += () =>
        {
            _pendingReplace = true;
            TryRunTailWork();
        };
        _tailWatcher = watcher;

        // The watcher's own baseline is captured at construction time, which may already reflect
        // growth that happened between the initial build finishing and this call — force one
        // catch-up check now so that gap can never be silently missed.
        _pendingGrow = true;
        TryRunTailWork();
    }

    public void StopTailing()
    {
        _tailWatcher?.Dispose();
        _tailWatcher = null;
    }

    internal bool IsTailingForTests => _tailWatcher is not null;

    private void TryRunTailWork()
    {
        if (Interlocked.CompareExchange(ref _tailBusy, 1, 0) != 0)
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                while (_pendingGrow || _pendingReplace)
                {
                    bool replace = _pendingReplace;
                    _pendingGrow = false;
                    _pendingReplace = false;

                    await _mutationLock.WaitAsync();
                    try
                    {
                        if (replace)
                        {
                            _source.Reopen();
                            if (!IsBinary)
                            {
                                Index.Reset();
                                _builder.Build(Index);
                            }
                        }
                        else
                        {
                            _source.Remap();
                            if (!IsBinary)
                            {
                                _builder.Continue(Index);
                            }
                        }

                        // A text document already gets this via the Index.Changed relay (Reset/Build/
                        // Continue above all mutate Index); a binary document never touches Index at
                        // all (see IsBinary), so nothing else would notify that FileSizeBytes moved.
                        if (IsBinary)
                        {
                            Changed?.Invoke();
                        }
                    }
                    catch (IOException)
                    {
                        // Transient (file locked mid-write, rotated out from under us, etc.) — the
                        // next FileTailWatcher signal will retry.
                    }
                    finally
                    {
                        _mutationLock.Release();
                    }

                    // Stay "primed" for RemapCoalesceDelay after each remap before deciding whether
                    // to exit, rather than re-checking the pending flags immediately: a remap this
                    // cheap (a few KB to a few MB) typically completes well within the gap between
                    // individual writes from a rapid producer, so without this pause nearly every
                    // append would find nothing pending yet, exit, and reset _tailBusy - meaning the
                    // next append spawns a brand new, undelayed Task.Run instead of coalescing into
                    // this one. The pause costs nothing when the file is actually idle (Index is
                    // already caught up the instant the remap above finishes) - it only delays how
                    // soon _tailBusy resets, not how soon new content becomes visible.
                    await Task.Delay(RemapCoalesceDelay);
                }
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Document was disposed while this catch-up was in flight (mutation lock or
                // source torn down mid-loop) — nothing left to update.
            }
            catch (Exception ex) when (!_disposed)
            {
                // Anything other than a transient IOException (a bug triggered by a specific byte
                // pattern, a corrupt file, etc.) would very likely recur identically on retry, since
                // Continue()/Build() always resume from the exact offset that just failed - looping
                // that silently forever is worse than surfacing it once and stopping. Clearing the
                // pending flags (in addition to StopTailing disabling the watcher) keeps the re-arm
                // check below from immediately calling back into a doomed retry.
                _pendingGrow = false;
                _pendingReplace = false;
                StopTailing();
                TailError?.Invoke(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _tailBusy, 0);

                // Close the race where a signal arrives between the loop's last (false) check and
                // the flag reset above: re-arm if something slipped in right at the boundary.
                if (!_disposed && (_pendingGrow || _pendingReplace))
                {
                    TryRunTailWork();
                }
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        StopTailing();
        _reindexCts?.Cancel();
        _source.Dispose();
        _mutationLock.Dispose();
    }
}
