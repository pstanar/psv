using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Psv.Core;

namespace Psv.App;

/// <summary>
/// A byte offset within the document. Unlike <see cref="DocumentPosition"/>'s gap-based column (a
/// cursor position between characters), a <see cref="BytePosition"/> names an actual byte cell -
/// the natural unit to drag-select over a fixed-width hex grid. Row/column-in-row are derived
/// (<c>ByteOffset / view.BytesPerRow</c>, <c>% view.BytesPerRow</c>) rather than stored, since each
/// row is exactly <see cref="HexView.BytesPerRow"/> bytes wide and needs no line-boundary scan to
/// locate.
/// </summary>
internal readonly record struct BytePosition(long ByteOffset) : IComparable<BytePosition>
{
    public int CompareTo(BytePosition other) => ByteOffset.CompareTo(other.ByteOffset);
}

/// <summary>One visually rendered row from the last <see cref="HexView.Render"/> pass, cached for hit-testing exactly like <see cref="RenderedRow"/>.</summary>
internal readonly record struct RenderedHexRow(double Y, long RowStartOffset, int ByteCount);

/// <summary>Pixel x-positions for one frame's column layout - computed once per <see cref="HexView.Render"/>/hit-test so drawing and hit-testing can never drift apart.</summary>
internal readonly record struct HexLayout(double GutterWidth, double HexPaneX, double SeparatorX, double AsciiPaneX);

/// <summary>
/// Virtualized binary/hex viewport: offset gutter, a <see cref="BytesPerRow"/>-bytes-per-row hex
/// pane, and a mirrored ASCII pane, reading bytes directly via <see cref="PsvDocument.ByteSource"/>.
/// Row width is uniform across the whole document (just user-configurable between 16/32/64), so -
/// unlike <see cref="DocumentView"/> - locating any row is pure arithmetic (<c>offset =
/// row * BytesPerRow</c>), with no line-boundary scan and no horizontal scroll/word-wrap concept.
/// Mirrors <see cref="DocumentView"/>'s selection (stream + Alt-drag rectangular), auto-scroll, and
/// omniscroll patterns; see that class for the rationale behind each.
/// </summary>
public sealed class HexView : Control
{
    public const int DefaultBytesPerRow = 32;

    public static readonly StyledProperty<PsvDocument?> DocumentProperty =
        AvaloniaProperty.Register<HexView, PsvDocument?>(nameof(Document));

    public static readonly StyledProperty<long> TopLineProperty =
        AvaloniaProperty.Register<HexView, long>(nameof(TopLine));

    public static readonly StyledProperty<int> BytesPerRowProperty =
        AvaloniaProperty.Register<HexView, int>(nameof(BytesPerRow), defaultValue: DefaultBytesPerRow);

    public static readonly StyledProperty<double> HorizontalOffsetProperty =
        AvaloniaProperty.Register<HexView, double>(nameof(HorizontalOffset));

    public static readonly StyledProperty<bool> ZebraStripingProperty =
        AvaloniaProperty.Register<HexView, bool>(nameof(ZebraStriping), defaultValue: true);

    public static readonly StyledProperty<bool> FollowSystemThemeProperty =
        AvaloniaProperty.Register<HexView, bool>(nameof(FollowSystemTheme), defaultValue: true);

    public static readonly StyledProperty<Color> ZebraEvenColorProperty =
        AvaloniaProperty.Register<HexView, Color>(nameof(ZebraEvenColor), defaultValue: Colors.White);

    public static readonly StyledProperty<Color> ZebraOddColorProperty =
        AvaloniaProperty.Register<HexView, Color>(nameof(ZebraOddColor), defaultValue: Color.FromRgb(0xF0, 0xF0, 0xF0));

    public static readonly StyledProperty<Color> TextColorProperty =
        AvaloniaProperty.Register<HexView, Color>(nameof(TextColor), defaultValue: Colors.Black);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<HexView, FontFamily>(nameof(FontFamily), defaultValue: new FontFamily("monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<HexView, double>(nameof(FontSize), defaultValue: 14);

    private const double LinePadding = 2;
    private const double GutterPadding = 6;
    private const int WheelScrollLines = 3;

    // Mid-row gaps: a narrow gap at every 8-byte boundary, and a slightly wider one at every
    // 16-byte ("word") boundary nested inside it, so a 32/64-byte row still reads as a stack of
    // the traditional 16-byte rows rather than one undifferentiated strip of hex pairs.
    private const int GroupBoundary = 8;
    private const int WordBoundary = 16;
    private const double GroupGapChars = 1.0;
    private const double WordGapChars = 2.0;
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(80);

    // Middle-click "omniscroll" - see DocumentView's field of the same name. Vertical-only here:
    // there's no horizontal offset concept when every row is exactly BytesPerRow bytes wide.
    private static readonly TimeSpan OmniScrollInterval = TimeSpan.FromMilliseconds(50);
    private const double OmniScrollDeadZone = 10;
    private const double OmniScrollPixelsPerUnit = 6;
    private const long OmniScrollMaxUnitsPerTick = 60;

    private static readonly Color SelectionColor = Color.FromArgb(100, 51, 153, 255);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(SelectionColor);

    private Typeface _typeface;
    private IBrush _textBrush = Brushes.Black;
    private IBrush _evenRowBrush = Brushes.White;
    private IBrush _oddRowBrush = Brushes.White;
    private IBrush _gutterBackgroundBrush = Brushes.White;
    private IBrush _gutterTextBrush = Brushes.Gray;
    private IPen _separatorPen = new Pen(Brushes.Gray);

    // Reused scratch buffers for building one row's hex/ASCII text - see DrawHexLine/DrawAsciiLine
    // for why a whole row is shaped as a single string instead of one FormattedText per byte.
    private readonly StringBuilder _hexLineBuilder = new();
    private readonly StringBuilder _asciiLineBuilder = new();

    // Populated fresh every Render() call - see RenderedHexRow for why.
    private readonly List<RenderedHexRow> _renderedRows = [];

    // Mouse-drag byte selection - see DocumentView's equivalent fields for the general shape.
    // _selectionStartedInAsciiPane has no DocumentView analog: it doesn't affect what's
    // highlighted (both panes always mirror the same selected bytes - see GetSelectionRangeForRow),
    // only which clipboard format Ctrl+C/right-click produces.
    private BytePosition? _selectionAnchor;
    private BytePosition? _selectionFocus;
    private bool _selectionIsRectangular;
    private bool _selectionStartedInAsciiPane;
    private bool _isSelecting;

    private DispatcherTimer? _autoScrollTimer;
    private int _autoScrollDirection;

    private DispatcherTimer? _omniScrollTimer;
    private Point? _omniScrollAnchor;
    private Point _omniScrollPointer;
    private IPointer? _omniScrollCapturedPointer;

    private double _lineHeight;
    private double _charWidth;

    static HexView()
    {
        AffectsRender<HexView>(
            DocumentProperty, TopLineProperty, BytesPerRowProperty, HorizontalOffsetProperty, ZebraStripingProperty,
            FollowSystemThemeProperty, ZebraEvenColorProperty, ZebraOddColorProperty, TextColorProperty, FontFamilyProperty, FontSizeProperty);
    }

    public HexView()
    {
        Focusable = true;
        ClipToBounds = true;
        RefreshRenderResources();
        RecomputeMetrics();
    }

    public PsvDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public long TopLine
    {
        get => GetValue(TopLineProperty);
        set => SetValue(TopLineProperty, Math.Clamp(value, 0, MaxTopLine()));
    }

    public int BytesPerRow
    {
        get => GetValue(BytesPerRowProperty);
        set => SetValue(BytesPerRowProperty, value);
    }

    /// <summary>
    /// Pixel scroll offset applied to the hex and ASCII panes only - the offset gutter stays
    /// pinned in place, matching how <see cref="DocumentView"/>'s line-number gutter never scrolls
    /// horizontally either. Only reachable when <see cref="BytesPerRow"/> is wide enough that the
    /// hex+ASCII content overflows the viewport (32/64-byte rows on a normal-width window); at the
    /// original fixed 16-byte row width this never came up.
    /// </summary>
    public double HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, Math.Clamp(value, 0, MaxHorizontalOffset()));
    }

    /// <summary>Read-only view of <see cref="MaxHorizontalOffset"/> for MainWindow to size the shared horizontal scrollbar against, without duplicating this view's layout/font-metric math.</summary>
    internal double MaxHorizontalOffsetValue => MaxHorizontalOffset();

    public bool ZebraStriping
    {
        get => GetValue(ZebraStripingProperty);
        set => SetValue(ZebraStripingProperty, value);
    }

    public bool FollowSystemTheme
    {
        get => GetValue(FollowSystemThemeProperty);
        set => SetValue(FollowSystemThemeProperty, value);
    }

    public Color ZebraEvenColor
    {
        get => GetValue(ZebraEvenColorProperty);
        set => SetValue(ZebraEvenColorProperty, value);
    }

    public Color ZebraOddColor
    {
        get => GetValue(ZebraOddColorProperty);
        set => SetValue(ZebraOddColorProperty, value);
    }

    public Color TextColor
    {
        get => GetValue(TextColorProperty);
        set => SetValue(TextColorProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public int VisibleRowCount => Math.Max(1, (int)Math.Ceiling(Bounds.Height / _lineHeight));

    /// <summary>Rounds down, unlike VisibleRowCount - see DocumentView.FullyVisibleLineCount for why.</summary>
    internal int FullyVisibleRowCount => Math.Max(1, (int)Math.Floor(Bounds.Height / _lineHeight));

    internal double LineHeightForTests => _lineHeight;

    internal double CharWidthForTests => _charWidth;

    internal IReadOnlyList<RenderedHexRow> RenderedRowsForTests => _renderedRows;

    internal HexLayout LayoutForTests => ComputeLayout();

    internal double HexByteXForTests(int byteIndex) => HexByteX(byteIndex, ComputeLayout().HexPaneX);

    internal long MaxTopLineForTests => MaxTopLine();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            RefreshRenderResources();
            RecomputeMetrics();

            // Character width feeds MaxHorizontalOffset (see ComputeLayout) - re-clamp rather than
            // leaving a scroll position that overshoots the content's new width.
            HorizontalOffset = HorizontalOffset;
        }
        else if (change.Property == TextColorProperty
            || change.Property == ZebraEvenColorProperty
            || change.Property == ZebraOddColorProperty
            || change.Property == FollowSystemThemeProperty)
        {
            RefreshRenderResources();
        }
        else if (change.Property == DocumentProperty)
        {
            _selectionAnchor = null;
            _selectionFocus = null;
            HorizontalOffset = 0;
        }
        else if (change.Property == BytesPerRowProperty)
        {
            // Row count (and therefore MaxTopLine) shifts whenever row width changes - re-clamp
            // rather than leaving TopLine pointing past the new end of the document. The stored
            // selection, unlike TopLine, needs no adjustment: BytePosition is a plain byte offset,
            // not derived from row width (see BytePosition's doc comment). Content width (and so
            // MaxHorizontalOffset) shifts too - re-clamp that as well.
            TopLine = TopLine;
            HorizontalOffset = HorizontalOffset;
        }
        else if (change.Property == BoundsProperty && Document is not null)
        {
            var oldBounds = change.GetOldValue<Rect>();
            var newBounds = change.GetNewValue<Rect>();
            if (oldBounds.Height != newBounds.Height)
            {
                long oldFullyVisible = Math.Max(1, (long)Math.Floor(oldBounds.Height / _lineHeight));
                long oldMaxTopLine = Math.Max(0, TotalRowCount() - oldFullyVisible);

                if (TopLine >= oldMaxTopLine)
                {
                    TopLine = MaxTopLine();
                }
            }

            if (oldBounds.Width != newBounds.Width)
            {
                HorizontalOffset = HorizontalOffset;
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Application.Current is { } app)
        {
            app.ActualThemeVariantChanged += OnAppThemeChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Application.Current is { } app)
        {
            app.ActualThemeVariantChanged -= OnAppThemeChanged;
        }

        StopAutoScroll();
        StopOmniScroll();
    }

    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        if (FollowSystemTheme)
        {
            RefreshRenderResources();
            InvalidateVisual();
        }
    }

    public (Color Text, Color ZebraEven, Color ZebraOdd) GetEffectiveColors()
    {
        var palette = GetEffectivePalette();
        return (palette.Text, palette.ZebraEven, palette.ZebraOdd);
    }

    private ViewPalette GetEffectivePalette()
    {
        if (!FollowSystemTheme)
        {
            return new ViewPalette(TextColor, ZebraEvenColor, ZebraOddColor);
        }

        bool isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        return isDark ? ViewPalettes.Dark : ViewPalettes.Light;
    }

    private void RefreshRenderResources()
    {
        _typeface = new Typeface(FontFamily);

        var palette = GetEffectivePalette();
        var (gutterBackground, gutterText) = ViewPalettes.GetGutterColors(palette.ZebraEven);
        _textBrush = new SolidColorBrush(palette.Text);
        _evenRowBrush = new SolidColorBrush(palette.ZebraEven);
        _oddRowBrush = new SolidColorBrush(palette.ZebraOdd);
        _gutterBackgroundBrush = new SolidColorBrush(gutterBackground);
        _gutterTextBrush = new SolidColorBrush(gutterText);
        _separatorPen = new Pen(_gutterTextBrush);
    }

    private void RecomputeMetrics()
    {
        var probe = new FormattedText("X", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.Black);
        _lineHeight = probe.Height + LinePadding;
        _charWidth = probe.Width;
    }

    /// <summary>Always 8 hex digits - unlike DocumentView's line-number gutter, offsets have a fixed width, so there's nothing to size against the document.</summary>
    private double ComputeGutterWidth() => (8 * _charWidth) + (GutterPadding * 2);

    private HexLayout ComputeLayout()
    {
        double gutterWidth = ComputeGutterWidth();
        double hexPaneX = gutterWidth + GutterPadding;

        // BytesPerRow two-digit cells, each followed by a one-char gap, plus the extra grouping
        // gaps computed by GapBeforeByte (see its doc comment).
        int bytesPerRow = BytesPerRow;
        double hexContentWidth = (bytesPerRow * 3 * _charWidth) + (GapBeforeByte(bytesPerRow - 1) * _charWidth);

        double separatorX = hexPaneX + hexContentWidth + GutterPadding;
        double asciiPaneX = separatorX + GutterPadding;
        return new HexLayout(gutterWidth, hexPaneX, separatorX, asciiPaneX);
    }

    /// <summary>
    /// Extra pixel gap accumulated immediately before hex cell <paramref name="byteIndex"/>, on top
    /// of each cell's own 1-char inter-byte spacing: a narrow <see cref="GroupGapChars"/>-wide gap
    /// at every <see cref="GroupBoundary"/>-byte boundary, widened to <see cref="WordGapChars"/> at
    /// every <see cref="WordBoundary"/>-byte boundary nested inside it. Only boundaries at or before
    /// <paramref name="byteIndex"/> count, so cell 0 has no leading gap and the row's own trailing
    /// edge never grows one either. BytesPerRow tops out at 64 (see the "Bytes Per Row" menu), so
    /// this loop is at most 7 iterations - cheap enough to call per-byte rather than caching.
    /// </summary>
    private static double GapBeforeByte(int byteIndex)
    {
        double gapChars = 0;
        for (int boundary = GroupBoundary; boundary <= byteIndex; boundary += GroupBoundary)
        {
            gapChars += GapAtBoundary(boundary);
        }

        return gapChars;
    }

    /// <summary>The extra gap chars (on top of the normal 1-char inter-byte spacing) added at boundary <paramref name="boundary"/> alone, not cumulative - see <see cref="GapBeforeByte"/> for the running total this feeds.</summary>
    private static double GapAtBoundary(int boundary) => boundary % WordBoundary == 0 ? WordGapChars : GroupGapChars;

    /// <summary>The left edge of hex byte cell <paramref name="byteIndex"/> - each cell is 2 hex digits + 1 gap char wide, plus any grouping gaps from <see cref="GapBeforeByte"/>.</summary>
    private double HexByteX(int byteIndex, double hexPaneX) =>
        hexPaneX + (byteIndex * 3 * _charWidth) + (GapBeforeByte(byteIndex) * _charWidth);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(_evenRowBrush, new Rect(Bounds.Size));

        var document = Document;
        if (document is null)
        {
            return;
        }

        bool zebra = ZebraStriping;
        long topRow = TopLine;
        long length = document.ByteSource.Length;
        var layout = ComputeLayout();
        double horizontalOffset = HorizontalOffset;

        context.FillRectangle(_gutterBackgroundBrush, new Rect(0, 0, layout.GutterWidth, Bounds.Height));

        _renderedRows.Clear();
        Span<byte> rowBuffer = stackalloc byte[BytesPerRow];
        int visibleRows = VisibleRowCount;
        double y = 0;

        for (int i = 0; i < visibleRows && y < Bounds.Height; i++)
        {
            long rowNumber = topRow + i;
            long rowStartOffset = rowNumber * BytesPerRow;
            if (rowStartOffset >= length)
            {
                break;
            }

            int byteCount = document.ByteSource.Read(rowStartOffset, rowBuffer);
            var rowBrush = (zebra && rowNumber % 2 != 0) ? _oddRowBrush : _evenRowBrush;
            _renderedRows.Add(new RenderedHexRow(y, rowStartOffset, byteCount));

            DrawRow(context, rowBrush, layout, y, rowStartOffset, rowBuffer[..byteCount], GetSelectionRangeForRow(rowStartOffset, byteCount), horizontalOffset);

            y += _lineHeight;
        }

        // The separator (and every hex/ASCII glyph drawn in DrawRow) scrolls with horizontalOffset;
        // the gutter fill and DrawOffset above do not - clipping to the gutter's right edge stops
        // scrolled-left content from ever painting over the still-pinned offset column.
        using (context.PushClip(new Rect(layout.GutterWidth, 0, Math.Max(0, Bounds.Width - layout.GutterWidth), Bounds.Height)))
        {
            double separatorX = layout.SeparatorX - horizontalOffset;
            context.DrawLine(_separatorPen, new Point(separatorX, 0), new Point(separatorX, Bounds.Height));
        }
    }

    private void DrawRow(
        DrawingContext context, IBrush rowBrush, HexLayout layout, double y, long rowStartOffset,
        ReadOnlySpan<byte> bytes, (int Start, int End)? selection, double horizontalOffset)
    {
        context.FillRectangle(rowBrush, new Rect(layout.GutterWidth, y, Bounds.Width - layout.GutterWidth, _lineHeight));
        DrawOffset(context, rowStartOffset, layout.GutterWidth, y);

        using (context.PushClip(new Rect(layout.GutterWidth, y, Math.Max(0, Bounds.Width - layout.GutterWidth), _lineHeight)))
        {
            if (selection is { } range)
            {
                DrawByteRangeHighlight(context, range.Start, range.End, layout, y, horizontalOffset);
            }

            DrawHexLine(context, bytes, layout.HexPaneX - horizontalOffset, y);
            DrawAsciiLine(context, bytes, layout.AsciiPaneX - horizontalOffset, y);
        }
    }

    private void DrawOffset(DrawingContext context, long offset, double gutterWidth, double y)
    {
        var formatted = new FormattedText(offset.ToString("X8", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _gutterTextBrush);
        context.DrawText(formatted, new Point(gutterWidth - GutterPadding - formatted.Width, y));
    }

    /// <summary>
    /// Draws every hex byte in the row as a single shaped string instead of one FormattedText/
    /// DrawText call per byte - at BytesPerRow=64 across ~70 visible rows, per-byte drawing meant
    /// thousands of DrawText calls per frame, each with its own fixed shaping/draw overhead; this
    /// cuts that to one call per row regardless of row width. The embedded spaces reproduce
    /// HexByteX's per-cell gap exactly (1 normal, 2 at an 8-byte boundary, 3 at a 16-byte one) - see
    /// GapBeforeByte - so the monospace-rendered text lands on the same pixel grid hit-testing and
    /// selection highlighting already assume.
    /// </summary>
    private void DrawHexLine(DrawingContext context, ReadOnlySpan<byte> bytes, double x, double y)
    {
        _hexLineBuilder.Clear();
        for (int i = 0; i < bytes.Length; i++)
        {
            _hexLineBuilder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));

            if (i + 1 < bytes.Length)
            {
                int boundary = i + 1;
                int gapChars = 1 + (boundary % GroupBoundary == 0 ? (int)GapAtBoundary(boundary) : 0);
                _hexLineBuilder.Append(' ', gapChars);
            }
        }

        var formatted = new FormattedText(_hexLineBuilder.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _textBrush);
        context.DrawText(formatted, new Point(x, y));
    }

    /// <summary>Same one-string-per-row batching as <see cref="DrawHexLine"/>, but the ASCII pane has no grouping gaps to reproduce - see ToDisplayChar.</summary>
    private void DrawAsciiLine(DrawingContext context, ReadOnlySpan<byte> bytes, double x, double y)
    {
        _asciiLineBuilder.Clear();
        for (int i = 0; i < bytes.Length; i++)
        {
            _asciiLineBuilder.Append(ToDisplayChar(bytes[i]));
        }

        var formatted = new FormattedText(_asciiLineBuilder.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _textBrush);
        context.DrawText(formatted, new Point(x, y));
    }

    /// <summary>Highlights the selected [start,end) byte range identically in both the hex and ASCII panes - the two panes always mirror the same selected bytes regardless of which pane the drag started in (see _selectionStartedInAsciiPane).</summary>
    private void DrawByteRangeHighlight(DrawingContext context, int start, int end, HexLayout layout, double y, double horizontalOffset)
    {
        if (start >= end)
        {
            return;
        }

        double hexStartX = HexByteX(start, layout.HexPaneX) - horizontalOffset;
        double hexEndX = HexByteX(end - 1, layout.HexPaneX) + (2 * _charWidth) - horizontalOffset;
        context.FillRectangle(SelectionBrush, new Rect(hexStartX, y, hexEndX - hexStartX, _lineHeight));

        double asciiStartX = layout.AsciiPaneX + (start * _charWidth) - horizontalOffset;
        context.FillRectangle(SelectionBrush, new Rect(asciiStartX, y, (end - start) * _charWidth, _lineHeight));
    }

    /// <summary>
    /// Byte -&gt; display glyph for the ASCII pane, indexed by raw byte value. A byte is "printable"
    /// if decoding it as Windows-1252 (the same single-byte fallback encoding text mode already
    /// uses for non-UTF-8 content - see EncodingDetector) yields something other than a control
    /// character. Earlier this only allowed core ASCII (0x20-0x7E), which meant every byte with
    /// the high bit set rendered as '.' even when it's a perfectly ordinary printable character in
    /// Windows-1252/Latin-1 (e.g. 0xE9 is 'é') - this table renders those as their real glyph
    /// instead, matching what a reference hex editor (HxD et al.) shows. Built once: this runs for
    /// every visible byte on every frame, so a per-byte Encoding.GetString call isn't affordable.
    /// </summary>
    private static readonly char[] DisplayCharByByte = BuildDisplayCharTable();

    private static char[] BuildDisplayCharTable()
    {
        var encoding = TextEncodingCatalog.Resolve(TextEncodingKind.Windows1252);
        var table = new char[256];
        byte[] single = new byte[1];
        for (int i = 0; i < table.Length; i++)
        {
            single[0] = (byte)i;
            char decoded = encoding.GetString(single)[0];
            table[i] = char.IsControl(decoded) ? '.' : decoded;
        }

        return table;
    }

    private static char ToDisplayChar(byte value) => DisplayCharByByte[value];

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        StopOmniScroll();
        ScrollBy((long)(-e.Delta.Y * WheelScrollLines));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_omniScrollAnchor is not null)
        {
            StopOmniScroll();
            e.Handled = true;
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            StartOmniScroll(e.GetPosition(this), e.Pointer);
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed)
        {
            _ = CopySelectionToClipboardAsync();
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (HitTest(position) is not { } hit)
        {
            return;
        }

        _selectionAnchor = hit.Position;
        _selectionFocus = hit.Position;
        _selectionIsRectangular = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _selectionStartedInAsciiPane = hit.InAsciiPane;
        _isSelecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        if (_omniScrollAnchor is not null)
        {
            _omniScrollPointer = position;
            return;
        }

        if (!_isSelecting)
        {
            return;
        }

        UpdateAutoScrollForPointerY(position.Y);

        if (HitTest(position) is { } hit)
        {
            _selectionFocus = hit.Position;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isSelecting)
        {
            _isSelecting = false;
            StopAutoScroll();
            e.Pointer.Capture(null);

            // A rectangular drag is Alt-modified (see OnPointerPressed): releasing that Alt key at
            // the end of the drag can be seen as a "bare Alt tap" by Avalonia's own access-key/menu
            // -mnemonic handling, which silently moves keyboard focus to the menu bar even though
            // the mouse never left this control - Ctrl+C (and every other shortcut) then goes
            // nowhere until focus is manually restored (confirmed: right-click, which also calls
            // Focus() in OnPointerPressed, fixes it; a plain left-click "fixes" it too but starts a
            // new selection in the process). Re-asserting focus here costs nothing when nothing
            // stole it, and silently repairs the case when something did.
            Focus();
        }
    }

    /// <summary>
    /// Maps a pointer position to a byte position using the last rendered frame's row layout (see
    /// <see cref="RenderedHexRow"/>), clamping to the nearest visible row/byte rather than
    /// returning null for a drag that strays outside the viewport - same rationale as
    /// DocumentView.HitTest. Also reports which pane (hex or ASCII) the position falls in, using
    /// the same <see cref="HexLayout.SeparatorX"/> boundary <see cref="Render"/> draws the dividing
    /// line at, so the two agree exactly on where one pane ends and the other begins.
    /// </summary>
    private (BytePosition Position, bool InAsciiPane)? HitTest(Point position)
    {
        if (_renderedRows.Count == 0)
        {
            return null;
        }

        var row = _renderedRows[0];
        foreach (var candidate in _renderedRows)
        {
            row = candidate;
            if (position.Y < candidate.Y + _lineHeight)
            {
                break;
            }
        }

        var layout = ComputeLayout();

        // Scrolled content coordinates - Render draws every hex/ASCII glyph (and the separator)
        // shifted left by HorizontalOffset, so a pointer position must be shifted back the same
        // amount before comparing against the unshifted layout's pane boundaries/cell positions.
        double contentX = position.X + HorizontalOffset;
        bool inAsciiPane = contentX >= layout.SeparatorX;
        int byteIndex = inAsciiPane
            ? AsciiByteIndexFromX(contentX, layout.AsciiPaneX)
            : HexByteIndexFromX(contentX, layout.HexPaneX);

        byteIndex = Math.Clamp(byteIndex, 0, Math.Max(0, row.ByteCount - 1));
        return (new BytePosition(row.RowStartOffset + byteIndex), inAsciiPane);
    }

    private int AsciiByteIndexFromX(double x, double asciiPaneX) =>
        Math.Clamp((int)((x - asciiPaneX) / _charWidth), 0, BytesPerRow - 1);

    /// <summary>
    /// Inverts <see cref="HexByteX"/>: the highest byte index whose cell start is at or before
    /// <paramref name="x"/>. Walked from the last cell down rather than solved algebraically,
    /// since GapBeforeByte's running total isn't invertible in closed form once it mixes two gap
    /// sizes - cheap regardless, at most 64 iterations per pointer event.
    /// </summary>
    private int HexByteIndexFromX(double x, double hexPaneX)
    {
        double relative = Math.Max(0, x - hexPaneX);
        int bytesPerRow = BytesPerRow;

        for (int i = bytesPerRow - 1; i > 0; i--)
        {
            double cellStart = (i * 3 * _charWidth) + (GapBeforeByte(i) * _charWidth);
            if (relative >= cellStart)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The [start,end) byte-in-row range on <paramref name="rowStartOffset"/> currently selected,
    /// or null if none of that row is selected. Unlike DocumentView's gap-based columns, a
    /// BytePosition names an actual byte, so both endpoints are inclusive of the anchor/focus byte
    /// itself (the +1 below converts to the exclusive-end range this method returns).
    /// </summary>
    private (int Start, int End)? GetSelectionRangeForRow(long rowStartOffset, int byteCount)
    {
        if (_selectionAnchor is not { } anchor || _selectionFocus is not { } focus || anchor.Equals(focus))
        {
            return null;
        }

        long rowNumber = rowStartOffset / BytesPerRow;

        if (_selectionIsRectangular)
        {
            long anchorRow = anchor.ByteOffset / BytesPerRow;
            long focusRow = focus.ByteOffset / BytesPerRow;
            long minRow = Math.Min(anchorRow, focusRow);
            long maxRow = Math.Max(anchorRow, focusRow);
            if (rowNumber < minRow || rowNumber > maxRow)
            {
                return null;
            }

            int anchorCol = (int)(anchor.ByteOffset % BytesPerRow);
            int focusCol = (int)(focus.ByteOffset % BytesPerRow);
            int minCol = Math.Min(anchorCol, focusCol);
            int maxCol = Math.Max(anchorCol, focusCol) + 1;
            int rectStart = Math.Max(0, minCol);
            int rectEnd = Math.Min(byteCount, maxCol);
            return rectStart < rectEnd ? (rectStart, rectEnd) : null;
        }

        var from = anchor.CompareTo(focus) <= 0 ? anchor : focus;
        var to = anchor.CompareTo(focus) <= 0 ? focus : anchor;

        long fromRow = from.ByteOffset / BytesPerRow;
        long toRow = to.ByteOffset / BytesPerRow;
        if (rowNumber < fromRow || rowNumber > toRow)
        {
            return null;
        }

        int rowStart = rowNumber == fromRow ? (int)(from.ByteOffset % BytesPerRow) : 0;
        int rowEnd = rowNumber == toRow ? (int)(to.ByteOffset % BytesPerRow) + 1 : BytesPerRow;
        int start = Math.Max(0, rowStart);
        int end = Math.Min(byteCount, rowEnd);
        return start < end ? (start, end) : null;
    }

    /// <summary>The currently selected bytes (stream or rectangular), or null if there's no selection.</summary>
    private byte[]? GetSelectedByteRange()
    {
        var document = Document;
        if (document is null || _selectionAnchor is not { } anchor || _selectionFocus is not { } focus || anchor.Equals(focus))
        {
            return null;
        }

        if (_selectionIsRectangular)
        {
            long anchorRow = anchor.ByteOffset / BytesPerRow;
            long focusRow = focus.ByteOffset / BytesPerRow;
            long minRow = Math.Min(anchorRow, focusRow);
            long maxRow = Math.Max(anchorRow, focusRow);
            int anchorCol = (int)(anchor.ByteOffset % BytesPerRow);
            int focusCol = (int)(focus.ByteOffset % BytesPerRow);
            int minCol = Math.Min(anchorCol, focusCol);
            int maxCol = Math.Max(anchorCol, focusCol);

            var result = new List<byte>();
            Span<byte> rowBuffer = stackalloc byte[BytesPerRow];
            for (long row = minRow; row <= maxRow; row++)
            {
                int read = document.ByteSource.Read(row * BytesPerRow, rowBuffer);
                for (int col = minCol; col <= maxCol && col < read; col++)
                {
                    result.Add(rowBuffer[col]);
                }
            }

            return [.. result];
        }

        var from = anchor.CompareTo(focus) <= 0 ? anchor : focus;
        var to = anchor.CompareTo(focus) <= 0 ? focus : anchor;
        long length = to.ByteOffset - from.ByteOffset + 1;
        byte[] buffer = new byte[length];
        int totalRead = document.ByteSource.Read(from.ByteOffset, buffer);
        return totalRead == length ? buffer : buffer[..totalRead];
    }

    /// <summary>
    /// Row-major bytes for the current rectangular selection, one entry per selected row, each
    /// exactly (maxCol - minCol + 1) wide - or null if there's no rectangular selection. A null
    /// slot stands in for the blank hex cell / blank ASCII space that row would render past its
    /// actual byte count (a short trailing row), never a real 0x00 byte, so the copy's padding
    /// can't be mistaken for data pasted elsewhere.
    /// </summary>
    private List<byte?[]>? GetSelectedRectangularRows()
    {
        var document = Document;
        if (document is null || !_selectionIsRectangular
            || _selectionAnchor is not { } anchor || _selectionFocus is not { } focus || anchor.Equals(focus))
        {
            return null;
        }

        long minRow = Math.Min(anchor.ByteOffset, focus.ByteOffset) / BytesPerRow;
        long maxRow = Math.Max(anchor.ByteOffset, focus.ByteOffset) / BytesPerRow;
        int anchorCol = (int)(anchor.ByteOffset % BytesPerRow);
        int focusCol = (int)(focus.ByteOffset % BytesPerRow);
        int minCol = Math.Min(anchorCol, focusCol);
        int maxCol = Math.Max(anchorCol, focusCol);
        int width = maxCol - minCol + 1;

        var result = new List<byte?[]>();
        Span<byte> rowBuffer = stackalloc byte[BytesPerRow];
        for (long row = minRow; row <= maxRow; row++)
        {
            int read = document.ByteSource.Read(row * BytesPerRow, rowBuffer);
            var rowBytes = new byte?[width];
            for (int col = minCol; col <= maxCol; col++)
            {
                rowBytes[col - minCol] = col < read ? rowBuffer[col] : null;
            }

            result.Add(rowBytes);
        }

        return result;
    }

    /// <summary>Selected bytes formatted as an uppercase space-separated hex string, e.g. "4D 5A 90 00".</summary>
    private static string FormatAsHexText(byte[] bytes) =>
        string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Selected bytes formatted exactly as the ASCII pane renders them, dots and all - a literal copy of what's on screen rather than a re-decode.</summary>
    private static string FormatAsAsciiText(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            builder.Append(ToDisplayChar(b));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rectangular selection as one hex-pair row per line, blank cells rendered as two spaces so
    /// every row is the same width - pasting elsewhere reproduces the same rectangle rather than
    /// collapsing the right edge of any row shorter than the selection (see GetSelectedRectangularRows).
    /// </summary>
    private static string FormatRectangularAsHexText(List<byte?[]> rows) =>
        string.Join(
            Environment.NewLine,
            rows.Select(row => string.Join(" ", row.Select(b => b is { } value ? value.ToString("X2", CultureInfo.InvariantCulture) : "  "))));

    /// <summary>Rectangular selection as one ASCII row per line, blank cells rendered as a space - see FormatRectangularAsHexText for why the shape must be preserved.</summary>
    private static string FormatRectangularAsAsciiText(List<byte?[]> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => new string(row.Select(b => b is { } value ? ToDisplayChar(value) : ' ').ToArray())));

    private string? BuildHexCopyText() =>
        _selectionIsRectangular
            ? GetSelectedRectangularRows() is { } rows ? FormatRectangularAsHexText(rows) : null
            : GetSelectedByteRange() is { Length: > 0 } bytes ? FormatAsHexText(bytes) : null;

    private string? BuildAsciiCopyText() =>
        _selectionIsRectangular
            ? GetSelectedRectangularRows() is { } rows ? FormatRectangularAsAsciiText(rows) : null
            : GetSelectedByteRange() is { Length: > 0 } bytes ? FormatAsAsciiText(bytes) : null;

    /// <summary>Copies the current selection - as a hex byte string if the drag started in the hex pane, or as decoded characters if it started in the ASCII pane (see _selectionStartedInAsciiPane).</summary>
    internal async Task CopySelectionToClipboardAsync()
    {
        string? text = _selectionStartedInAsciiPane ? BuildAsciiCopyText() : BuildHexCopyText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    internal (BytePosition Position, bool InAsciiPane)? HitTestForTests(Point position) => HitTest(position);

    internal void BeginSelectionForTests(Point position, bool rectangular)
    {
        if (HitTest(position) is not { } hit)
        {
            return;
        }

        _selectionAnchor = hit.Position;
        _selectionFocus = hit.Position;
        _selectionIsRectangular = rectangular;
        _selectionStartedInAsciiPane = hit.InAsciiPane;
        InvalidateVisual();
    }

    internal void UpdateSelectionForTests(Point position)
    {
        if (HitTest(position) is { } hit)
        {
            _selectionFocus = hit.Position;
            InvalidateVisual();
        }
    }

    internal void SetSelectionForTests(BytePosition anchor, BytePosition focus, bool rectangular, bool startedInAsciiPane)
    {
        _selectionAnchor = anchor;
        _selectionFocus = focus;
        _selectionIsRectangular = rectangular;
        _selectionStartedInAsciiPane = startedInAsciiPane;
        InvalidateVisual();
    }

    internal BytePosition? SelectionFocusForTests => _selectionFocus;

    internal byte[]? SelectedBytesForTests => GetSelectedByteRange();

    /// <summary>The hex-format copy text regardless of which pane the (simulated) drag started in - lets tests assert both clipboard formats independently.</summary>
    internal string? HexCopyTextForTests => BuildHexCopyText();

    /// <summary>The ASCII-format copy text regardless of which pane the (simulated) drag started in - lets tests assert both clipboard formats independently.</summary>
    internal string? AsciiCopyTextForTests => BuildAsciiCopyText();

    internal void SimulateCtrlCForTests() => OnKeyDown(new KeyEventArgs { Key = Key.C, KeyModifiers = KeyModifiers.Control });

    internal void SimulateEscapeForTests() => OnKeyDown(new KeyEventArgs { Key = Key.Escape });

    internal void UpdateAutoScrollForTests(double y) => UpdateAutoScrollForPointerY(y);

    internal int AutoScrollDirectionForTests => _autoScrollDirection;

    internal void TriggerAutoScrollTickForTests() => OnAutoScrollTick();

    internal void SetIsSelectingForTests(bool isSelecting) => _isSelecting = isSelecting;

    internal void StartOmniScrollForTests(Point anchor) => StartOmniScroll(anchor, pointer: null);

    internal void MoveOmniScrollPointerForTests(Point position) => _omniScrollPointer = position;

    internal void StopOmniScrollForTests() => StopOmniScroll();

    internal bool IsOmniScrollingForTests => _omniScrollAnchor is not null;

    internal void TriggerOmniScrollTickForTests() => OnOmniScrollTick();

    internal void SimulateAltKeyUpForTests() => OnKeyUp(new KeyEventArgs { Key = Key.LeftAlt });

    /// <summary>
    /// Releasing the Alt key held for a rectangular drag (see OnPointerPressed) can be read by
    /// Avalonia's own access-key/menu-mnemonic handling as a "bare Alt tap", which silently moves
    /// keyboard focus to the menu bar - during the tunnel phase of this very key-up event, before
    /// this bubble-phase override even runs. The OnPointerReleased fix alone isn't enough: it
    /// reclaims focus at mouse-up, which happens chronologically before the user actually lets go
    /// of Alt, so the later Alt key-up still steals it right back. Reclaiming focus again here
    /// undoes that within the same event dispatch - this control was still the event's source
    /// when Alt was released (it had focus at that moment), so it still receives the bubble phase
    /// regardless of where the tunnel phase sent focus in the meantime.
    /// </summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            Focus();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && _omniScrollAnchor is not null)
        {
            StopOmniScroll();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                ScrollBy(-1);
                break;
            case Key.Down:
                ScrollBy(1);
                break;
            case Key.PageUp:
                ScrollBy(-VisibleRowCount);
                break;
            case Key.PageDown:
                ScrollBy(VisibleRowCount);
                break;
            case Key.Home:
                TopLine = 0;
                break;
            case Key.End:
                TopLine = MaxTopLine();
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = CopySelectionToClipboardAsync();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ScrollBy(long deltaRows) => TopLine += deltaRows;

    private void UpdateAutoScrollForPointerY(double y)
    {
        if (y < 0)
        {
            StartAutoScroll(-1);
        }
        else if (y >= Bounds.Height)
        {
            StartAutoScroll(1);
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void StartAutoScroll(int direction)
    {
        if (_autoScrollDirection == direction)
        {
            return;
        }

        _autoScrollDirection = direction;
        _autoScrollTimer ??= new DispatcherTimer(AutoScrollInterval, DispatcherPriority.Normal, (_, _) => OnAutoScrollTick());
        _autoScrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        _autoScrollDirection = 0;
        _autoScrollTimer?.Stop();
    }

    private void OnAutoScrollTick()
    {
        if (!_isSelecting || _autoScrollDirection == 0 || _selectionFocus is not { } focus)
        {
            StopAutoScroll();
            return;
        }

        ScrollBy(_autoScrollDirection);

        long edgeRow = _autoScrollDirection < 0 ? TopLine : TopLine + VisibleRowCount - 1;
        long edgeCol = focus.ByteOffset % BytesPerRow;
        _selectionFocus = new BytePosition((edgeRow * BytesPerRow) + edgeCol);
        InvalidateVisual();
    }

    private void StartOmniScroll(Point anchor, IPointer? pointer)
    {
        _omniScrollAnchor = anchor;
        _omniScrollPointer = anchor;
        _omniScrollCapturedPointer = pointer;
        pointer?.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        _omniScrollTimer ??= new DispatcherTimer(OmniScrollInterval, DispatcherPriority.Normal, (_, _) => OnOmniScrollTick());
        _omniScrollTimer.Start();
    }

    private void StopOmniScroll()
    {
        if (_omniScrollAnchor is null)
        {
            return;
        }

        _omniScrollAnchor = null;
        _omniScrollTimer?.Stop();
        _omniScrollCapturedPointer?.Capture(null);
        _omniScrollCapturedPointer = null;
        Cursor = null;
    }

    private void OnOmniScrollTick()
    {
        if (_omniScrollAnchor is not { } anchor)
        {
            StopOmniScroll();
            return;
        }

        long rowDelta = ComputeOmniScrollUnits(_omniScrollPointer.Y - anchor.Y);
        if (rowDelta != 0)
        {
            ScrollBy(rowDelta);
        }
    }

    private static long ComputeOmniScrollUnits(double distanceFromAnchor)
    {
        double magnitude = Math.Abs(distanceFromAnchor) - OmniScrollDeadZone;
        if (magnitude <= 0)
        {
            return 0;
        }

        long units = Math.Min((long)Math.Ceiling(magnitude / OmniScrollPixelsPerUnit), OmniScrollMaxUnitsPerTick);
        return distanceFromAnchor < 0 ? -units : units;
    }

    private long TotalRowCount()
    {
        var document = Document;
        if (document is null)
        {
            return 0;
        }

        return (document.ByteSource.Length + BytesPerRow - 1) / BytesPerRow;
    }

    private long MaxTopLine() => Math.Max(0, TotalRowCount() - FullyVisibleRowCount);

    /// <summary>
    /// How far the hex+ASCII content (everything right of the offset gutter) overflows the current
    /// viewport width - zero at the original fixed 16-byte row width on any reasonably-sized window,
    /// but real once a wider <see cref="BytesPerRow"/> makes that content wider than the control.
    /// </summary>
    private double MaxHorizontalOffset()
    {
        var layout = ComputeLayout();
        double contentWidth = (layout.AsciiPaneX + (BytesPerRow * _charWidth)) - layout.GutterWidth;
        double viewportWidth = Math.Max(0, Bounds.Width - layout.GutterWidth);
        return Math.Max(0, contentWidth - viewportWidth);
    }
}
