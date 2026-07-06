namespace Psv.App.Tests;

/// <summary>
/// Redirects <see cref="SettingsStore"/> to a throwaway temp file for the lifetime of a test, so
/// constructing/closing a <see cref="MainWindow"/> never reads or overwrites the real developer
/// machine's settings under %AppData%.
/// </summary>
internal sealed class SettingsIsolation : IDisposable
{
    private readonly string _path;

    public SettingsIsolation()
    {
        _path = Path.Combine(Path.GetTempPath(), $"psv-settings-{Guid.NewGuid():N}.json");
        SettingsStore.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        SettingsStore.OverridePathForTests = null;
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
