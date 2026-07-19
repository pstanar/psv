using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Psv.Core;

namespace Psv.App.Tests;

/// <summary>Covers the configurable <see cref="HexView.BytesPerRow"/> (16/32/64) and its two-tier mid-row separator gap - see HexSelectionTests for the fixed-16-byte-row selection/hit-test behavior this builds on.</summary>
public class HexRowWidthTests
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

    private static HexView CreateView(PsvDocument document, int bytesPerRow, double width = 800)
    {
        var view = new HexView
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Document = document,
            BytesPerRow = bytesPerRow,
            Width = width,
            Height = 300,
        };

        var window = new Window { Content = view };
        window.Show();
        window.CaptureRenderedFrame()?.Dispose();
        window.Close();

        return view;
    }

    [AvaloniaFact]
    public void DefaultBytesPerRowIsThirtyTwo()
    {
        var view = new HexView();

        Assert.Equal(32, view.BytesPerRow);
        Assert.Equal(32, HexView.DefaultBytesPerRow);
    }

    [AvaloniaTheory]
    [InlineData(32)]
    [InlineData(64)]
    public void CellSpacingIsNarrowAtEightByteBoundariesAndWideAtSixteenByteBoundaries(int bytesPerRow)
    {
        var document = OpenTempBinaryDocument(SequentialBytes(bytesPerRow), out string path);
        try
        {
            var view = CreateView(document, bytesPerRow);
            double charWidth = view.CharWidthForTests;

            for (int i = 1; i < bytesPerRow; i++)
            {
                double spacing = view.HexByteXForTests(i) - view.HexByteXForTests(i - 1);
                double expected = i % 16 == 0 ? 5 * charWidth : i % 8 == 0 ? 4 * charWidth : 3 * charWidth;
                Assert.Equal(expected, spacing, precision: 5);
            }
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void StreamSelectionAcrossMultipleRowsWorksAtSixtyFourBytesPerRow()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(128), out string path);
        try
        {
            var view = CreateView(document, bytesPerRow: 64);

            // Byte 60 (row 0) through byte 70 (row 1), inclusive on both ends.
            view.SetSelectionForTests(new BytePosition(60), new BytePosition(70), rectangular: false, startedInAsciiPane: false);

            byte[] expected = SequentialBytes(128)[60..71];
            Assert.Equal(expected, view.SelectedBytesForTests);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void IncreasingBytesPerRowClampsTopLineToTheNewSmallerMaxTopLine()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(16 * 1000), out string path);
        try
        {
            var view = CreateView(document, bytesPerRow: 16);
            view.TopLine = view.MaxTopLineForTests;
            Assert.True(view.TopLine > 0, "test setup: expected the 16-byte-row document to scroll past the first page");

            // Quadrupling the row width quarters the row count, so the old TopLine (valid for the
            // 16-byte layout) can now overshoot the document's new, much shorter, end.
            view.BytesPerRow = 64;

            Assert.Equal(view.MaxTopLineForTests, view.TopLine);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void WiderRowsOverflowAnUnchangedViewportMoreThanNarrowerRows()
    {
        // Regression: BytesPerRow used to be a fixed 16, which always fit a normal window - once it
        // became configurable, nothing scrolled the hex/ASCII panes into view past the right edge.
        // Asserted relatively (not "16 never overflows 800px") since the exact crossover point is a
        // function of the active font's glyph width, which varies by environment/font substitution.
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        try
        {
            var narrowRowView = CreateView(document, bytesPerRow: 16, width: 800);
            var wideRowView = CreateView(document, bytesPerRow: 64, width: 800);

            Assert.True(
                wideRowView.MaxHorizontalOffsetValue > narrowRowView.MaxHorizontalOffsetValue,
                "expected a 64-byte row to overflow an unchanged viewport more than a 16-byte row");
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HorizontalOffsetClampsToMaxHorizontalOffset()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        try
        {
            var view = CreateView(document, bytesPerRow: 64, width: 300);
            Assert.True(view.MaxHorizontalOffsetValue > 0, "test setup: expected overflow at this width");

            view.HorizontalOffset = view.MaxHorizontalOffsetValue + 1000;

            Assert.Equal(view.MaxHorizontalOffsetValue, view.HorizontalOffset);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void HitTestAccountsForHorizontalScrollWhenContentOverflows()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        try
        {
            var view = CreateView(document, bytesPerRow: 64, width: 300);
            Assert.True(view.MaxHorizontalOffsetValue > 0, "test setup: expected overflow at this width");

            view.HorizontalOffset = view.MaxHorizontalOffsetValue;

            // Byte 63's cell, scrolled all the way to MaxHorizontalOffset, should land at (or very
            // near) the right edge of the viewport rather than off past the width used at offset 0.
            double screenX = view.HexByteXForTests(63) - view.HorizontalOffset + 1;
            var hit = view.HitTestForTests(new Point(screenX, view.LineHeightForTests / 2));

            Assert.NotNull(hit);
            Assert.Equal(63, hit!.Value.Position.ByteOffset);
            Assert.False(hit.Value.InAsciiPane);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void ShrinkingBytesPerRowClampsHorizontalOffsetToTheNewSmallerMax()
    {
        var document = OpenTempBinaryDocument(SequentialBytes(64), out string path);
        try
        {
            var view = CreateView(document, bytesPerRow: 64, width: 300);
            view.HorizontalOffset = view.MaxHorizontalOffsetValue;
            double maxAt64BytesPerRow = view.MaxHorizontalOffsetValue;
            Assert.True(maxAt64BytesPerRow > 0, "test setup: expected scroll room at 64 bytes per row");

            // Narrowing the row width shrinks the content overflow, so the old HorizontalOffset
            // (valid for the 64-byte layout) can now overshoot the new, smaller max.
            view.BytesPerRow = 16;

            Assert.True(view.MaxHorizontalOffsetValue < maxAt64BytesPerRow, "expected a narrower row width to reduce the overflow");
            Assert.Equal(view.MaxHorizontalOffsetValue, view.HorizontalOffset);
        }
        finally
        {
            document.Dispose();
            File.Delete(path);
        }
    }
}
