namespace Psv.Core;

public readonly record struct EncodingDetectionResult(TextEncodingKind Kind, int BomLength);

/// <summary>
/// Auto-detects a file's encoding from its leading bytes (plan §2.2). BOM-less files are
/// distinguished by validating the sample as UTF-8: real-world non-UTF-8 log files are
/// overwhelmingly single-byte Western encodings (Windows-1252 in practice, given this is
/// primarily a Windows tool), and any byte sequence that isn't valid UTF-8 can't be one of those
/// either, since Windows-1252/Latin-1 use the high bit freely for single-byte characters that
/// UTF-8's continuation-byte rules would reject.
/// </summary>
public static class EncodingDetector
{
    /// <param name="header">The leading bytes of the file to sniff.</param>
    /// <param name="sampleIsEntireFile">
    /// True if <paramref name="header"/> is the whole file rather than a truncated prefix - the
    /// difference matters for a multi-byte sequence that runs off the end of the sample: cut off
    /// mid-file that's inconclusive (more bytes exist beyond our sample), but at the true end of
    /// the file it's simply invalid.
    /// </param>
    public static EncodingDetectionResult Detect(ReadOnlySpan<byte> header, bool sampleIsEntireFile = false)
    {
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            return new EncodingDetectionResult(TextEncodingKind.Utf8, 3);
        }

        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xFE)
        {
            return new EncodingDetectionResult(TextEncodingKind.Utf16LE, 2);
        }

        if (header.Length >= 2 && header[0] == 0xFE && header[1] == 0xFF)
        {
            return new EncodingDetectionResult(TextEncodingKind.Utf16BE, 2);
        }

        TextEncodingKind kind = LooksLikeUtf8(header, sampleIsEntireFile) ? TextEncodingKind.Utf8 : TextEncodingKind.Windows1252;
        return new EncodingDetectionResult(kind, 0);
    }

    /// <summary>
    /// A standard UTF-8 validator (RFC 3629): rejects invalid continuation bytes, overlong
    /// encodings, and encoded surrogates/out-of-range codepoints. A multi-byte sequence that's cut
    /// off by the end of the sample is treated as inconclusive rather than invalid when the sample
    /// is only a prefix of the file, since a real character may simply straddle that boundary.
    /// </summary>
    private static bool LooksLikeUtf8(ReadOnlySpan<byte> bytes, bool sampleIsEntireFile)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            byte b = bytes[i];
            if (b <= 0x7F)
            {
                i++;
                continue;
            }

            int extraBytes;
            uint codepoint;
            int minCodepoint;

            if ((b & 0xE0) == 0xC0)
            {
                extraBytes = 1;
                codepoint = (uint)(b & 0x1F);
                minCodepoint = 0x80;
            }
            else if ((b & 0xF0) == 0xE0)
            {
                extraBytes = 2;
                codepoint = (uint)(b & 0x0F);
                minCodepoint = 0x800;
            }
            else if ((b & 0xF8) == 0xF0)
            {
                extraBytes = 3;
                codepoint = (uint)(b & 0x07);
                minCodepoint = 0x10000;
            }
            else
            {
                return false;
            }

            if (i + extraBytes >= bytes.Length)
            {
                return !sampleIsEntireFile;
            }

            for (int j = 1; j <= extraBytes; j++)
            {
                byte continuation = bytes[i + j];
                if ((continuation & 0xC0) != 0x80)
                {
                    return false;
                }

                codepoint = (codepoint << 6) | (uint)(continuation & 0x3F);
            }

            if (codepoint < minCodepoint || codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF))
            {
                return false;
            }

            i += extraBytes + 1;
        }

        return true;
    }
}
