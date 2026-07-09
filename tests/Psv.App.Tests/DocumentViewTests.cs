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
    public void ScrollingToEndNeverLeavesTheDocumentsLastLinePartiallyClipped()
    {
        // Mid-file, a row that only partially fits at the bottom of the viewport is drawn anyway
        // (sliced off by ClipToBounds) as a "there's more below" teaser - that's intentional. But
        // when scrolled all the way down, the document's actual last line must land in a row that
        // fully fits: there's nothing below it to justify slicing it in half.
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = false, ShowColumnRuler = false };
        double lineHeight = view.LineHeightForTests;

        // Deliberately not a multiple of the line height, so the top-of-file render exercises the
        // partially-clipped teaser row this test needs to contrast against the end-of-file case.
        double height = (5 * lineHeight) + (lineHeight / 2);

        string content = string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n"));
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        var document = PsvDocument.Open(path);
        try
        {
            document.BuildIndex();

            view.Document = document;
            view.Width = 800;
            view.Height = height;

            var window = new Window { Content = view };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            var lastRowAtTop = view.RenderedRowsForTests[^1];
            Assert.True(
                lastRowAtTop.Y + lineHeight > view.Bounds.Height,
                "test setup expects a partially-clipped teaser row while scrolled at the top");

            view.TopLine = long.MaxValue;
            window.CaptureRenderedFrame()?.Dispose();

            var lastRowAtEnd = view.RenderedRowsForTests[^1];
            Assert.Equal(49, lastRowAtEnd.LineNumber);
            Assert.True(
                lastRowAtEnd.Y + lineHeight <= view.Bounds.Height + 0.01,
                "the document's actual last line must not be clipped once scrolled to the end");

            window.Close();
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void ShrinkingTheViewportAfterScrollingToEndReclampsTopLineToAvoidClippingTheLastLine()
    {
        // Mirrors what happens once the horizontal scrollbar's row claims space only after the
        // initial layout (MainWindow's Grid row shrinks DocView when HScrollBar.IsVisible flips
        // on for a long line found after the initial scroll-to-end): the viewport shrinks *after*
        // TopLine was already pinned to the end, and the last line must not end up half-cut as a
        // result of that stale clamp.
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = false, ShowColumnRuler = false };
        double lineHeight = view.LineHeightForTests;
        double tallHeight = 6 * lineHeight;

        string content = string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n"));
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        var document = PsvDocument.Open(path);
        try
        {
            document.BuildIndex();

            view.Document = document;
            view.Width = 800;
            view.Height = tallHeight;

            var window = new Window { Content = view };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            view.TopLine = long.MaxValue;
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(49, view.RenderedRowsForTests[^1].LineNumber);

            // Shrink the viewport the way DocView's Grid row shrinks once HScrollBar.IsVisible
            // flips on - without a matching change to TopLine, the row that used to exactly fit
            // the taller viewport no longer fits the shorter one.
            view.Height = tallHeight - (lineHeight / 2);
            window.CaptureRenderedFrame()?.Dispose();

            var lastRow = view.RenderedRowsForTests[^1];
            Assert.True(
                lastRow.Y + lineHeight <= view.Bounds.Height + 0.01,
                "the last line must not be clipped after the viewport shrinks out from under an end-of-file scroll position");

            window.Close();
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void LastMaxLineLengthNeverShrinksWhenTheLongestLineScrollsOutOfView()
    {
        // LastMaxLineLength is only ever computed over whichever rows the current Render() pass
        // happened to draw (scanning the whole file up front would be O(file size) on every
        // scroll), not the whole document. Scrolling the one long line out of view must not pull
        // this back down - that previously let it swing between two values (e.g. once the
        // horizontal scrollbar's own height reservation changed how many rows fit, scrolling that
        // line in and out of the visible set), flipping the horizontal scrollbar on/off forever.
        var view = new DocumentView { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = false, ShowColumnRuler = false, Width = 800 };
        double lineHeight = view.LineHeightForTests;
        view.Height = 5 * lineHeight;

        string longLine = new string('x', 200);
        string content = $"short0\n{longLine}\n" + string.Concat(Enumerable.Range(0, 50).Select(i => $"s{i}\n"));
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        var document = PsvDocument.Open(path);
        try
        {
            document.BuildIndex();

            view.Document = document;
            var window = new Window { Content = view };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            Assert.Equal(200, view.LastMaxLineLength);

            // Scroll well past the long line - the rendered rows from here on are all short.
            view.TopLine = 40;
            window.CaptureRenderedFrame()?.Dispose();

            Assert.Equal(200, view.LastMaxLineLength);

            window.Close();
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
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
