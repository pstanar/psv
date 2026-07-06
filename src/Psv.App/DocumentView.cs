using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Psv.Core;

namespace Psv.App;

internal readonly record struct DocumentPalette(Color Text, Color ZebraEven, Color ZebraOdd);

/// <summary>A (line, character-column) location within the document, in absolute (unscrolled) coordinates.</summary>
internal readonly record struct DocumentPosition(long Line, int Column) : IComparable<DocumentPosition>
{
    public int CompareTo(DocumentPosition other)
    {
        int lineCompare = Line.CompareTo(other.Line);
        return lineCompare != 0 ? lineCompare : Column.CompareTo(other.Column);
    }
}

/// <summary>
/// One visually rendered row from the last <see cref="DocumentView.Render"/> pass - the text
/// segment [SegmentStart, SegmentEnd) of LineNumber drawn at screen y. Cached so pointer hit
/// testing (for text selection) maps pixels back to a document position using exactly the layout
/// that's actually on screen, including per-segment bounds under word wrap, without redoing the
/// wrap/scroll math independently and risking it drifting out of sync with what's drawn.
/// </summary>
internal readonly record struct RenderedRow(double Y, long LineNumber, int SegmentStart, int SegmentEnd, string Text);

/// <summary>
/// Virtualized document viewport: decodes and draws only the lines currently visible, resolved
/// via <see cref="LineLocator.GetLineRanges"/>, so cost per frame is O(visible rows), never
/// O(file size) (plan §3.1). Zebra striping, the line-number gutter, soft-wrap (with horizontal
/// scroll for the non-wrap case), and font family/size/color are all user-configurable
/// (milestone 4). Font stays constrained to monospace fonts — see <see cref="FontFamily"/>.
/// Colors follow the OS light/dark theme by default; picking any color in the Appearance dialog
/// switches to a fixed custom palette until "Follow system theme" is re-enabled (plan §3.5).
/// Brushes and the typeface are cached in fields and refreshed only when the properties they
/// derive from change — rebuilding them per frame showed up as per-render garbage.
/// </summary>
public sealed class DocumentView : Control
{
    public static readonly StyledProperty<PsvDocument?> DocumentProperty =
        AvaloniaProperty.Register<DocumentView, PsvDocument?>(nameof(Document));

    public static readonly StyledProperty<long> TopLineProperty =
        AvaloniaProperty.Register<DocumentView, long>(nameof(TopLine));

    public static readonly StyledProperty<long> HorizontalOffsetProperty =
        AvaloniaProperty.Register<DocumentView, long>(nameof(HorizontalOffset));

    public static readonly StyledProperty<bool> ShowLineNumbersProperty =
        AvaloniaProperty.Register<DocumentView, bool>(nameof(ShowLineNumbers), defaultValue: true);

    public static readonly StyledProperty<bool> ShowColumnRulerProperty =
        AvaloniaProperty.Register<DocumentView, bool>(nameof(ShowColumnRuler), defaultValue: true);

    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<DocumentView, bool>(nameof(WordWrap), defaultValue: false);

    public static readonly StyledProperty<bool> ZebraStripingProperty =
        AvaloniaProperty.Register<DocumentView, bool>(nameof(ZebraStriping), defaultValue: true);

    public static readonly StyledProperty<bool> FollowSystemThemeProperty =
        AvaloniaProperty.Register<DocumentView, bool>(nameof(FollowSystemTheme), defaultValue: true);

    public static readonly StyledProperty<Color> ZebraEvenColorProperty =
        AvaloniaProperty.Register<DocumentView, Color>(nameof(ZebraEvenColor), defaultValue: Colors.White);

    public static readonly StyledProperty<Color> ZebraOddColorProperty =
        AvaloniaProperty.Register<DocumentView, Color>(nameof(ZebraOddColor), defaultValue: Color.FromRgb(0xF0, 0xF0, 0xF0));

    public static readonly StyledProperty<Color> TextColorProperty =
        AvaloniaProperty.Register<DocumentView, Color>(nameof(TextColor), defaultValue: Colors.Black);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<DocumentView, FontFamily>(nameof(FontFamily), defaultValue: new FontFamily("monospace"));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<DocumentView, double>(nameof(FontSize), defaultValue: 14);

    public static readonly StyledProperty<SearchMatch?> CurrentMatchProperty =
        AvaloniaProperty.Register<DocumentView, SearchMatch?>(nameof(CurrentMatch));

    /// <summary>
    /// Fonts a user can pick from the appearance dialog. Kept to known-monospace names on
    /// purpose — the whole rendering model (wrap math, gutter width, horizontal scroll) assumes
    /// fixed-width glyphs (plan: "Monospace only"), so a proportional font would silently break
    /// column alignment rather than fail loudly.
    /// </summary>
    public static readonly string[] AvailableFontFamilies =
    [
        "monospace", "Cascadia Mono", "Cascadia Code", "Consolas",
        "JetBrains Mono", "Courier New", "DejaVu Sans Mono", "Menlo", "Liberation Mono",
    ];

    private static readonly DocumentPalette LightPalette = new(
        Text: Colors.Black,
        ZebraEven: Colors.White,
        ZebraOdd: Color.FromRgb(0xF0, 0xF0, 0xF0));

    private static readonly DocumentPalette DarkPalette = new(
        Text: Color.FromRgb(0xE0, 0xE0, 0xE0),
        ZebraEven: Color.FromRgb(0x1E, 0x1E, 0x1E),
        ZebraOdd: Color.FromRgb(0x25, 0x25, 0x26));

    private static readonly Color MatchHighlightColor = Color.FromArgb(140, 255, 165, 0);
    private static readonly IBrush HighlightBrush = new SolidColorBrush(MatchHighlightColor);

    private static readonly Color LightGutterBackground = Color.FromRgb(0xE8, 0xE8, 0xE8);
    private static readonly Color DarkGutterBackground = Color.FromRgb(0x2D, 0x2D, 0x2D);
    private static readonly Color GutterTextGray = Color.FromRgb(0x80, 0x80, 0x80);

    // Rec. 601 luma coefficients, used to pick a light or dark gutter to match the zebra base color.
    private const double LumaRed = 0.299;
    private const double LumaGreen = 0.587;
    private const double LumaBlue = 0.114;
    private const double GutterLuminanceThreshold = 0.5;

    private const double LinePadding = 2;
    private const double GutterPadding = 6;
    private const int MinGutterDigits = 4;
    private const int WheelScrollLines = 3;
    private const int HorizontalArrowStepChars = 4;
    private static readonly TimeSpan AutoScrollInterval = TimeSpan.FromMilliseconds(80);

    // Middle-click "omniscroll", as in web browsers: after the anchor click, scroll speed grows
    // with how far the pointer strays from the anchor rather than snapping to one fixed rate, so a
    // small nudge scrolls gently and a large one scrolls fast without a separate gesture for each.
    private static readonly TimeSpan OmniScrollInterval = TimeSpan.FromMilliseconds(50);
    private const double OmniScrollDeadZone = 10;
    private const double OmniScrollPixelsPerUnit = 6;
    private const long OmniScrollMaxUnitsPerTick = 60;

    // Column ruler: three tiers of tick, like a physical ruler's graduations - a numbered tick
    // every 10 columns (frequent enough to eyeball an arbitrary column, sparse enough not to
    // clutter a narrow window), a medium unlabeled tick every 5, and a short tick on every other
    // column so the eye can still count individual characters between the labeled ones.
    private const int RulerMajorTickInterval = 10;
    private const int RulerMediumTickInterval = 5;
    private const double RulerMajorTickHeightFraction = 0.5;
    private const double RulerMediumTickHeightFraction = 0.3;
    private const double RulerMinorTickHeightFraction = 0.15;

    private static readonly Color HoverColumnColor = Color.FromArgb(60, 70, 130, 230);
    private static readonly IBrush HoverColumnBrush = new SolidColorBrush(HoverColumnColor);

    private static readonly Color SelectionColor = Color.FromArgb(100, 51, 153, 255);
    private static readonly IBrush SelectionBrush = new SolidColorBrush(SelectionColor);

    private Typeface _typeface;
    private IBrush _textBrush = Brushes.Black;
    private IBrush _evenRowBrush = Brushes.White;
    private IBrush _oddRowBrush = Brushes.White;
    private IBrush _gutterBackgroundBrush = Brushes.White;
    private IBrush _gutterTextBrush = Brushes.Gray;
    private IPen _rulerTickPen = new Pen(Brushes.Gray);

    // Populated fresh every Render() call - see RenderedRow for why.
    private readonly List<RenderedRow> _renderedRows = [];

    // Mouse-drag text selection. Anchor is where the drag started, Focus is the other end (the
    // current pointer position while dragging, or where it was released) - anchor == focus means
    // no selection. IsRectangular is decided once, from the Alt key state at drag start, and held
    // for the whole gesture rather than re-evaluated on every move.
    private DocumentPosition? _selectionAnchor;
    private DocumentPosition? _selectionFocus;
    private bool _selectionIsRectangular;
    private bool _isSelecting;

    // Keeps extending the selection while a drag is held past the top/bottom edge of the
    // viewport, since there's no other way to reach content that isn't currently visible without
    // releasing the drag and scrolling manually first.
    private DispatcherTimer? _autoScrollTimer;
    private int _autoScrollDirection;

    // Anchor point and live pointer position for the middle-click omniscroll gesture (see
    // OmniScrollInterval) - a null anchor means the gesture isn't active. The pointer is captured
    // for its duration so movement keeps scrolling even once the cursor leaves the control bounds.
    private DispatcherTimer? _omniScrollTimer;
    private Point? _omniScrollAnchor;
    private Point _omniScrollPointer;
    private IPointer? _omniScrollCapturedPointer;

    private long? _hoverColumn;

    private double _lineHeight;
    private double _charWidth;
    private int _lastMaxLineChars;

    static DocumentView()
    {
        AffectsRender<DocumentView>(
            DocumentProperty, TopLineProperty, HorizontalOffsetProperty,
            ShowLineNumbersProperty, ShowColumnRulerProperty, WordWrapProperty,
            ZebraStripingProperty, FollowSystemThemeProperty, ZebraEvenColorProperty, ZebraOddColorProperty,
            TextColorProperty, FontFamilyProperty, FontSizeProperty, CurrentMatchProperty);
    }

    public DocumentView()
    {
        Focusable = true;
        ClipToBounds = true;
        RefreshRenderResources();
        RecomputeMetrics();
    }

    public event EventHandler? ContentMeasured;

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

    public long HorizontalOffset
    {
        get => GetValue(HorizontalOffsetProperty);
        set => SetValue(HorizontalOffsetProperty, Math.Clamp(value, 0, MaxHorizontalOffset()));
    }

    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public bool ShowColumnRuler
    {
        get => GetValue(ShowColumnRulerProperty);
        set => SetValue(ShowColumnRulerProperty, value);
    }

    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
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

    public SearchMatch? CurrentMatch
    {
        get => GetValue(CurrentMatchProperty);
        set => SetValue(CurrentMatchProperty, value);
    }

    public int VisibleLineCount => Math.Max(1, (int)Math.Ceiling((Bounds.Height - ComputeRulerHeight()) / _lineHeight));

    internal long? HoverColumnForTests => _hoverColumn;

    internal void SetHoverPositionForTests(Point position) => UpdateHoverColumn(position);

    public int VisibleCharCount
    {
        get
        {
            // DrawText insets glyphs by GutterPadding from the row's left edge (see DrawText), so
            // that much of textAreaWidth never holds a character - omitting it here overcounts how
            // many columns actually fit, letting horizontal scroll expose a clipped final column.
            double textAreaWidth = Math.Max(_charWidth, Bounds.Width - ComputeGutterWidth());
            return Math.Max(1, (int)((textAreaWidth - GutterPadding) / _charWidth));
        }
    }

    public int LastMaxLineLength => _lastMaxLineChars;

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
            // A stale selection referring to line numbers/content from whatever was open before
            // must not survive into the newly opened document.
            _selectionAnchor = null;
            _selectionFocus = null;
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

        // Belt-and-suspenders: if the control is removed from the tree mid-drag (OnPointerReleased
        // never firing), a still-running timer would keep this instance alive via its own closure.
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

    private DocumentPalette GetEffectivePalette()
    {
        if (!FollowSystemTheme)
        {
            return new DocumentPalette(TextColor, ZebraEvenColor, ZebraOddColor);
        }

        bool isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        return isDark ? DarkPalette : LightPalette;
    }

    private static (Color Background, Color Text) GetGutterColors(Color zebraEvenColor)
    {
        double luminance = ((LumaRed * zebraEvenColor.R) + (LumaGreen * zebraEvenColor.G) + (LumaBlue * zebraEvenColor.B)) / 255.0;
        Color background = luminance > GutterLuminanceThreshold ? LightGutterBackground : DarkGutterBackground;
        return (background, GutterTextGray);
    }

    private void RefreshRenderResources()
    {
        _typeface = new Typeface(FontFamily);

        var palette = GetEffectivePalette();
        var (gutterBackground, gutterText) = GetGutterColors(palette.ZebraEven);
        _textBrush = new SolidColorBrush(palette.Text);
        _evenRowBrush = new SolidColorBrush(palette.ZebraEven);
        _oddRowBrush = new SolidColorBrush(palette.ZebraOdd);
        _gutterBackgroundBrush = new SolidColorBrush(gutterBackground);
        _gutterTextBrush = new SolidColorBrush(gutterText);
        _rulerTickPen = new Pen(_gutterTextBrush);
    }

    private void RecomputeMetrics()
    {
        var probe = new FormattedText("X", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.Black);
        _lineHeight = probe.Height + LinePadding;
        _charWidth = probe.Width;
    }

    private double ComputeGutterWidth()
    {
        if (!ShowLineNumbers)
        {
            return 0;
        }

        long knownLines = Document?.Index.KnownLineCount ?? 0;
        int digits = Math.Max(MinGutterDigits, CountDigits(knownLines));
        return (digits * _charWidth) + (GutterPadding * 2);
    }

    private double ComputeRulerHeight() => ShowColumnRuler ? _lineHeight : 0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.FillRectangle(_evenRowBrush, new Rect(Bounds.Size));

        var document = Document;
        if (document is null)
        {
            return;
        }

        bool showGutter = ShowLineNumbers;
        bool showRuler = ShowColumnRuler;
        bool wrap = WordWrap;
        bool zebra = ZebraStriping;
        long topLine = TopLine;
        long horizontalOffset = wrap ? 0 : HorizontalOffset;
        var currentMatch = CurrentMatch;

        double gutterWidth = ComputeGutterWidth();
        double rulerHeight = ComputeRulerHeight();
        double textAreaWidth = Math.Max(_charWidth, Bounds.Width - gutterWidth);
        // Same GutterPadding inset as VisibleCharCount - without it, wrapped rows would break one
        // character later than actually fits, clipping the last glyph of each wrapped segment.
        int charsPerRow = wrap ? Math.Max(1, (int)((textAreaWidth - GutterPadding) / _charWidth)) : int.MaxValue;

        if (showGutter)
        {
            context.FillRectangle(_gutterBackgroundBrush, new Rect(0, 0, gutterWidth, Bounds.Height));
        }

        if (showRuler)
        {
            // Drawn after the gutter fill so it also covers the gutter/ruler corner cell, keeping
            // that intersection visually part of the ruler rather than a leftover gutter sliver.
            DrawColumnRuler(context, gutterWidth, textAreaWidth, rulerHeight, horizontalOffset);
        }

        var ranges = document.Locator.GetLineRanges(topLine, VisibleLineCount);

        double y = rulerHeight;
        long lineNumber = topLine;
        int maxLineChars = 0;
        _renderedRows.Clear();

        foreach (var range in ranges)
        {
            if (y >= Bounds.Height)
            {
                break;
            }

            string text = document.Locator.DecodeLine(range);
            maxLineChars = Math.Max(maxLineChars, text.Length);
            var rowBrush = (zebra && lineNumber % 2 != 0) ? _oddRowBrush : _evenRowBrush;
            SearchMatch? matchOnLine = currentMatch is { } cm && cm.LineNumber == lineNumber ? currentMatch : null;

            if (wrap)
            {
                int segments = Math.Max(1, (int)Math.Ceiling((double)text.Length / charsPerRow));
                for (int seg = 0; seg < segments && y < Bounds.Height; seg++)
                {
                    int start = seg * charsPerRow;
                    int length = Math.Min(charsPerRow, text.Length - start);
                    int segEnd = start + length;
                    _renderedRows.Add(new RenderedRow(y, lineNumber, start, segEnd, text));
                    DrawRow(
                        context, rowBrush, gutterWidth, textAreaWidth, y,
                        showGutter && seg == 0 ? lineNumber : null,
                        text, start, segEnd, matchOnLine,
                        GetSelectionRangeForRow(lineNumber, start, segEnd));
                    y += _lineHeight;
                }
            }
            else
            {
                int segEnd = text.Length;
                _renderedRows.Add(new RenderedRow(y, lineNumber, (int)horizontalOffset, segEnd, text));
                DrawRow(
                    context, rowBrush, gutterWidth, textAreaWidth, y,
                    showGutter ? lineNumber : null,
                    text, (int)horizontalOffset, -1, matchOnLine,
                    GetSelectionRangeForRow(lineNumber, (int)horizontalOffset, segEnd));
                y += _lineHeight;
            }

            lineNumber++;
        }

        if (maxLineChars != _lastMaxLineChars)
        {
            _lastMaxLineChars = maxLineChars;
            ContentMeasured?.Invoke(this, EventArgs.Empty);
        }

        if (showRuler)
        {
            DrawHoverColumnHighlight(context, gutterWidth, rulerHeight, horizontalOffset);
        }
    }

    /// <summary>
    /// Draws one visual row: zebra background, optional gutter number, optional selection/match
    /// highlight, and the text segment [segStart, segEndExclusive) of the logical line
    /// (segEndExclusive of -1 means "to end of line"). Shared by the wrap and non-wrap render paths.
    /// </summary>
    private void DrawRow(
        DrawingContext context, IBrush rowBrush, double gutterWidth, double textAreaWidth, double y,
        long? gutterLineNumber, string text, int segStart, int segEndExclusive, SearchMatch? matchOnLine,
        (int Start, int End)? selectionRange)
    {
        context.FillRectangle(rowBrush, new Rect(gutterWidth, y, textAreaWidth, _lineHeight));

        if (gutterLineNumber is { } number)
        {
            DrawGutterNumber(context, number, gutterWidth, y);
        }

        if (selectionRange is { } selection)
        {
            DrawColumnRangeHighlight(context, SelectionBrush, selection.Start, selection.End, segStart, gutterWidth, y);
        }

        if (matchOnLine is { } match)
        {
            DrawMatchHighlight(context, match, segStart, segEndExclusive, gutterWidth, y);
        }

        int end = segEndExclusive < 0 ? text.Length : Math.Min(segEndExclusive, text.Length);
        if (segStart < end)
        {
            DrawText(context, text[segStart..end], gutterWidth, y);
        }
    }

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
            // Any click while omniscrolling cancels the gesture instead of performing its usual
            // action - this is also how browsers treat a click during middle-click autoscroll.
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
            // Right click never changes the selection - it just copies whatever is already
            // selected, so a user can right-click anywhere (including inside the selection) to
            // copy it without disturbing it.
            _ = CopySelectionToClipboardAsync();
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (position.Y < ComputeRulerHeight() || HitTest(position) is not { } hit)
        {
            return;
        }

        _selectionAnchor = hit;
        _selectionFocus = hit;
        _selectionIsRectangular = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        _isSelecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        UpdateHoverColumn(position);

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
            _selectionFocus = hit;
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
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHoverColumn(null);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        StopOmniScroll();
    }

    private void UpdateHoverColumn(Point position)
    {
        if (!ShowColumnRuler)
        {
            SetHoverColumn(null);
            return;
        }

        double gutterWidth = ComputeGutterWidth();
        if (position.X < gutterWidth + GutterPadding)
        {
            SetHoverColumn(null);
            return;
        }

        long horizontalOffset = WordWrap ? 0 : HorizontalOffset;
        long column = horizontalOffset + (long)((position.X - gutterWidth - GutterPadding) / _charWidth);
        SetHoverColumn(column);
    }

    private void SetHoverColumn(long? column)
    {
        if (_hoverColumn == column)
        {
            return;
        }

        _hoverColumn = column;
        InvalidateVisual();
    }

    /// <summary>
    /// Maps a pointer position to a document position using the last rendered frame's row layout
    /// (see <see cref="RenderedRow"/>), clamping to the nearest visible row/column rather than
    /// returning null for a drag that strays above, below, or to either side of the viewport -
    /// there's no auto-scroll, so clamping is what lets a drag past the edge still extend the
    /// selection to whatever is currently visible at that edge.
    /// </summary>
    private DocumentPosition? HitTest(Point position)
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

        double gutterWidth = ComputeGutterWidth();
        int relativeChar = (int)Math.Round((position.X - gutterWidth - GutterPadding) / _charWidth, MidpointRounding.AwayFromZero);
        int column = Math.Clamp(row.SegmentStart + relativeChar, row.SegmentStart, row.SegmentEnd);

        return new DocumentPosition(row.LineNumber, column);
    }

    /// <summary>
    /// The portion of [segmentStart, segmentEnd) on lineNumber currently selected, or null if none
    /// of that row is selected. Stream selection includes whole lines strictly between the anchor
    /// and focus lines and trims only the first/last line to the click columns; rectangular
    /// selection applies the same [minColumn, maxColumn) to every line in [minLine, maxLine]
    /// regardless of where each line's content actually ends.
    /// </summary>
    private (int Start, int End)? GetSelectionRangeForRow(long lineNumber, int segmentStart, int segmentEnd)
    {
        if (_selectionAnchor is not { } anchor || _selectionFocus is not { } focus || anchor.Equals(focus))
        {
            return null;
        }

        if (_selectionIsRectangular)
        {
            long minLine = Math.Min(anchor.Line, focus.Line);
            long maxLine = Math.Max(anchor.Line, focus.Line);
            if (lineNumber < minLine || lineNumber > maxLine)
            {
                return null;
            }

            int minColumn = Math.Min(anchor.Column, focus.Column);
            int maxColumn = Math.Max(anchor.Column, focus.Column);
            int rectStart = Math.Max(minColumn, segmentStart);
            int rectEnd = Math.Min(maxColumn, segmentEnd);
            return rectStart < rectEnd ? (rectStart, rectEnd) : null;
        }

        var from = anchor.CompareTo(focus) <= 0 ? anchor : focus;
        var to = anchor.CompareTo(focus) <= 0 ? focus : anchor;
        if (lineNumber < from.Line || lineNumber > to.Line)
        {
            return null;
        }

        int rowStart = lineNumber == from.Line ? Math.Max(from.Column, segmentStart) : segmentStart;
        int rowEnd = lineNumber == to.Line ? Math.Min(to.Column, segmentEnd) : segmentEnd;
        return rowStart < rowEnd ? (rowStart, rowEnd) : null;
    }

    /// <summary>Extracts the current selection as plain text, or null if there's no selection.</summary>
    private string? GetSelectedText()
    {
        var document = Document;
        if (document is null || _selectionAnchor is not { } anchor || _selectionFocus is not { } focus || anchor.Equals(focus))
        {
            return null;
        }

        if (_selectionIsRectangular)
        {
            long minLine = Math.Min(anchor.Line, focus.Line);
            long maxLine = Math.Max(anchor.Line, focus.Line);
            int minColumn = Math.Min(anchor.Column, focus.Column);
            int maxColumn = Math.Max(anchor.Column, focus.Column);

            var ranges = document.Locator.GetLineRanges(minLine, (int)(maxLine - minLine + 1));
            var lines = new string[ranges.Count];
            for (int i = 0; i < ranges.Count; i++)
            {
                string text = document.Locator.DecodeLine(ranges[i]);
                int start = Math.Min(minColumn, text.Length);
                int end = Math.Min(maxColumn, text.Length);
                lines[i] = end > start ? text[start..end] : string.Empty;
            }

            return string.Join(Environment.NewLine, lines);
        }

        var from = anchor.CompareTo(focus) <= 0 ? anchor : focus;
        var to = anchor.CompareTo(focus) <= 0 ? focus : anchor;

        var lineRanges = document.Locator.GetLineRanges(from.Line, (int)(to.Line - from.Line + 1));
        var streamLines = new string[lineRanges.Count];
        for (int i = 0; i < lineRanges.Count; i++)
        {
            long lineNumber = from.Line + i;
            string text = document.Locator.DecodeLine(lineRanges[i]);
            int start = lineNumber == from.Line ? Math.Min(from.Column, text.Length) : 0;
            int end = lineNumber == to.Line ? Math.Min(to.Column, text.Length) : text.Length;
            streamLines[i] = end > start ? text[start..end] : string.Empty;
        }

        return string.Join(Environment.NewLine, streamLines);
    }

    internal async Task CopySelectionToClipboardAsync()
    {
        string? text = GetSelectedText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    internal DocumentPosition? HitTestForTests(Point position) => HitTest(position);

    /// <summary>The pixel width DrawText would render this string at, after the same tab sanitization it applies - lets tests confirm a line's rendered width stays on the per-character _charWidth grid even when it contains tabs.</summary>
    internal double MeasureRenderedTextWidthForTests(string text)
    {
        var formatted = new FormattedText(SanitizeForDisplay(text), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _textBrush);
        return formatted.Width;
    }

    internal void BeginSelectionForTests(Point position, bool rectangular)
    {
        if (HitTest(position) is not { } hit)
        {
            return;
        }

        _selectionAnchor = hit;
        _selectionFocus = hit;
        _selectionIsRectangular = rectangular;
        InvalidateVisual();
    }

    internal void UpdateSelectionForTests(Point position)
    {
        if (HitTest(position) is { } hit)
        {
            _selectionFocus = hit;
            InvalidateVisual();
        }
    }

    internal string? SelectedTextForTests => GetSelectedText();

    internal DocumentPosition? SelectionFocusForTests => _selectionFocus;

    /// <summary>Sets the selection directly by document position, bypassing pixel hit-testing - for tests that exercise the selection/copy logic precisely rather than pixel-to-column mapping.</summary>
    internal void SetSelectionForTests(DocumentPosition anchor, DocumentPosition focus, bool rectangular)
    {
        _selectionAnchor = anchor;
        _selectionFocus = focus;
        _selectionIsRectangular = rectangular;
        InvalidateVisual();
    }

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

    internal double CharWidthForTests => _charWidth;

    internal double GutterPaddingForTests => GutterPadding;

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
                ScrollBy(-VisibleLineCount);
                break;
            case Key.PageDown:
                ScrollBy(VisibleLineCount);
                break;
            case Key.Home:
                TopLine = 0;
                break;
            case Key.End:
                TopLine = MaxTopLine();
                break;
            case Key.Left when !WordWrap:
                HorizontalOffset -= HorizontalArrowStepChars;
                break;
            case Key.Right when !WordWrap:
                HorizontalOffset += HorizontalArrowStepChars;
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = CopySelectionToClipboardAsync();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void DrawMatchHighlight(DrawingContext context, SearchMatch match, int segmentStart, int segmentEndExclusive, double gutterWidth, double y)
    {
        int matchEnd = match.Column + match.Length;
        int highlightStart = Math.Max(match.Column, segmentStart);
        int highlightEnd = segmentEndExclusive < 0 ? matchEnd : Math.Min(matchEnd, segmentEndExclusive);
        DrawColumnRangeHighlight(context, HighlightBrush, highlightStart, highlightEnd, segmentStart, gutterWidth, y);
    }

    /// <summary>Shared by the match and selection highlights: a background rectangle over [rangeStart, rangeEnd) within a row whose visible text starts at segmentStart.</summary>
    private void DrawColumnRangeHighlight(DrawingContext context, IBrush brush, int rangeStart, int rangeEnd, int segmentStart, double gutterWidth, double y)
    {
        if (rangeStart >= rangeEnd)
        {
            return;
        }

        double x = gutterWidth + GutterPadding + ((rangeStart - segmentStart) * _charWidth);
        double width = (rangeEnd - rangeStart) * _charWidth;
        context.FillRectangle(brush, new Rect(x, y, width, _lineHeight));
    }

    private void DrawGutterNumber(DrawingContext context, long lineNumber, double gutterWidth, double y)
    {
        var formatted = new FormattedText((lineNumber + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _gutterTextBrush);
        context.DrawText(formatted, new Point(gutterWidth - GutterPadding - formatted.Width, y));
    }

    /// <summary>
    /// Draws a tick at every visible column, aligned to the same per-character x positions
    /// <see cref="DrawText"/> and <see cref="DrawMatchHighlight"/> use so a tick always lines up
    /// exactly under the character it labels regardless of scroll offset. Three tiers, like a
    /// physical ruler's graduations: a numbered tick every <see cref="RulerMajorTickInterval"/>
    /// columns, a shorter unlabeled one every <see cref="RulerMediumTickInterval"/>, and an even
    /// shorter one on every other column.
    /// </summary>
    private void DrawColumnRuler(DrawingContext context, double gutterWidth, double textAreaWidth, double rulerHeight, long horizontalOffset)
    {
        context.FillRectangle(_gutterBackgroundBrush, new Rect(0, 0, Bounds.Width, rulerHeight));

        long firstVisibleColumn = horizontalOffset + 1;
        int visibleChars = (int)Math.Ceiling(textAreaWidth / _charWidth) + 1;
        long lastVisibleColumn = firstVisibleColumn + visibleChars;

        for (long column = firstVisibleColumn; column <= lastVisibleColumn; column++)
        {
            double x = gutterWidth + GutterPadding + ((column - firstVisibleColumn) * _charWidth);

            if (column % RulerMajorTickInterval == 0)
            {
                DrawRulerTick(context, x, rulerHeight, RulerMajorTickHeightFraction);
                var formatted = new FormattedText(column.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _gutterTextBrush);
                context.DrawText(formatted, new Point(x - (formatted.Width / 2), 0));
            }
            else if (column % RulerMediumTickInterval == 0)
            {
                DrawRulerTick(context, x, rulerHeight, RulerMediumTickHeightFraction);
            }
            else
            {
                DrawRulerTick(context, x, rulerHeight, RulerMinorTickHeightFraction);
            }
        }
    }

    private void DrawRulerTick(DrawingContext context, double x, double rulerHeight, double heightFraction) =>
        context.DrawLine(_rulerTickPen, new Point(x, rulerHeight * (1 - heightFraction)), new Point(x, rulerHeight));

    private void DrawHoverColumnHighlight(DrawingContext context, double gutterWidth, double rulerHeight, long horizontalOffset)
    {
        if (_hoverColumn is not { } column)
        {
            return;
        }

        long relative = column - horizontalOffset;
        if (relative < 0)
        {
            return;
        }

        double x = gutterWidth + GutterPadding + (relative * _charWidth);
        if (x >= Bounds.Width)
        {
            return;
        }

        context.FillRectangle(HoverColumnBrush, new Rect(x, 0, _charWidth, rulerHeight));
    }

    private void DrawText(DrawingContext context, string text, double x, double y)
    {
        if (text.Length == 0)
        {
            return;
        }

        var formatted = new FormattedText(SanitizeForDisplay(text), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, _textBrush);
        context.DrawText(formatted, new Point(x + GutterPadding, y));
    }

    /// <summary>
    /// Every other per-character calculation in this file (selection/match highlighting, the
    /// ruler, hit-testing) assumes one character occupies exactly one _charWidth cell, but
    /// FormattedText expands '\t' to the next tab stop instead of a single glyph cell - left
    /// as-is, a tab makes everything after it on the line render wider than its highlight
    /// rectangle, so text past the tab visually sits outside its own selection/match box.
    /// </summary>
    private static string SanitizeForDisplay(string text) => text.Contains('\t') ? text.Replace('\t', ' ') : text;

    private static int CountDigits(long value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }

        return digits;
    }

    private void ScrollBy(long deltaLines) => TopLine += deltaLines;

    private void UpdateAutoScrollForPointerY(double y)
    {
        if (y < ComputeRulerHeight())
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

    /// <summary>
    /// Scrolls one line further in the held direction and extends the focus end of the selection
    /// to the newly revealed edge line, keeping its column from the last real pointer position -
    /// _renderedRows won't reflect this scroll until the next composited frame, so re-hitting the
    /// stale layout here would lag a tick behind; deriving the edge line from TopLine directly
    /// doesn't have that problem.
    /// </summary>
    private void OnAutoScrollTick()
    {
        if (!_isSelecting || _autoScrollDirection == 0 || _selectionFocus is not { } focus)
        {
            StopAutoScroll();
            return;
        }

        ScrollBy(_autoScrollDirection);

        long edgeLine = _autoScrollDirection < 0 ? TopLine : TopLine + VisibleLineCount - 1;
        _selectionFocus = new DocumentPosition(edgeLine, focus.Column);
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

        long lineDelta = ComputeOmniScrollUnits(_omniScrollPointer.Y - anchor.Y);
        if (lineDelta != 0)
        {
            ScrollBy(lineDelta);
        }

        if (!WordWrap)
        {
            long charDelta = ComputeOmniScrollUnits(_omniScrollPointer.X - anchor.X);
            if (charDelta != 0)
            {
                HorizontalOffset += charDelta;
            }
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

    private long MaxTopLine()
    {
        var document = Document;
        if (document is null)
        {
            return 0;
        }

        return Math.Max(0, document.Index.KnownLineCount - VisibleLineCount);
    }

    private long MaxHorizontalOffset() => Math.Max(0, LastMaxLineLength - VisibleCharCount);
}
