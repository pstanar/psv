using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Psv.Core;

namespace Psv.App.Tests;

public class TextSelectionTests
{
    private static PsvDocument OpenTempDocument(string content, out string path)
    {
        path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        var document = PsvDocument.Open(path);
        document.BuildIndex();
        return document;
    }

    /// <summary>
    /// HitTest relies on the row layout _renderedRows caches during Render(), which Measure()
    /// and Arrange() alone don't trigger - the view needs to be in an actual shown window with a
    /// captured frame to force a real render pass. The window is closed immediately afterward: a
    /// still-open window keeps rendering asynchronously on the compositor's own schedule, which
    /// then throws once the test's own cleanup disposes the PsvDocument out from under it.
    /// </summary>
    private static DocumentView CreateView(PsvDocument document)
    {
        var view = CreateAttachedView(document, out var window);
        window.Close();
        return view;
    }

    private static DocumentView CreateAttachedView(PsvDocument document, out Window window)
    {
        var view = new DocumentView
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Document = document,
            ShowLineNumbers = false,
            Width = 800,
            Height = 300,
        };

        window = new Window { Content = view };
        window.Show();
        window.CaptureRenderedFrame()?.Dispose();

        return view;
    }

    [AvaloniaFact]
    public void StreamSelectionSpanningMultipleLinesTrimsTheEndpointsAndKeepsMiddleLinesWhole()
    {
        var document = OpenTempDocument("line0\nline1\nline2\nline3\n", out string path);
        try
        {
            var view = CreateView(document);

            // Column 2 into "line0" (skips "li"), through column 3 into "line2" (up to "lin").
            view.SetSelectionForTests(new DocumentPosition(0, 2), new DocumentPosition(2, 3), rectangular: false);

            string expected = string.Join(Environment.NewLine, "ne0", "line1", "lin");
            Assert.Equal(expected, view.SelectedTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void StreamSelectionIsOrderIndependentBetweenAnchorAndFocus()
    {
        var document = OpenTempDocument("line0\nline1\nline2\nline3\n", out string path);
        try
        {
            var view = CreateView(document);

            // Same two endpoints as the test above, but dragged in the opposite direction.
            view.SetSelectionForTests(new DocumentPosition(2, 3), new DocumentPosition(0, 2), rectangular: false);

            string expected = string.Join(Environment.NewLine, "ne0", "line1", "lin");
            Assert.Equal(expected, view.SelectedTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RectangularSelectionExtractsTheSameColumnRangeFromEveryLineRegardlessOfLength()
    {
        var document = OpenTempDocument("abcdefgh\nab\nabcdef\n", out string path);
        try
        {
            var view = CreateView(document);

            // Columns [2, 5) from each of the three lines - the middle line is shorter than
            // column 2, so it must contribute an empty string rather than throwing or wrapping.
            view.SetSelectionForTests(new DocumentPosition(0, 2), new DocumentPosition(2, 5), rectangular: true);

            string expected = string.Join(Environment.NewLine, "cde", string.Empty, "cde");
            Assert.Equal(expected, view.SelectedTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RectangularSelectionOnASingleLineBehavesLikeAStreamSelection()
    {
        var document = OpenTempDocument("abcdefgh\n", out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new DocumentPosition(0, 2), new DocumentPosition(0, 5), rectangular: true);

            Assert.Equal("cde", view.SelectedTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void AnchorEqualToFocusMeansNoSelection()
    {
        var document = OpenTempDocument("line0\nline1\n", out string path);
        try
        {
            var view = CreateView(document);
            var position = new DocumentPosition(0, 2);

            view.SetSelectionForTests(position, position, rectangular: false);

            Assert.Null(view.SelectedTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OpeningANewDocumentClearsAnyExistingSelection()
    {
        var documentA = OpenTempDocument("aaaa\nbbbb\n", out string pathA);
        var documentB = OpenTempDocument("cccc\ndddd\n", out string pathB);
        try
        {
            var view = CreateView(documentA);
            view.SetSelectionForTests(new DocumentPosition(0, 0), new DocumentPosition(1, 2), rectangular: false);
            Assert.NotNull(view.SelectedTextForTests);

            view.Document = documentB;

            Assert.Null(view.SelectedTextForTests);
        }
        finally
        {
            documentA.Dispose();
            documentB.Dispose();
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [AvaloniaFact]
    public void HitTestFartherRightProducesAHigherColumn()
    {
        var document = OpenTempDocument("abcdefghijklmnopqrstuvwxyz\n", out string path);
        try
        {
            var view = CreateView(document);

            var nearer = view.HitTestForTests(new Point(50, 50));
            var farther = view.HitTestForTests(new Point(150, 50));

            Assert.NotNull(nearer);
            Assert.NotNull(farther);
            Assert.True(farther.Value.Column > nearer.Value.Column);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HitTestAboveTheViewportClampsToTheFirstVisibleRow()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);

            var hit = view.HitTestForTests(new Point(20, -1000));

            Assert.NotNull(hit);
            Assert.Equal(0, hit.Value.Line);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HitTestBelowTheViewportClampsToTheLastVisibleRow()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);

            var hit = view.HitTestForTests(new Point(20, 10_000));

            Assert.NotNull(hit);
            Assert.Equal(2, hit.Value.Line);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task CopyingTheSelectionPutsItOnTheSystemClipboard()
    {
        var document = OpenTempDocument("copy-me-line0\ncopy-me-line1\n", out string path);
        Window? window = null;
        try
        {
            var view = CreateAttachedView(document, out window);

            view.SetSelectionForTests(new DocumentPosition(0, 0), new DocumentPosition(1, 13), rectangular: false);
            string? expected = view.SelectedTextForTests;

            await view.CopySelectionToClipboardAsync();

            string? clipboardText = await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync();
            Assert.Equal(expected, clipboardText);
        }
        finally
        {
            window?.Close();
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task CtrlCCopiesTheSelectionToTheClipboard()
    {
        var document = OpenTempDocument("copy-me-line0\ncopy-me-line1\n", out string path);
        Window? window = null;
        try
        {
            var view = CreateAttachedView(document, out window);
            view.SetSelectionForTests(new DocumentPosition(0, 0), new DocumentPosition(1, 13), rectangular: false);
            string? expected = view.SelectedTextForTests;

            view.SimulateCtrlCForTests();

            // The key handler fires the copy but doesn't await it, so give the posted task a
            // moment to complete before reading the clipboard back.
            string? clipboardText = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                clipboardText = await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync();
                if (clipboardText == expected)
                {
                    break;
                }

                await Task.Delay(20);
            }

            Assert.Equal(expected, clipboardText);
        }
        finally
        {
            window?.Close();
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void CtrlCWithNoSelectionDoesNotThrow()
    {
        var document = OpenTempDocument("line0\nline1\n", out string path);
        try
        {
            var view = CreateView(document);

            var exception = Record.Exception(() => view.SimulateCtrlCForTests());

            Assert.Null(exception);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void DraggingPastTheBottomEdgeStartsDownwardAutoScroll()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);

            view.UpdateAutoScrollForTests(view.Bounds.Height + 10);

            Assert.Equal(1, view.AutoScrollDirectionForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void DraggingPastTheTopEdgeStartsUpwardAutoScroll()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);

            view.UpdateAutoScrollForTests(-10);

            Assert.Equal(-1, view.AutoScrollDirectionForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void MovingBackInsideTheViewportStopsAutoScroll()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);
            view.UpdateAutoScrollForTests(-10);
            Assert.NotEqual(0, view.AutoScrollDirectionForTests);

            view.UpdateAutoScrollForTests(view.Bounds.Height / 2);

            Assert.Equal(0, view.AutoScrollDirectionForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void AutoScrollTickAdvancesTopLineAndExtendsSelectionToTheNewBottomEdge()
    {
        var document = OpenTempDocument(string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n")), out string path);
        try
        {
            var view = CreateView(document);
            view.SetSelectionForTests(new DocumentPosition(0, 2), new DocumentPosition(0, 2), rectangular: false);
            view.SetIsSelectingForTests(true);
            long topLineBefore = view.TopLine;

            view.UpdateAutoScrollForTests(view.Bounds.Height + 10);
            view.TriggerAutoScrollTickForTests();

            Assert.Equal(topLineBefore + 1, view.TopLine);
            var expectedEdge = new DocumentPosition(view.TopLine + view.VisibleLineCount - 1, 2);
            Assert.Equal(expectedEdge, view.SelectionFocusForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void AutoScrollTickDoesNothingWhenNotActivelySelecting()
    {
        // A stray timer tick that outlives the drag (e.g. the very tick during which
        // OnPointerReleased already fired) must not keep scrolling.
        var document = OpenTempDocument(string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n")), out string path);
        try
        {
            var view = CreateView(document);
            view.SetSelectionForTests(new DocumentPosition(0, 2), new DocumentPosition(0, 2), rectangular: false);
            long topLineBefore = view.TopLine;

            view.UpdateAutoScrollForTests(view.Bounds.Height + 10);
            view.TriggerAutoScrollTickForTests();

            Assert.Equal(topLineBefore, view.TopLine);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void StartingOmniScrollSetsACustomCursor()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);
            Assert.Null(view.Cursor);

            view.StartOmniScrollForTests(new Point(50, 50));

            Assert.True(view.IsOmniScrollingForTests);
            Assert.NotNull(view.Cursor);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void StoppingOmniScrollRestoresTheDefaultCursor()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);
            view.StartOmniScrollForTests(new Point(50, 50));

            view.StopOmniScrollForTests();

            Assert.False(view.IsOmniScrollingForTests);
            Assert.Null(view.Cursor);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OmniScrollTickDoesNothingWithinTheDeadZoneAroundTheAnchor()
    {
        var document = OpenTempDocument(string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n")), out string path);
        try
        {
            var view = CreateView(document);
            var anchor = new Point(50, 50);
            view.StartOmniScrollForTests(anchor);
            view.MoveOmniScrollPointerForTests(anchor + new Point(2, 2));
            long topLineBefore = view.TopLine;

            view.TriggerOmniScrollTickForTests();

            Assert.Equal(topLineBefore, view.TopLine);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OmniScrollTickScrollsDownwardWhenThePointerMovesBelowTheAnchor()
    {
        var document = OpenTempDocument(string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n")), out string path);
        try
        {
            var view = CreateView(document);
            var anchor = new Point(50, 50);
            view.StartOmniScrollForTests(anchor);
            view.MoveOmniScrollPointerForTests(anchor + new Point(0, 40));
            long topLineBefore = view.TopLine;

            view.TriggerOmniScrollTickForTests();

            Assert.True(view.TopLine > topLineBefore);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OmniScrollTickScrollsUpwardWhenThePointerMovesAboveTheAnchor()
    {
        var document = OpenTempDocument(string.Concat(Enumerable.Range(0, 50).Select(i => $"line{i}\n")), out string path);
        try
        {
            var view = CreateView(document);
            view.TopLine = 20;
            var anchor = new Point(50, 50);
            view.StartOmniScrollForTests(anchor);
            view.MoveOmniScrollPointerForTests(anchor - new Point(0, 40));
            long topLineBefore = view.TopLine;

            view.TriggerOmniScrollTickForTests();

            Assert.True(view.TopLine < topLineBefore);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void EscapeKeyStopsAnActiveOmniScroll()
    {
        var document = OpenTempDocument("line0\nline1\nline2\n", out string path);
        try
        {
            var view = CreateView(document);
            view.StartOmniScrollForTests(new Point(50, 50));

            view.SimulateEscapeForTests();

            Assert.False(view.IsOmniScrollingForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void LinesContainingTabsRenderOnTheSamePerCharacterGridAsSelectionHighlighting()
    {
        // Regression: FormattedText expands '\t' to the next tab stop instead of one _charWidth
        // cell, so a line's rendered width used to outgrow the highlight rectangle selection/match
        // highlighting compute from raw character counts - text after a tab looked unselected even
        // though it was. Rendered width must stay exactly text.Length * CharWidth.
        var document = OpenTempDocument("a\tb\n", out string path);
        try
        {
            var view = CreateView(document);
            string text = "a\tb";

            double renderedWidth = view.MeasureRenderedTextWidthForTests(text);

            Assert.Equal(text.Length * view.CharWidthForTests, renderedWidth, precision: 3);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void SettingHorizontalOffsetPastTheLongestLineClampsToItsRightEdge()
    {
        // Regression: keyboard Right-arrow and middle-click omniscroll both just add to
        // HorizontalOffset - without a clamp here they could scroll arbitrarily far past the
        // longest line, unlike the horizontal scroll bar, which is bounded by the same content.
        var document = OpenTempDocument(new string('x', 40) + "\n", out string path);
        try
        {
            var view = CreateView(document);
            long expectedMax = Math.Max(0, view.LastMaxLineLength - view.VisibleCharCount);

            view.HorizontalOffset = 10_000;

            Assert.Equal(expectedMax, view.HorizontalOffset);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void OmniScrollDoesNotScrollHorizontallyPastTheLongestLine()
    {
        var document = OpenTempDocument(new string('x', 40) + "\n", out string path);
        try
        {
            var view = CreateView(document);
            long expectedMax = Math.Max(0, view.LastMaxLineLength - view.VisibleCharCount);
            var anchor = new Point(50, 50);
            view.StartOmniScrollForTests(anchor);
            view.MoveOmniScrollPointerForTests(anchor + new Point(5000, 0));

            for (int i = 0; i < 20; i++)
            {
                view.TriggerOmniScrollTickForTests();
            }

            Assert.Equal(expectedMax, view.HorizontalOffset);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }
}
