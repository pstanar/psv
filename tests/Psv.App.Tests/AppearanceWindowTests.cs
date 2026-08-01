using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Psv.App.Tests;

public class AppearanceWindowTests
{
    [AvaloniaFact]
    public void EscapeClosesTheAppearanceWindowWithoutApplyingChanges()
    {
        var window = new AppearanceWindow();
        window.Show();

        bool closed = false;
        window.Closed += (_, _) => closed = true;

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, keySymbol: null);

        Assert.True(closed, "expected Escape to close the Appearance window");
        Assert.False(window.Applied, "Escape must behave like Cancel, not OK");
    }
}
