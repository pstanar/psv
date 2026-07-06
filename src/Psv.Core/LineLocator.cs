using System.Buffers;

namespace Psv.Core;

/// <summary>
/// Resolves line numbers to byte ranges (and decoded text) against a <see cref="LineIndex"/>.
/// <see cref="TryGetLineRange"/> is for one-off random access (e.g. Go To Line); <see
/// cref="GetLineRanges"/> does a single checkpoint lookup + forward scan for a whole range, which
/// is what viewport rendering and search should use so they don't repeat the
/// checkpoint-to-start scan once per line. <see cref="Encoding"/> is mutable so a manual encoding
/// override (plan §2.2.1) can take effect without replacing the locator.
/// </summary>
public sealed class LineLocator(LineIndex index, IByteSource source, TextEncodingKind encoding)
{
    public TextEncodingKind Encoding { get; set; } = encoding;

    public bool TryGetLineRange(long lineNumber, out LineRange range)
    {
        if (lineNumber >= 0 && lineNumber < index.KnownLineCount)
        {
            var ranges = GetLineRanges(lineNumber, 1);
            if (ranges.Count == 1)
            {
                range = ranges[0];
                return true;
            }
        }

        range = default;
        return false;
    }

    public IReadOnlyList<LineRange> GetLineRanges(long startLine, int count)
    {
        var results = new List<LineRange>(Math.Max(0, count));
        if (startLine < 0 || count <= 0)
        {
            return results;
        }

        if (!index.TryGetNearestCheckpoint(startLine, out var checkpoint))
        {
            return results;
        }

        long currentLine = checkpoint.LineNumber;
        long lineStart = checkpoint.ByteOffset;
        long endLineExclusive = startLine + count;

        LineScanWalker.Walk(source, Encoding, checkpoint.ByteOffset, boundary =>
        {
            if (currentLine >= endLineExclusive)
            {
                return true;
            }

            if (currentLine >= startLine)
            {
                results.Add(new LineRange(lineStart, checked((int)(boundary.Offset - lineStart)), boundary.TerminatorLength));
            }

            currentLine++;
            lineStart = boundary.Offset + boundary.TerminatorLength;
            return currentLine >= endLineExclusive;
        });

        if (results.Count < count
            && currentLine >= startLine && currentLine < endLineExclusive
            && currentLine < index.KnownLineCount
            && lineStart <= source.Length)
        {
            results.Add(new LineRange(lineStart, checked((int)(source.Length - lineStart)), 0));
        }

        return results;
    }

    public string GetLineText(long lineNumber)
    {
        if (!TryGetLineRange(lineNumber, out var range))
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        }

        return DecodeLine(range);
    }

    public string DecodeLine(LineRange range)
    {
        if (range.ContentLength == 0)
        {
            return string.Empty;
        }

        // Rented, not allocated: this runs once per visible line per frame while scrolling, and
        // once per examined line during a search.
        byte[] rented = ArrayPool<byte>.Shared.Rent(range.ContentLength);
        try
        {
            int read = source.Read(range.StartOffset, rented.AsSpan(0, range.ContentLength));
            return TextEncodingCatalog.Resolve(Encoding).GetString(rented, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
