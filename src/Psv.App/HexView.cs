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
/// (<c>ByteOffset / HexView.BytesPerRow</c>, <c>% HexView.BytesPerRow</c>) rather than stored,
/// since the grid is fixed-width and needs no line-boundary scan to locate.
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
/// Virtualized binary/hex viewport: offset gutter, a 16-bytes-per-row hex pane, and a mirrored
/// ASCII pane, reading bytes directly via <see cref="PsvDocument.ByteSource"/>. Row width is fixed,
/// so - unlike <see cref="DocumentView"/> - locating any row is pure arithmetic (<c>offset =
/// row * BytesPerRow</c>), with no line-boundary scan and no horizontal scroll/word-wrap concept.
/// Mirrors <see cref="DocumentView"/>'s selection (stream + Alt-drag rectangular), auto-scroll, and
/// omniscroll patterns; see that class for the rationale behind each.
/// </summary>
public sealed class HexView : Control
{
    public const int BytesPerRow = 16;

    public static readonly StyledProperty<PsvDocument?> DocumentProperty =
        AvaloniaProperty.Register<HexView, PsvDocument?>(nameof(Document));

    public static readonly StyledProperty<long> TopLineProperty =
        AvaloniaProperty.Register<HexView, long>(nameof(TopLine));

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
            DocumentProperty, TopLineProperty, ZebraStripingProperty, FollowSystemThemeProperty,
            ZebraEvenColorProperty, ZebraOddColorProperty, TextColorProperty, FontFamilyProperty, FontSizeProperty);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
        {
            RefreshRenderResources();
            RecomputeMetrics();
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
        }
        else if (change.Property == BoundsProperty && Document is not null)
        {
            var oldBounds = change.GetOldValue<Rect>();
            double newHeight = change.GetNewValue<Rect>().Height;
            if (oldBounds.Height != newHeight)
            {
                long oldFullyVisible = Math.Max(1, (long)Math.Floor(oldBounds.Height / _lineHeight));
                long oldMaxTopLine = Math.Max(0, TotalRowCount() - oldFullyVisible);

                if (TopLine >= oldMaxTopLine)
                {
                    TopLine = MaxTopLine();
                }
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

        // 16 two-digit cells, each followed by a one-char gap, plus one extra char of gap at the
        // row's midpoint (see HexByteX) splitting the hex pane into two 8-byte halves.
        double hexContentWidth = ((BytesPerRow * 3) + 1) * _charWidth;

        double separatorX = hexPaneX + hexContentWidth + GutterPadding;
        double asciiPaneX = separatorX + GutterPadding;
        return new HexLayout(gutterWidth, hexPaneX, separatorX, asciiPaneX);
    }

    /// <summary>The left edge of hex byte cell <paramref name="byteIndex"/> - each cell is 2 hex digits + 1 gap char wide, with one extra gap char at the row's midpoint.</summary>
    private double HexByteX(int byteIndex, double hexPaneX)
    {
        double midGap = byteIndex >= BytesPerRow / 2 ? _charWidth : 0;
        return hexPaneX + (byteIndex * 3 * _charWidth) + midGap;
    }

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

            DrawRow(context, rowBrush, layout, y, rowStartOffset, rowBuffer[..byteCount], GetSelectionRangeForRow(rowStartOffset, byteCount));

            y += _lineHeight;
        }

        context.DrawLine(_separatorPen, new Point(layout.SeparatorX, 0), new Point(layout.SeparatorX, Bounds.Height));
    }

    private void DrawRow(
        DrawingContext context, IBrush rowBrush, HexLayout layout, double y, long rowStartOffset,
        ReadOnlySpan<byte> bytes, (int Start, int End)? selection)
    {
        context.FillRectangle(rowBrush, new Rect(layout.GutterWidth, y, Bounds.Width - layout.GutterWidth, _lineHeight));
        DrawOffset(context, rowStartOffset, layout.GutterWidth, y);

        if (selection is { } range)
        {
            DrawByteRangeHighlight(context, range.Start, range.End, layout, y);
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            DrawHexByte(context, bytes[i], HexByteX(i, layout.HexPaneX), y);
            DrawAsciiChar(context, bytes[i], layout.AsciiPaneX + (i * _charWidth), y);
        }
    }

    private void DrawOffset(DrawingContext context, long offset, double gutterWidth, double y)
    {
        var formatted = new FormattedText(offset.ToString("X8", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _gutterTextBrush);
        context.DrawText(formatted, new Point(gutterWidth - GutterPadding - formatted.Width, y));
    }

    private void DrawHexByte(DrawingContext context, byte value, double x, double y)
    {
        var formatted = new FormattedText(value.ToString("X2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _textBrush);
        context.DrawText(formatted, new Point(x, y));
    }

    private void DrawAsciiChar(DrawingContext context, byte value, double x, double y)
    {
        var formatted = new FormattedText(ToDisplayChar(value).ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _textBrush);
        context.DrawText(formatted, new Point(x, y));
    }

    /// <summary>Highlights the selected [start,end) byte range identically in both the hex and ASCII panes - the two panes always mirror the same selected bytes regardless of which pane the drag started in (see _selectionStartedInAsciiPane).</summary>
    private void DrawByteRangeHighlight(DrawingContext context, int start, int end, HexLayout layout, double y)
    {
        if (start >= end)
        {
            return;
        }

        double hexStartX = HexByteX(start, layout.HexPaneX);
        double hexEndX = HexByteX(end - 1, layout.HexPaneX) + (2 * _charWidth);
        context.FillRectangle(SelectionBrush, new Rect(hexStartX, y, hexEndX - hexStartX, _lineHeight));

        double asciiStartX = layout.AsciiPaneX + (start * _charWidth);
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
        bool inAsciiPane = position.X >= layout.SeparatorX;
        int byteIndex = inAsciiPane
            ? AsciiByteIndexFromX(position.X, layout.AsciiPaneX)
            : HexByteIndexFromX(position.X, layout.HexPaneX);

        byteIndex = Math.Clamp(byteIndex, 0, Math.Max(0, row.ByteCount - 1));
        return (new BytePosition(row.RowStartOffset + byteIndex), inAsciiPane);
    }

    private int AsciiByteIndexFromX(double x, double asciiPaneX) =>
        Math.Clamp((int)((x - asciiPaneX) / _charWidth), 0, BytesPerRow - 1);

    private int HexByteIndexFromX(double x, double hexPaneX)
    {
        double relative = Math.Max(0, x - hexPaneX);
        double halfWidth = (BytesPerRow / 2) * 3 * _charWidth;
        if (relative >= halfWidth)
        {
            relative -= _charWidth;
        }

        return Math.Clamp((int)(relative / (3 * _charWidth)), 0, BytesPerRow - 1);
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
}
