using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Psv.Core;

namespace Psv.App.Tests;

public class HexSelectionTests
{
    private static PsvDocument OpenTempBinaryDocument(byte[] content, out string path)
    {
        path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        return PsvDocument.Open(path, forceBinary: true);
    }

    private static byte[] SequentialBytes(int count)
    {
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)i;
        }

        return bytes;
    }

    /// <summary>Mirrors TextSelectionTests.CreateView - see there for why a shown-and-closed window is needed to force a real render pass.</summary>
    private static HexView CreateView(PsvDocument document)
    {
        var view = CreateAttachedView(document, out var window);
        window.Close();
        return view;
    }

    private static HexView CreateAttachedView(PsvDocument document, out Window window)
    {
        var view = new HexView
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Document = document,
            Width = 800,
            Height = 300,
        };

        window = new Window { Content = view };
        window.Show();
        window.CaptureRenderedFrame()?.Dispose();

        return view;
    }

    private static Point HexCellCenter(HexView view, int row, int byteIndex)
    {
        var layout = view.LayoutForTests;
        double charWidth = view.CharWidthForTests;
        double midGap = byteIndex >= HexView.BytesPerRow / 2 ? charWidth : 0;
        double cellStart = layout.HexPaneX + (byteIndex * 3 * charWidth) + midGap;
        return new Point(cellStart + charWidth, (row * view.LineHeightForTests) + (view.LineHeightForTests / 2));
    }

    private static Point AsciiCellCenter(HexView view, int row, int byteIndex)
    {
        var layout = view.LayoutForTests;
        double charWidth = view.CharWidthForTests;
        double cellStart = layout.AsciiPaneX + (byteIndex * charWidth);
        return new Point(cellStart + (charWidth / 2), (row * view.LineHeightForTests) + (view.LineHeightForTests / 2));
    }

    [AvaloniaFact]
    public void StreamSelectionAcrossMultipleRowsProducesContiguousByteRange()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        try
        {
            var view = CreateView(document);

            // Byte 10 (row 0, col 10) through byte 20 (row 1, col 4), inclusive on both ends.
            view.SetSelectionForTests(new BytePosition(10), new BytePosition(20), rectangular: false, startedInAsciiPane: false);

            byte[] expected = SequentialBytes(64)[10..21];
            Assert.Equal(expected, view.SelectedBytesForTests);
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
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(20), new BytePosition(10), rectangular: false, startedInAsciiPane: false);

            byte[] expected = SequentialBytes(64)[10..21];
            Assert.Equal(expected, view.SelectedBytesForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RectangularSelectionExtractsSameColumnRangeFromEveryRow()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(48), out string path);
        try
        {
            var view = CreateView(document);

            // Columns [2, 5) (inclusive of 2,3,4) from rows 0-2.
            view.SetSelectionForTests(new BytePosition(2), new BytePosition(36), rectangular: true, startedInAsciiPane: false);

            byte[] expected = [2, 3, 4, 18, 19, 20, 34, 35, 36];
            Assert.Equal(expected, view.SelectedBytesForTests);
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
        var document = OpenTempBinaryDocument(SequentialBytes(32), out string path);
        try
        {
            var view = CreateView(document);
            var position = new BytePosition(5);

            view.SetSelectionForTests(position, position, rectangular: false, startedInAsciiPane: false);

            Assert.Null(view.SelectedBytesForTests);
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
        var documentA = OpenTempBinaryDocument(SequentialBytes(32), out string pathA);
        var documentB = OpenTempBinaryDocument(SequentialBytes(32), out string pathB);
        try
        {
            var view = CreateView(documentA);
            view.SetSelectionForTests(new BytePosition(0), new BytePosition(5), rectangular: false, startedInAsciiPane: false);
            Assert.NotNull(view.SelectedBytesForTests);

            view.Document = documentB;

            Assert.Null(view.SelectedBytesForTests);
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
    public void SelectionStartedInHexPaneCopiesSpaceSeparatedUppercaseHexPairs()
    {
        var document = OpenTempBinaryDocument([0x4D, 0x5A, 0x90, 0x00], out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(0), new BytePosition(3), rectangular: false, startedInAsciiPane: false);

            Assert.Equal("4D 5A 90 00", view.HexCopyTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void SelectionStartedInAsciiPaneCopiesDecodedCharactersWithDotsForNonPrintable()
    {
        // 'A' (printable), then a NUL and 0x01 (both non-printable -> '.'), then 'B'.
        var document = OpenTempBinaryDocument([(byte)'A', 0x00, 0x01, (byte)'B'], out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(0), new BytePosition(3), rectangular: false, startedInAsciiPane: true);

            Assert.Equal("A..B", view.AsciiCopyTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void ExtendedWindows1252BytesRenderAsTheirGlyphInsteadOfADot()
    {
        // 0xE9 is 'é' in Windows-1252 - a real printable character despite the high bit being
        // set, unlike the pure-ASCII range this used to be limited to.
        var document = OpenTempBinaryDocument([(byte)'A', 0xE9, (byte)'B'], out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(0), new BytePosition(2), rectangular: false, startedInAsciiPane: true);

            Assert.Equal("AéB", view.AsciiCopyTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void UndefinedWindows1252BytesStillRenderAsADot()
    {
        // 0x81 has no assigned character in Windows-1252 (.NET decodes it to the C1 control
        // U+0081) - still non-printable, just via a different code page's gap rather than ASCII's.
        var document = OpenTempBinaryDocument([(byte)'A', 0x81, (byte)'B'], out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(0), new BytePosition(2), rectangular: false, startedInAsciiPane: true);

            Assert.Equal("A.B", view.AsciiCopyTextForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void BothPanesHighlightTheSameBytesRegardlessOfWhichPaneTheDragStartedIn()
    {
        // The copy-format hooks read the same underlying selected byte range regardless of
        // _selectionStartedInAsciiPane - that field only ever changes which ONE of the two
        // formats CopySelectionToClipboardAsync picks, never what's selected/highlighted.
        var document = OpenTempBinaryDocument([0x4D, 0x5A, 0x90, 0x00], out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(0), new BytePosition(3), rectangular: false, startedInAsciiPane: false);
            string? hexTextWhenStartedInHexPane = view.HexCopyTextForTests;
            string? asciiTextWhenStartedInHexPane = view.AsciiCopyTextForTests;

            view.SetSelectionForTests(new BytePosition(0), new BytePosition(3), rectangular: false, startedInAsciiPane: true);
            string? hexTextWhenStartedInAsciiPane = view.HexCopyTextForTests;
            string? asciiTextWhenStartedInAsciiPane = view.AsciiCopyTextForTests;

            Assert.Equal(hexTextWhenStartedInHexPane, hexTextWhenStartedInAsciiPane);
            Assert.Equal(asciiTextWhenStartedInHexPane, asciiTextWhenStartedInAsciiPane);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HitTestMapsPixelsInHexColumnsToTheCorrectByteOffset()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(32), out string path);
        try
        {
            var view = CreateView(document);

            var hit = view.HitTestForTests(HexCellCenter(view, row: 1, byteIndex: 5));

            Assert.NotNull(hit);
            Assert.Equal(16 + 5, hit.Value.Position.ByteOffset);
            Assert.False(hit.Value.InAsciiPane);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HitTestMapsPixelsInAsciiColumnsToTheCorrectByteOffset()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(32), out string path);
        try
        {
            var view = CreateView(document);

            var hit = view.HitTestForTests(AsciiCellCenter(view, row: 1, byteIndex: 5));

            Assert.NotNull(hit);
            Assert.Equal(16 + 5, hit.Value.Position.ByteOffset);
            Assert.True(hit.Value.InAsciiPane);
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
        var document = OpenTempBinaryDocument(SequentialBytes(32), out string path);
        try
        {
            var view = CreateView(document);

            var hit = view.HitTestForTests(new Point(view.LayoutForTests.HexPaneX, 10_000));

            Assert.NotNull(hit);
            Assert.Equal(16, hit.Value.Position.ByteOffset);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void LastPartialRowClampsSelectionToActualByteCount()
    {
        // 20 bytes: one full 16-byte row, then a partial row of 4 bytes.
        var document = OpenTempBinaryDocument(SequentialBytes(20), out string path);
        try
        {
            var view = CreateView(document);

            // A rectangular selection spanning both rows across the full column range - the
            // partial row must contribute only its 4 actual bytes, not pad up to 16.
            view.SetSelectionForTests(new BytePosition(0), new BytePosition(31), rectangular: true, startedInAsciiPane: false);

            byte[]? selected = view.SelectedBytesForTests;
            Assert.NotNull(selected);
            Assert.Equal(20, selected!.Length);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RectangularHexCopyPadsPartialTrailingRowToPreserveShape()
    {
        // 20 bytes: one full 16-byte row, then a partial row of 4 (offsets 16-19).
        var document = OpenTempBinaryDocument(SequentialBytes(20), out string path);
        try
        {
            var view = CreateView(document);

            // Columns [2,5] (width 4) across rows 0-1 - row 1 only has real bytes at columns 0-3,
            // so columns 4 and 5 there are blank. A flat/unshaped copy would produce a shorter
            // second line ("12 13"); the fix must instead pad it to the same width as row 0.
            view.SetSelectionForTests(new BytePosition(2), new BytePosition(16 + 5), rectangular: true, startedInAsciiPane: false);

            string[] lines = (view.HexCopyTextForTests ?? string.Empty).Split(Environment.NewLine);
            Assert.Equal(2, lines.Length);
            Assert.Equal("02 03 04 05", lines[0]);
            Assert.Equal(string.Join(" ", "12", "13", "  ", "  "), lines[1]);
            Assert.Equal(lines[0].Length, lines[1].Length);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RectangularAsciiCopyPadsPartialTrailingRowToPreserveShape()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(20), out string path);
        try
        {
            var view = CreateView(document);

            view.SetSelectionForTests(new BytePosition(2), new BytePosition(16 + 5), rectangular: true, startedInAsciiPane: true);

            string[] lines = (view.AsciiCopyTextForTests ?? string.Empty).Split(Environment.NewLine);
            Assert.Equal(2, lines.Length);
            // Bytes 2-5 and 18-19 are all non-printable control bytes ('.'); the two blank
            // columns on the short row must render as literal spaces, not '.', so a paste can't
            // mistake "no byte here" for "an actual non-printable byte was here".
            Assert.Equal("....", lines[0]);
            Assert.Equal("..  ", lines[1]);
            Assert.Equal(lines[0].Length, lines[1].Length);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task CopyingASelectionStartedInTheHexPanePutsHexTextOnTheSystemClipboard()
    {
        Window? window = null;
        var document = OpenTempBinaryDocument([0x4D, 0x5A, 0x90, 0x00], out string path);
        try
        {
            var view = CreateAttachedView(document, out window);
            view.SetSelectionForTests(new BytePosition(0), new BytePosition(3), rectangular: false, startedInAsciiPane: false);

            await view.CopySelectionToClipboardAsync();

            string? clipboardText = await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync();
            Assert.Equal("4D 5A 90 00", clipboardText);
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
        Window? window = null;
        var document = OpenTempBinaryDocument([0x41, 0x42, 0x43], out string path);
        try
        {
            var view = CreateAttachedView(document, out window);
            view.SetSelectionForTests(new BytePosition(0), new BytePosition(2), rectangular: false, startedInAsciiPane: false);

            view.SimulateCtrlCForTests();

            string? clipboardText = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                clipboardText = await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync();
                if (clipboardText == "41 42 43")
                {
                    break;
                }

                await Task.Delay(20);
            }

            Assert.Equal("41 42 43", clipboardText);
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
        var document = OpenTempBinaryDocument(SequentialBytes(16), out string path);
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
        var document = OpenTempBinaryDocument(SequentialBytes(16 * 200), out string path);
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
    public void AutoScrollTickAdvancesTopLineAndExtendsSelectionToTheNewBottomEdge()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(16 * 200), out string path);
        try
        {
            var view = CreateView(document);
            view.SetSelectionForTests(new BytePosition(2), new BytePosition(2), rectangular: false, startedInAsciiPane: false);
            view.SetIsSelectingForTests(true);
            long topLineBefore = view.TopLine;

            view.UpdateAutoScrollForTests(view.Bounds.Height + 10);
            view.TriggerAutoScrollTickForTests();

            Assert.Equal(topLineBefore + 1, view.TopLine);
            var expectedEdge = new BytePosition(((view.TopLine + view.VisibleRowCount - 1) * HexView.BytesPerRow) + 2);
            Assert.Equal(expectedEdge, view.SelectionFocusForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void EndingARectangularDragReclaimsFocusEvenIfSomethingStoleItMidDrag()
    {
        // Regression: a rectangular drag holds Alt for its whole duration (see OnPointerPressed).
        // Releasing a "bare" Alt tap at the end of the gesture can be read by Avalonia's own
        // access-key/menu-mnemonic handling as a request to move keyboard focus to the menu bar -
        // silently breaking Ctrl+C and every other shortcut afterward, even though the mouse never
        // left this control. Simulates that focus theft directly (a focusable sibling grabs focus
        // mid-drag) rather than relying on the real access-key handler actually firing, since that
        // behavior is environment-dependent and wasn't reproducible under headless/synthetic input.
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        Window? window = null;
        try
        {
            var view = new HexView { FontFamily = new FontFamily("Consolas"), FontSize = 14, Document = document, Width = 800, Height = 300 };
            var focusThief = new Button { Content = "steal focus" };
            var panel = new StackPanel();
            panel.Children.Add(view);
            panel.Children.Add(focusThief);

            window = new Window { Content = panel };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            // The view has an explicit Width/Height inside the StackPanel, so it isn't necessarily
            // at the window's origin (it can end up centered) - translate view-local points into
            // window coordinates rather than assuming the two coordinate spaces coincide.
            var start = view.TranslatePoint(HexCellCenter(view, 0, 2), window) ?? default;
            var end = view.TranslatePoint(HexCellCenter(view, 2, 5), window) ?? default;

            window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseMove(end, RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);

            focusThief.Focus();
            Assert.False(view.IsFocused, "test setup: the sibling must actually have stolen focus before the drag ends");
            Assert.NotNull(view.SelectedBytesForTests); // sanity check: the drag actually landed on the view

            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);

            Assert.True(view.IsFocused, "expected ending the rectangular drag to reclaim keyboard focus");
        }
        finally
        {
            window?.Close();
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void ReleasingAltAfterMouseUpReclaimsFocusEvenThoughOnPointerReleasedAlreadyRanOnce()
    {
        // Regression: a real Alt+drag releases the mouse button before the user lets go of Alt,
        // so OnPointerReleased's own focus fix runs first - then Avalonia's access-key/menu
        // handling reacts to the later, separate Alt key-up and steals focus right back. Confirmed
        // against the real report: releasing Alt after a rectangular selection visibly selected
        // the File menu, and OnPointerReleased's fix alone did not prevent it.
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        Window? window = null;
        try
        {
            var view = new HexView { FontFamily = new FontFamily("Consolas"), FontSize = 14, Document = document, Width = 800, Height = 300 };
            var focusThief = new Button { Content = "steal focus" };
            var panel = new StackPanel();
            panel.Children.Add(view);
            panel.Children.Add(focusThief);

            window = new Window { Content = panel };
            window.Show();
            window.CaptureRenderedFrame()?.Dispose();

            view.Focus();
            Assert.True(view.IsFocused);

            // Simulate the access-key handler's tunnel-phase focus steal, which happens when the
            // user actually releases Alt - chronologically after OnPointerReleased already ran.
            focusThief.Focus();
            Assert.False(view.IsFocused, "test setup: focus must be stolen before simulating the Alt key-up");

            view.SimulateAltKeyUpForTests();

            Assert.True(view.IsFocused, "expected releasing Alt to reclaim keyboard focus a second time");
        }
        finally
        {
            window?.Close();
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void StartingOmniScrollSetsACustomCursor()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(16), out string path);
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
    public void OmniScrollTickScrollsDownwardWhenThePointerMovesBelowTheAnchor()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(16 * 200), out string path);
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
}
