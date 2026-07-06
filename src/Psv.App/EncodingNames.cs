using Psv.Core;

namespace Psv.App;

/// <summary>
/// Maps encoding names/aliases (for the `--encoding` CLI flag) to/from <see cref="TextEncodingKind"/>,
/// and defines the manual-override cycle order (plan §2.2.1).
/// </summary>
public static class EncodingNames
{
    public static readonly TextEncodingKind[] CycleOrder =
    [
        TextEncodingKind.Utf8,
        TextEncodingKind.Utf16LE,
        TextEncodingKind.Utf16BE,
        TextEncodingKind.Ascii,
        TextEncodingKind.Windows1252,
        TextEncodingKind.Latin1,
    ];

    // Single source of truth for the CLI alias -> encoding mapping. The user-facing
    // ValidNamesDescription is derived from this table, so the two can't drift apart.
    private static readonly (string Name, TextEncodingKind Kind)[] Aliases =
    [
        ("utf-8", TextEncodingKind.Utf8),
        ("utf8", TextEncodingKind.Utf8),
        ("utf-16le", TextEncodingKind.Utf16LE),
        ("utf16le", TextEncodingKind.Utf16LE),
        ("utf-16be", TextEncodingKind.Utf16BE),
        ("utf16be", TextEncodingKind.Utf16BE),
        ("ascii", TextEncodingKind.Ascii),
        ("windows-1252", TextEncodingKind.Windows1252),
        ("cp1252", TextEncodingKind.Windows1252),
        ("iso-8859-1", TextEncodingKind.Latin1),
        ("latin1", TextEncodingKind.Latin1),
        ("latin-1", TextEncodingKind.Latin1),
    ];

    public static readonly string ValidNamesDescription = BuildValidNamesDescription();

    public static TextEncodingKind Next(TextEncodingKind current)
    {
        int index = Array.IndexOf(CycleOrder, current);
        int nextIndex = index < 0 ? 0 : (index + 1) % CycleOrder.Length;
        return CycleOrder[nextIndex];
    }

    public static bool TryParse(string name, out TextEncodingKind kind)
    {
        string normalized = name.Trim();
        foreach (var (alias, aliasKind) in Aliases)
        {
            if (string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase))
            {
                kind = aliasKind;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static string ToDisplayName(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => "UTF-8",
        TextEncodingKind.Utf16LE => "UTF-16 LE",
        TextEncodingKind.Utf16BE => "UTF-16 BE",
        TextEncodingKind.Ascii => "ASCII",
        TextEncodingKind.Windows1252 => "Windows-1252",
        TextEncodingKind.Latin1 => "ISO-8859-1",
        _ => kind.ToString(),
    };

    private static string BuildValidNamesDescription()
    {
        var parts = new List<string>();
        foreach (var kind in CycleOrder)
        {
            string[] names = Aliases.Where(a => a.Kind == kind).Select(a => a.Name).ToArray();
            parts.Add(names.Length > 1 ? $"{names[0]} (or {string.Join(", ", names.Skip(1))})" : names[0]);
        }

        return string.Join(", ", parts);
    }
}
