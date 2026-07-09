namespace Psv.Core;

/// <summary>
/// Sniffs a file's leading bytes to decide whether it's binary content rather than text (plan:
/// binary/hex viewer mode). Kept separate from <see cref="EncodingDetector"/>, which answers a
/// different, narrower question ("which text encoding") and is also invoked from the manual
/// encoding-cycle path, where re-running binary detection would be wrong. Two signals, mirroring
/// what git's buffer_is_binary and file(1) use: a raw NUL byte is decisive (real text - UTF-8,
/// Windows-1252/Latin-1, or BOM-marked UTF-16, which is excluded by the caller before this runs -
/// essentially never contains one), and otherwise a high ratio of non-text control bytes.
/// </summary>
public static class BinaryContentDetector
{
    // Bytes 0x00-0x08, 0x0E-0x1F, and 0x7F are control codes with no place in ordinary text. Tab
    // (0x09), LF (0x0A), and CR (0x0D) are deliberately excluded - they're common in real text -
    // and so are the codes in between (0x0B-0x0C, VT/FF) as occasional legitimate text controls.
    private const int NonTextControlPercentThreshold = 30;

    public static bool LooksBinary(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        if (header.IndexOf((byte)0) >= 0)
        {
            return true;
        }

        int nonTextControls = 0;
        foreach (byte b in header)
        {
            bool isNonTextControl = b < 0x09 || (b >= 0x0E && b < 0x20) || b == 0x7F;
            if (isNonTextControl)
            {
                nonTextControls++;
            }
        }

        return nonTextControls * 100 >= header.Length * NonTextControlPercentThreshold;
    }
}
