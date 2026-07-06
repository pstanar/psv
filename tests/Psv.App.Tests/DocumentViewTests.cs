using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Psv.Core;

namespace Psv.App.Tests;

public class DocumentViewTests
{
    [AvaloniaFact]
    public void ShowingTheColumnRulerReducesVisibleLineCountByExactlyOneRow()
    {
        // The ruler occupies one row's worth of vertical space at the top of the viewport, so it
        // must shrink the number of text rows that fit by exactly one - not zero (ruler drawn over
        // the first row) and not more than one (double-counted somewhere).
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14 };
        view.Measure(new Size(800, 300));
        view.Arrange(new Rect(0, 0, 800, 300));

        view.ShowColumnRuler = false;
        int withoutRuler = view.VisibleLineCount;

        view.ShowColumnRuler = true;
        int withRuler = view.VisibleLineCount;

        Assert.Equal(withoutRuler - 1, withRuler);
    }

    [AvaloniaFact]
    public void ColumnRulerIsShownByDefault()
    {
        var view = new DocumentView();
        Assert.True(view.ShowColumnRuler);
    }

    [AvaloniaFact]
    public void HoveringOverTheGutterClearsTheHighlightedColumn()
    {
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = true };
        view.Measure(new Size(800, 300));
        view.Arrange(new Rect(0, 0, 800, 300));

        // First establish a non-null column, so the assertion actually proves the gutter cleared
        // it rather than it having never been set.
        view.SetHoverPositionForTests(new Point(700, 50));
        Assert.NotNull(view.HoverColumnForTests);

        view.SetHoverPositionForTests(new Point(2, 50));

        Assert.Null(view.HoverColumnForTests);
    }

    [AvaloniaFact]
    public void HoveringDoesNothingWhenTheColumnRulerIsHidden()
    {
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowColumnRuler = false };
        view.Measure(new Size(800, 300));
        view.Arrange(new Rect(0, 0, 800, 300));

        view.SetHoverPositionForTests(new Point(700, 50));

        Assert.Null(view.HoverColumnForTests);
    }

    [AvaloniaFact]
    public void HoveringFartherRightIncreasesTheHighlightedColumn()
    {
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = false };
        view.Measure(new Size(800, 300));
        view.Arrange(new Rect(0, 0, 800, 300));

        view.SetHoverPositionForTests(new Point(50, 50));
        long? nearer = view.HoverColumnForTests;

        view.SetHoverPositionForTests(new Point(150, 50));
        long? farther = view.HoverColumnForTests;

        Assert.NotNull(nearer);
        Assert.NotNull(farther);
        Assert.True(farther > nearer);
    }

    [AvaloniaFact]
    public void ScrollingHorizontallyShiftsTheHighlightedColumnByTheSameAmount()
    {
        // The highlight tracks an absolute column number, not a screen pixel, so hovering the same
        // screen position after scrolling must report a proportionally shifted column - otherwise
        // the highlight would silently point at the wrong character once the user scrolls.
        // The line needs to be long enough that a 20-character scroll stays within the clamped
        // HorizontalOffset range (bounded by the longest rendered line - see MaxHorizontalOffset).
        string path = Path.GetTempFileName();
        File.WriteAllText(path, new string('x', 200) + "\n");
        var document = PsvDocument.Open(path);
        document.BuildIndex();
        Window? window = null;
        try
        {
            var view = new DocumentView
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Document = document,
                ShowLineNumbers = false,
                WordWrap = false,
                Width = 800,
                Height = 300,
            };
            window = new Window { Content = view };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            view.SetHoverPositionForTests(new Point(100, 50));
            long before = view.HoverColumnForTests!.Value;

            view.HorizontalOffset = 20;
            view.SetHoverPositionForTests(new Point(100, 50));
            long after = view.HoverColumnForTests!.Value;

            Assert.Equal(before + 20, after);
        }
        finally
        {
            window?.Close();
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void TheLastVisibleColumnAtMaxHorizontalOffsetFitsEntirelyWithinTheViewport()
    {
        // Regression: DrawText insets every glyph by GutterPadding from the row's left edge, but
        // VisibleCharCount used to size the visible column count off the full row width without
        // that inset - overcounting by a fraction of a character and letting the rightmost column,
        // once scrolled all the way over, get sliced off by ClipToBounds instead of fitting on screen.
        string path = Path.GetTempFileName();
        File.WriteAllText(path, new string('x', 500) + "\n");
        var document = PsvDocument.Open(path);
        document.BuildIndex();
        Window? window = null;
        try
        {
            var view = new DocumentView
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Document = document,
                ShowLineNumbers = false,
                WordWrap = false,
                Width = 800,
                Height = 300,
            };
            window = new Window { Content = view };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            view.HorizontalOffset = long.MaxValue / 2;

            // ShowLineNumbers is off, so the gutter contributes no width - the row's glyphs start
            // GutterPaddingForTests pixels in from the left edge and run for VisibleCharCount columns.
            double lastColumnRightEdge = view.GutterPaddingForTests + (view.VisibleCharCount * view.CharWidthForTests);

            Assert.True(
                lastColumnRightEdge <= view.Bounds.Width + 0.01,
                $"Last column's right edge ({lastColumnRightEdge}) extends past the viewport width ({view.Bounds.Width}).");
        }
        finally
        {
            window?.Close();
            document.Dispose();
            File.Delete(path);
        }
    }
}
