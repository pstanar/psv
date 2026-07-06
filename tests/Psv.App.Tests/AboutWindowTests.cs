using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Psv.App.Tests;

public class AboutWindowTests
{
    [AvaloniaFact]
    public void EscapeClosesTheAboutWindow()
    {
        var window = new AboutWindow();
        window.Show();

        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);

        Assert.True(closed, "expected Escape to close the About window");
    }
}
