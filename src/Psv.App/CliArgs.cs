using Psv.Core;

namespace Psv.App;

/// <summary><paramref name="BinBytesPerRow"/> is null when none of --bin16/--bin32/--bin64 were passed (auto-detect text vs. binary); otherwise 16, 32, or 64, forcing hex view at that row width.</summary>
public readonly record struct CliArgs(string? Path, TextEncodingKind? Encoding, bool Tail, int? BinBytesPerRow);

public static class CliArgsParser
{
    public static bool TryParse(string[] args, out CliArgs parsed, out string? error)
    {
        string? path = null;
        TextEncodingKind? encoding = null;
        bool tail = false;
        int? binBytesPerRow = null;

        foreach (string arg in args)
        {
            if (arg.StartsWith("--encoding=", StringComparison.OrdinalIgnoreCase))
            {
                string name = arg["--encoding=".Length..];
                if (!EncodingNames.TryParse(name, out var kind))
                {
                    parsed = default;
                    error = $"Unrecognized --encoding value '{name}'. Valid values: {EncodingNames.ValidNamesDescription}";
                    return false;
                }

                encoding = kind;
            }
            else if (string.Equals(arg, "--tail", StringComparison.OrdinalIgnoreCase))
            {
                tail = true;
            }
            else if (string.Equals(arg, "--bin16", StringComparison.OrdinalIgnoreCase))
            {
                binBytesPerRow = 16;
            }
            else if (string.Equals(arg, "--bin32", StringComparison.OrdinalIgnoreCase))
            {
                binBytesPerRow = 32;
            }
            else if (string.Equals(arg, "--bin64", StringComparison.OrdinalIgnoreCase))
            {
                binBytesPerRow = 64;
            }
            else if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                path ??= arg;
            }
        }

        parsed = new CliArgs(path, encoding, tail, binBytesPerRow);
        error = null;
        return true;
    }
}
