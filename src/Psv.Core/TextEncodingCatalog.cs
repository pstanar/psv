using System.Text;

namespace Psv.Core;

public static class TextEncodingCatalog
{
    private static readonly Lock RegistrationLock = new();

    // Cached instances: Resolve is called once per decoded line on both the render and search
    // paths, so allocating a new Encoding per call (as a naive `new UTF8Encoding(...)` in the
    // switch would) shows up directly as per-frame garbage.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static volatile Encoding? _windows1252;

    public static Encoding Resolve(TextEncodingKind kind)
    {
        return kind switch
        {
            TextEncodingKind.Utf8 => Utf8NoBom,
            TextEncodingKind.Ascii => Encoding.ASCII,
            TextEncodingKind.Utf16LE => Encoding.Unicode,
            TextEncodingKind.Utf16BE => Encoding.BigEndianUnicode,
            TextEncodingKind.Latin1 => Encoding.Latin1,
            TextEncodingKind.Windows1252 => ResolveWindows1252(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public static bool IsWideEncoding(TextEncodingKind kind) =>
        kind is TextEncodingKind.Utf16LE or TextEncodingKind.Utf16BE;

    private static Encoding ResolveWindows1252()
    {
        if (_windows1252 is { } cached)
        {
            return cached;
        }

        lock (RegistrationLock)
        {
            if (_windows1252 is null)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _windows1252 = Encoding.GetEncoding(1252);
            }

            return _windows1252;
        }
    }
}
