namespace Psv.Core;

/// <summary>
/// Finds line-ending byte sequences within a single chunk, invoking a callback per boundary
/// (returning true from the callback stops the scan) so large scans don't allocate an
/// intermediate list per chunk. Chunks are scanned independently; a terminator that straddles two
/// chunk reads (e.g. a CR as the last byte of one chunk and an LF as the first byte of the next)
/// is resolved via <see cref="ScanCarry"/>, which the caller threads through consecutive calls.
/// </summary>
public static class LineBoundaryScanner
{
    public static int UnitWidth(TextEncodingKind encoding) =>
        TextEncodingCatalog.IsWideEncoding(encoding) ? 2 : 1;

    /// <summary>Returns true if <paramref name="onBoundary"/> requested an early stop.</summary>
    public static bool ScanChunk(
        ReadOnlySpan<byte> buffer,
        TextEncodingKind encoding,
        ref ScanCarry carry,
        long baseOffset,
        Func<LineBoundary, bool> onBoundary)
    {
        return TextEncodingCatalog.IsWideEncoding(encoding)
            ? ScanUtf16(buffer, bigEndian: encoding == TextEncodingKind.Utf16BE, baseOffset, ref carry, onBoundary)
            : ScanSingleByte(buffer, baseOffset, ref carry, onBoundary);
    }

    private static bool ScanSingleByte(ReadOnlySpan<byte> buffer, long baseOffset, ref ScanCarry carry, Func<LineBoundary, bool> onBoundary)
    {
        int i = 0;

        if (carry.HasPendingCr)
        {
            carry.HasPendingCr = false;
            if (buffer.Length > 0 && buffer[0] == (byte)'\n')
            {
                i = 1;
                if (onBoundary(new LineBoundary(carry.PendingOffset, 2, LineEndingKind.CrLf)))
                {
                    return true;
                }
            }
            else if (onBoundary(new LineBoundary(carry.PendingOffset, 1, LineEndingKind.Cr)))
            {
                return true;
            }
        }

        while (i < buffer.Length)
        {
            int rel = buffer[i..].IndexOfAny((byte)'\n', (byte)'\r');
            if (rel < 0)
            {
                break;
            }

            int pos = i + rel;
            if (buffer[pos] == (byte)'\n')
            {
                if (onBoundary(new LineBoundary(baseOffset + pos, 1, LineEndingKind.Lf)))
                {
                    return true;
                }

                i = pos + 1;
                continue;
            }

            // buffer[pos] == '\r'
            if (pos + 1 < buffer.Length)
            {
                if (buffer[pos + 1] == (byte)'\n')
                {
                    if (onBoundary(new LineBoundary(baseOffset + pos, 2, LineEndingKind.CrLf)))
                    {
                        return true;
                    }

                    i = pos + 2;
                }
                else
                {
                    if (onBoundary(new LineBoundary(baseOffset + pos, 1, LineEndingKind.Cr)))
                    {
                        return true;
                    }

                    i = pos + 1;
                }
            }
            else
            {
                carry.HasPendingCr = true;
                carry.PendingOffset = baseOffset + pos;
                i = pos + 1;
            }
        }

        return false;
    }

    private static bool ScanUtf16(ReadOnlySpan<byte> buffer, bool bigEndian, long baseOffset, ref ScanCarry carry, Func<LineBoundary, bool> onBoundary)
    {
        byte lfHi = bigEndian ? (byte)0x00 : (byte)0x0A;
        byte lfLo = bigEndian ? (byte)0x0A : (byte)0x00;
        byte crHi = bigEndian ? (byte)0x00 : (byte)0x0D;
        byte crLo = bigEndian ? (byte)0x0D : (byte)0x00;

        int i = 0;

        if (carry.HasPendingCr)
        {
            carry.HasPendingCr = false;
            if (buffer.Length >= 2 && Matches(buffer, 0, lfHi, lfLo))
            {
                i = 2;
                if (onBoundary(new LineBoundary(carry.PendingOffset, 4, LineEndingKind.CrLf)))
                {
                    return true;
                }
            }
            else if (onBoundary(new LineBoundary(carry.PendingOffset, 2, LineEndingKind.Cr)))
            {
                return true;
            }
        }

        while (i + 1 < buffer.Length)
        {
            if (Matches(buffer, i, lfHi, lfLo))
            {
                if (onBoundary(new LineBoundary(baseOffset + i, 2, LineEndingKind.Lf)))
                {
                    return true;
                }

                i += 2;
                continue;
            }

            if (Matches(buffer, i, crHi, crLo))
            {
                if (i + 3 < buffer.Length && Matches(buffer, i + 2, lfHi, lfLo))
                {
                    if (onBoundary(new LineBoundary(baseOffset + i, 4, LineEndingKind.CrLf)))
                    {
                        return true;
                    }

                    i += 4;
                }
                else if (i + 2 < buffer.Length)
                {
                    if (onBoundary(new LineBoundary(baseOffset + i, 2, LineEndingKind.Cr)))
                    {
                        return true;
                    }

                    i += 2;
                }
                else
                {
                    carry.HasPendingCr = true;
                    carry.PendingOffset = baseOffset + i;
                    i += 2;
                }

                continue;
            }

            i += 2;
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> buffer, int pos, byte hi, byte lo) =>
        buffer[pos] == hi && buffer[pos + 1] == lo;
}
