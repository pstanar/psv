using Psv.Core;

namespace Psv.App;

public readonly record struct CliArgs(string? Path, TextEncodingKind? Encoding, bool Tail);

public static class CliArgsParser
{
    public static bool TryParse(string[] args, out CliArgs parsed, out string? error)
    {
        string? path = null;
        TextEncodingKind? encoding = null;
        bool tail = false;

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
            else if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                path ??= arg;
            }
        }

        parsed = new CliArgs(path, encoding, tail);
        error = null;
        return true;
    }
}
