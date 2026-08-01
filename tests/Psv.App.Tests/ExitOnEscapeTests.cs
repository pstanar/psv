using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Psv.App.Tests;

public class ExitOnEscapeTests
{
    [AvaloniaFact]
    public void ExitOnEscapeIsDisabledByDefault()
    {
        using var isolation = new SettingsIsolation();
        var window = new MainWindow();
        try
        {
            Assert.False(window.ExitOnEscapeForTests);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeDoesNotCloseTheWindowWhenTheOptionIsDisabled()
    {
        using var isolation = new SettingsIsolation();
        var window = new MainWindow();
        window.Show();

        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);

        Assert.False(closed, "Escape must not close the window unless Exit on Esc is enabled");
        window.Close();
    }

    [AvaloniaFact]
    public void EscapeClosesTheWindowWhenTheOptionIsEnabled()
    {
        using var isolation = new SettingsIsolation();
        var window = new MainWindow();
        window.Show();
        window.SetExitOnEscapeForTests(true);

        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);

        Assert.True(closed, "expected Escape to close the window once Exit on Esc is enabled");
    }
}
