using System.Diagnostics;
using System.Text.Json;

namespace Psv.App;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under the OS's per-user config directory (plan
/// §3.4). Missing or corrupt files fall back to defaults rather than crashing; saving is
/// best-effort. Both directions trace a warning on failure via <see cref="Trace.TraceWarning"/> -
/// visible to a debugger or any <see cref="TraceListener"/> the host process attaches - rather
/// than swallowing the error outright, so "settings silently didn't persist" isn't a dead end for
/// a support investigation.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Redirects reads/writes to a test-controlled path instead of the real per-user config file.</summary>
    internal static string? OverridePathForTests { get; set; }

    private static string SettingsPath => OverridePathForTests ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "psv", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Psv: failed to load settings from '{SettingsPath}', using defaults: {ex}");
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        string path = SettingsPath;

        // Write-to-temp-then-rename: MainWindow bounds how long it waits on this call with a
        // timeout and lets the write keep running in the background past it, so a slow save can
        // still be in flight when the process exits. Writing the real path directly could leave a
        // half-written, corrupt settings.json in that case; renaming over it is atomic on both
        // Windows and POSIX filesystems, so the target is always either the old complete file or
        // the new complete file, never a partial one.
        string tempPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Psv: failed to save settings to '{path}': {ex}");
            TryDeleteStaleTempFile(tempPath);
        }
    }

    /// <summary>
    /// Cleans up the intermediate file if the write succeeded but the rename didn't (or the write
    /// itself failed partway through) - best-effort, since the failure is already traced above and
    /// a leftover .tmp file is cosmetic clutter, not a correctness problem the next Save/Load sees.
    /// </summary>
    private static void TryDeleteStaleTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
