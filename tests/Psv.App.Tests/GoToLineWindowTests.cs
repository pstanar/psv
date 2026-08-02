using Avalonia.Headless.XUnit;

namespace Psv.App.Tests;

public class GoToLineWindowTests
{
    [AvaloniaFact]
    public void SetLineRangeSetsMaximumAndClampsTheCurrentValue()
    {
        var dialog = new GoToLineWindow();

        dialog.SetLineRange(currentLineOneBased: 500, maxLineOneBased: 1000);

        Assert.Equal(1000, dialog.LineNumberUpDown.Maximum);
        Assert.Equal(1, dialog.LineNumberUpDown.Minimum);
        Assert.Equal(500, dialog.LineNumberUpDown.Value);
    }

    [AvaloniaFact]
    public void UpdateMaxLineWidensTheBoundWithoutTouchingTheCurrentValue()
    {
        // Regression test: the dialog's upper bound used to be captured once at open time and
        // never refreshed while it stayed open (ShowDialog runs a nested dispatcher loop, so a
        // live-tailed, actively-growing file keeps advancing KnownLineCount underneath it) -
        // rejecting/clamping a line number that had since become valid. UpdateMaxLine must widen
        // the bound alone; re-running the full SetLineRange on every growth tick would silently
        // overwrite whatever the user had already typed into the box.
        var dialog = new GoToLineWindow();
        dialog.SetLineRange(currentLineOneBased: 500, maxLineOneBased: 1000);

        dialog.LineNumberUpDown.Value = 777; // simulates the user typing a value

        dialog.UpdateMaxLine(5000);

        Assert.Equal(5000, dialog.LineNumberUpDown.Maximum);
        Assert.Equal(777, dialog.LineNumberUpDown.Value);
        Assert.Contains("5,000", dialog.RangeText.Text);
    }
}
