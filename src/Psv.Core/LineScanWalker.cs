using System.Buffers;

namespace Psv.Core;

/// <summary>
/// Streams line boundaries forward from a given offset, handling chunked reads and cross-chunk
/// carry state. Shared by the index builder (walks to EOF) and the line locator (walks until it
/// has what it needs, then stops). Boundaries are delivered straight to the callback — no
/// per-chunk list allocation.
/// </summary>
internal static class LineScanWalker
{
    internal const int DefaultScanBufferSize = 64 * 1024;

    public static void Walk(
        IByteSource source,
        TextEncodingKind encoding,
        long startOffset,
        Func<LineBoundary, bool> onBoundary,
        int bufferSize = DefaultScanBufferSize)
    {
        int unitWidth = LineBoundaryScanner.UnitWidth(encoding);
        long scanOffset = startOffset;
        var carry = new ScanCarry();
        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            bool stopped = false;
            while (!stopped)
            {
                int capacity = rented.Length - (rented.Length % unitWidth);
                long remaining = source.Length - scanOffset;
                if (remaining <= 0)
                {
                    break;
                }

                int toRead = (int)Math.Min(capacity, remaining);
                if (toRead <= 0)
                {
                    break;
                }

                var span = rented.AsSpan(0, toRead);
                int read = source.Read(scanOffset, span);
                if (read <= 0)
                {
                    break;
                }

                stopped = LineBoundaryScanner.ScanChunk(span[..read], encoding, ref carry, scanOffset, onBoundary);
                scanOffset += read;
            }

            if (!stopped && carry.HasPendingCr)
            {
                onBoundary(new LineBoundary(carry.PendingOffset, unitWidth, LineEndingKind.Cr));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
