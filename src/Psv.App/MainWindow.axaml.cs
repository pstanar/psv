using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Psv.Core;

namespace Psv.App;

public partial class MainWindow : Window
{
    // Bounds how long window close waits on settings persistence - a local per-user JSON file
    // normally writes near-instantly, but a hung/contended config store (e.g. a roaming profile
    // on a flaky network share) must not freeze shutdown indefinitely. The write itself isn't
    // cancelled at the timeout, just no longer waited on, so a save that's merely slow still lands.
    private static readonly TimeSpan SettingsSaveTimeout = TimeSpan.FromSeconds(2);

    // HScrollBar.SmallChange is in text mode's units (characters, set to 4 in XAML) - hex mode's
    // HorizontalOffset is pixels, where a step of 4 would be imperceptible, so hex mode overrides
    // it to a step that visibly moves the content per click.
    private const double HexHorizontalSmallChange = 24;

    private PsvDocument? _document;
    private string? _currentFilePath;
    private CancellationTokenSource? _indexCts;
    private bool _syncingScroll;
    private long _lastMaxTop;
    private bool _initialIndexSeen;

    // Detach target for the currently-subscribed document's Changed event - kept so OpenFile can
    // unsubscribe from the previous document before replacing it (see OnDocumentChanged).
    private Action? _documentChangedHandler;

    // Changed can fire many times in quick succession (once per checkpoint during a large initial
    // build, every 4096 lines/1MB) - coalesces those into a single pending UI refresh instead of
    // flooding the dispatcher queue with one Post per checkpoint. Guarded with Interlocked rather
    // than a plain bool since Changed is raised from whatever background thread mutated the
    // document, not the UI thread.
    private int _refreshPending;

    private DocumentSearcher? _searcher;
    private CancellationTokenSource? _searchCts;

    // Read from the background index-build continuation (TaskScheduler.Default), written from
    // the UI thread (OnToggleLiveTail, ApplySettings) - volatile for cross-thread visibility,
    // matching PsvDocument's _disposed/_pendingGrow/_pendingReplace flags.
    private volatile bool _tailingEnabled;

    private bool _exitOnEscape;

    private PixelPoint _lastNormalPosition;
    private Size _lastNormalSize = new(900, 600);

    public MainWindow()
    {
        InitializeComponent();

        ApplySettings(SettingsStore.Load());

        LineNumbersMenuItem.IsChecked = DocView.ShowLineNumbers;
        ColumnRulerMenuItem.IsChecked = DocView.ShowColumnRuler;
        WordWrapMenuItem.IsChecked = DocView.WordWrap;
        ZebraStripingMenuItem.IsChecked = DocView.ZebraStriping;
        LiveTailMenuItem.IsChecked = _tailingEnabled;
        ExitOnEscapeMenuItem.IsChecked = _exitOnEscape;
        SyncBytesPerRowMenu();
        UpdateHScrollBarState();

        HexV.PropertyChanged += (_, e) =>
        {
            if (e.Property == HexView.TopLineProperty)
            {
                if (!_syncingScroll)
                {
                    _syncingScroll = true;
                    VScrollBar.Value = HexV.TopLine;
                    _syncingScroll = false;
                }

                UpdatePositionStatus();
                RefreshFollowStatus();
            }
            else if (e.Property == HexView.HorizontalOffsetProperty)
            {
                if (!_syncingScroll)
                {
                    _syncingScroll = true;
                    HScrollBar.Value = HexV.HorizontalOffset;
                    _syncingScroll = false;
                }
            }
            else if (e.Property == HexView.BytesPerRowProperty
                || e.Property == HexView.BoundsProperty
                || e.Property == HexView.FontFamilyProperty
                || e.Property == HexView.FontSizeProperty)
            {
                // All four feed HexV.MaxHorizontalOffsetValue (row width and font metrics change
                // the content's total width; a resize changes the viewport it's measured against)
                // - re-derive whether the shared horizontal scrollbar should show and how far it
                // can go. BytesPerRow and Bounds.Height feed the *vertical* scrollbar the same way
                // (row count and rows-per-viewport respectively) - refresh that too.
                UpdateHScrollBarState();
                RefreshHexVerticalScrollBounds();

                if (e.Property == HexView.BytesPerRowProperty)
                {
                    // Keeps the View menu's radio selection correct even when BytesPerRow changes
                    // from outside a menu click - e.g. the --bin16/--bin32/--bin64 CLI flags.
                    SyncBytesPerRowMenu();
                }
            }
        };

        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _lastNormalPosition = e.Point;
            }
        };

        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty && WindowState == WindowState.Normal)
            {
                _lastNormalSize = ClientSize;
            }
        };

        DocView.PropertyChanged += (_, e) =>
        {
            if (e.Property == DocumentView.TopLineProperty)
            {
                // The status text must refresh regardless of which side triggered the change - a
                // scroll bar drag sets _syncingScroll to break the DocView<->ScrollBar feedback
                // loop, but that guard must not also suppress the status bar's own update.
                if (!_syncingScroll)
                {
                    _syncingScroll = true;
                    VScrollBar.Value = DocView.TopLine;
                    _syncingScroll = false;
                }

                UpdatePositionStatus();
                RefreshFollowStatus();
            }
            else if (e.Property == DocumentView.HorizontalOffsetProperty)
            {
                if (!_syncingScroll)
                {
                    _syncingScroll = true;
                    HScrollBar.Value = DocView.HorizontalOffset;
                    _syncingScroll = false;
                }

                UpdatePositionStatus();
            }
            else if (e.Property == DocumentView.BoundsProperty
                || e.Property == DocumentView.FontFamilyProperty
                || e.Property == DocumentView.FontSizeProperty)
            {
                // Mirrors HexV's equivalent branch above: a resize changes FullyVisibleLineCount
                // (the viewport DocView measures against) and a font-size change changes _lineHeight,
                // both of which feed RefreshTextVerticalScrollBounds' newMaxTop - and Bounds/font
                // metrics also feed UpdateHScrollBarState's overflow calculation. Without this,
                // resizing the window or changing font size on a static/idle text file left the
                // scrollbar stuck at its pre-change bounds forever, since RefreshTextVerticalScrollBounds
                // is otherwise only reachable in reaction to the document itself changing
                // (OnDocumentChanged) - a resize/font change alone never raises that.
                UpdateHScrollBarState();
                RefreshTextVerticalScrollBounds();
            }
        };

        DocView.ContentMeasured += (_, _) =>
        {
            // Deferred: ContentMeasured fires from inside DocView.Render(), and touching another
            // control's layout-affecting properties synchronously from within an active render
            // pass throws ("Visual was invalidated during the render pass").
            Dispatcher.UIThread.Post(() =>
            {
                UpdateHScrollBarState();

                // The rightmost reachable column in the status text tracks LastMaxLineLength, the
                // same value that just changed here - without this, it would only refresh on the
                // next scroll rather than as soon as the new line width is actually known.
                UpdatePositionStatus();
            });
        };

        VScrollBar.ValueChanged += (_, e) =>
        {
            if (_syncingScroll)
            {
                return;
            }

            _syncingScroll = true;
            if (_document is { IsBinary: true })
            {
                HexV.TopLine = (long)e.NewValue;
            }
            else
            {
                DocView.TopLine = (long)e.NewValue;
            }

            _syncingScroll = false;
        };

        HScrollBar.ValueChanged += (_, e) =>
        {
            if (_syncingScroll)
            {
                return;
            }

            _syncingScroll = true;
            if (_document is { IsBinary: true })
            {
                HexV.HorizontalOffset = e.NewValue;
            }
            else
            {
                DocView.HorizontalOffset = (long)e.NewValue;
            }

            _syncingScroll = false;
        };

        Opened += (_, _) => EnsureWindowIsOnScreen();

        Closing += (_, _) =>
        {
            var settings = CaptureSettings();
            Task.Run(() => SettingsStore.Save(settings)).Wait(SettingsSaveTimeout);
        };

        Closed += (_, _) =>
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = null;
            _document?.Dispose();

            // Unlike _searchCts/_indexCts above, this was never nulled after disposal - a
            // Dispatcher.Post continuation queued before Close() (e.g. OpenFile's tail-jump-to-end
            // callback) can still run afterward, and its ReferenceEquals(_document, document) guard
            // only protects against a *newer* document superseding this one, not against this same
            // document having just been disposed. Nulling it here makes that guard correctly fail
            // instead of proceeding to touch a disposed MappedFileByteSource.
            _document = null;
        };
    }

    // --- Settings (plan §3.4 window geometry / §3.5 appearance persistence) ---

    private void ApplySettings(AppSettings settings)
    {
        if (!double.IsNaN(settings.WindowX) && !double.IsNaN(settings.WindowY))
        {
            Position = new PixelPoint((int)settings.WindowX, (int)settings.WindowY);
        }

        Width = settings.WindowWidth;
        Height = settings.WindowHeight;
        _lastNormalSize = new Size(settings.WindowWidth, settings.WindowHeight);

        DocView.ShowLineNumbers = settings.ShowLineNumbers;
        DocView.ShowColumnRuler = settings.ShowColumnRuler;
        DocView.WordWrap = settings.WordWrap;
        DocView.ZebraStriping = settings.ZebraStriping;
        DocView.FollowSystemTheme = settings.FollowSystemTheme;
        _tailingEnabled = settings.TailingEnabled;
        _exitOnEscape = settings.ExitOnEscape;
        DocView.FontFamily = new FontFamily(settings.FontFamily);
        DocView.FontSize = settings.FontSize;
        DocView.TextColor = ParseColorOrDefault(settings.TextColor, Colors.Black);
        DocView.ZebraEvenColor = ParseColorOrDefault(settings.ZebraEvenColor, Colors.White);
        DocView.ZebraOddColor = ParseColorOrDefault(settings.ZebraOddColor, Color.FromRgb(0xF0, 0xF0, 0xF0));

        // HexView has no dedicated appearance settings of its own - it mirrors DocView's font,
        // color, and zebra-striping settings so the two views look consistent regardless of which
        // one a given file happens to open in.
        HexV.ZebraStriping = settings.ZebraStriping;
        HexV.FollowSystemTheme = settings.FollowSystemTheme;
        HexV.FontFamily = new FontFamily(settings.FontFamily);
        HexV.FontSize = settings.FontSize;
        HexV.TextColor = ParseColorOrDefault(settings.TextColor, Colors.Black);
        HexV.ZebraEvenColor = ParseColorOrDefault(settings.ZebraEvenColor, Colors.White);
        HexV.ZebraOddColor = ParseColorOrDefault(settings.ZebraOddColor, Color.FromRgb(0xF0, 0xF0, 0xF0));
        HexV.BytesPerRow = settings.HexBytesPerRow is 16 or 32 or 64 ? settings.HexBytesPerRow : HexView.DefaultBytesPerRow;

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static Color ParseColorOrDefault(string text, Color fallback)
    {
        try
        {
            return Color.Parse(text);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private AppSettings CaptureSettings() => new()
    {
        WindowX = _lastNormalPosition.X,
        WindowY = _lastNormalPosition.Y,
        WindowWidth = _lastNormalSize.Width,
        WindowHeight = _lastNormalSize.Height,
        WindowMaximized = WindowState == WindowState.Maximized,

        ShowLineNumbers = DocView.ShowLineNumbers,
        ShowColumnRuler = DocView.ShowColumnRuler,
        WordWrap = DocView.WordWrap,
        ZebraStriping = DocView.ZebraStriping,
        FollowSystemTheme = DocView.FollowSystemTheme,
        TailingEnabled = _tailingEnabled,
        ExitOnEscape = _exitOnEscape,
        HexBytesPerRow = HexV.BytesPerRow,

        FontFamily = DocView.FontFamily.Name,
        FontSize = DocView.FontSize,
        TextColor = DocView.TextColor.ToString(),
        ZebraEvenColor = DocView.ZebraEvenColor.ToString(),
        ZebraOddColor = DocView.ZebraOddColor.ToString(),
    };

    private void EnsureWindowIsOnScreen()
    {
        var screens = Screens.All;
        if (screens.Count == 0)
        {
            return;
        }

        if (screens.Any(s => s.Bounds.Contains(Position)))
        {
            return;
        }

        var primary = Screens.Primary ?? screens[0];
        var area = primary.WorkingArea;
        Position = new PixelPoint(
            area.X + ((area.Width - (int)_lastNormalSize.Width) / 2),
            area.Y + ((area.Height - (int)_lastNormalSize.Height) / 2));
    }

    // --- File opening / indexing progress ---

    internal PsvDocument? DocumentForTests => _document;

    internal string StatusStateTextForTests => StatusStateText.Text ?? string.Empty;

    internal string StatusEncodingTextForTests => StatusEncodingText.Text ?? string.Empty;

    internal string StatusPositionTextForTests => StatusPositionText.Text ?? string.Empty;

    internal bool HasIndexCtsForTests => _indexCts is not null;

    internal bool HasSearchCtsForTests => _searchCts is not null;

    internal bool TailingEnabledForTests => _tailingEnabled;

    internal bool ExitOnEscapeForTests => _exitOnEscape;

    internal long TopLineForTests => DocView.TopLine;

    internal long HexTopLineForTests => HexV.TopLine;

    internal bool IsHexViewActiveForTests => HexV.IsVisible;

    internal bool IsHexViewMenuCheckedForTests => HexViewMenuItem.IsChecked;

    internal bool IsFindMenuEnabledForTests => FindMenuItem.IsEnabled;

    internal bool IsGoToLineMenuEnabledForTests => GoToLineMenuItem.IsEnabled;

    internal bool IsCycleEncodingMenuEnabledForTests => CycleEncodingMenuItem.IsEnabled;

    internal void ToggleHexViewForTests() => OnToggleHexView(this, new RoutedEventArgs());

    internal HexView HexViewForTests => HexV;

    internal DocumentView DocumentViewForTests => DocView;

    internal bool IsHScrollBarVisibleForTests => HScrollBar.IsVisible;

    internal double HScrollBarMaximumForTests => HScrollBar.Maximum;

    internal bool IsVScrollBarVisibleForTests => VScrollBar.IsVisible;

    internal double VScrollBarMaximumForTests => VScrollBar.Maximum;

    internal bool BytesPerRow64MenuItemCheckedForTests => BytesPerRow64MenuItem.IsChecked;

    /// <param name="enableTailing">
    /// Overrides the current live-tail setting for this open (e.g. the CLI --tail switch) - null
    /// leaves whatever the user/settings already have it set to untouched. Updates the View menu
    /// checkbox either way, since it must always reflect whether tailing is actually going to run.
    /// </param>
    /// <param name="forceBinary">
    /// Forces binary/hex or text mode rather than auto-detecting from the file's leading bytes
    /// (the <c>--bin16</c>/<c>--bin32</c>/<c>--bin64</c> CLI flags or the Ctrl+B view-mode toggle,
    /// which reopens the file with the opposite of its current <see cref="PsvDocument.IsBinary"/>).
    /// Null lets detection decide.
    /// </param>
    /// <param name="forcedBytesPerRow">
    /// Overrides HexV's row width for this open (16, 32, or 64 - the <c>--bin16</c>/<c>--bin32</c>/
    /// <c>--bin64</c> CLI flags). Null leaves whatever row width is already set untouched, matching
    /// <paramref name="enableTailing"/>'s null-means-don't-override convention.
    /// </param>
    public void OpenFile(string path, TextEncodingKind? forcedEncoding = null, bool? enableTailing = null, bool? forceBinary = null, int? forcedBytesPerRow = null)
    {
        if (enableTailing is { } tail)
        {
            _tailingEnabled = tail;
            LiveTailMenuItem.IsChecked = tail;
        }

        PsvDocument document;
        try
        {
            // Opened before touching any current-document state: if this throws (missing file,
            // access denied, bad path from a CLI arg), the existing view/tailing must be left
            // completely alone rather than torn down for a file that never actually opened.
            document = PsvDocument.Open(path, forcedEncoding, forceBinary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStatusStateText("Failed to open", $"'{path}': {ex.Message}");
            return;
        }

        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = null;

        // Unsubscribe from the outgoing document before disposing it - not strictly required for
        // correctness (OnDocumentChanged's ReferenceEquals(_document, document) guard would catch a
        // stale event anyway), but avoids a lingering background task (e.g. a tail catch-up already
        // in flight) waking the handler for a document that's about to be replaced.
        if (_document is not null && _documentChangedHandler is not null)
        {
            _document.Changed -= _documentChangedHandler;
        }

        _document?.Dispose();
        CloseFindBar();
        _searcher = null;

        _document = document;
        _currentFilePath = path;

        // Surfaces PsvDocument.TailError (an unexpected, non-transient exception from the tail
        // catch-up loop, which PsvDocument stops retrying once this fires) instead of leaving
        // tailing silently dead with no indication why. The status text set here may later be
        // overwritten by a legitimate status change (e.g. scrolling) - acceptable for now since this
        // is a rare fault-reporting path, not the normal status flow.
        document.TailError += ex => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_document, document))
            {
                SetStatusStateText("Live tail stopped", ex.Message);
            }
        });

        // The sole trigger for refreshing redraw/scrollbar/status state once something in the
        // document actually changes - replaces the old 150ms polling timer entirely. Subscribed
        // before BuildIndexAsync below so the build's own Complete() call (always raised, even for
        // a file too small to ever hit a checkpoint) is never missed.
        _documentChangedHandler = () => OnDocumentChanged(document);
        document.Changed += _documentChangedHandler;

        // Must run before resetting TopLine below: it's what points DocView/HexV at the new
        // document in the first place. The old document was just disposed above - a TopLine reset
        // against a view still pointing at it wouldn't just show stale content, it would throw the
        // instant HexView.MaxTopLine() touches the disposed MappedFileByteSource's ReaderWriterLockSlim.
        ApplyViewMode();

        if (forcedBytesPerRow is { } bytesPerRow)
        {
            HexV.BytesPerRow = bytesPerRow;
        }

        DocView.TopLine = 0;
        DocView.HorizontalOffset = 0;
        HexV.TopLine = 0;
        VScrollBar.Value = 0;
        HScrollBar.Value = 0;
        _lastMaxTop = 0;
        _initialIndexSeen = false;

        Title = $"psv - {path}";
        StatusSizeText.Text = FormatFileSize(document.FileSizeBytes);
        StatusEncodingText.Text = string.Empty;
        StatusLineEndingText.Text = string.Empty;
        StatusPositionText.Text = string.Empty;
        StatusStateText.Text = document.IsBinary ? "Ready" : "Indexing...";

        var cts = new CancellationTokenSource();
        _indexCts = cts;

        // Tailing must not start until the initial build finishes — running Continue() on the
        // same LineIndex concurrently with the initial Build() would race (see plan §4). Only
        // start it if the build actually ran to completion: a cancellation (reopen/close raced
        // ahead of us) or a fault (corrupt/unreadable file) must not leave a tail watcher running
        // against a half-built or already-disposed document. Tailing itself defaults to off - the
        // user opts in via the View menu checkbox or the --tail CLI switch.
        document.BuildIndexAsync(cts.Token).ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    if (_tailingEnabled)
                    {
                        document.StartTailing();

                        // Jump to the end once, same as toggling the View menu checkbox on
                        // (SyncTailingToCurrentDocument) - a file opened with --tail should start
                        // following the end rather than sitting at the top waiting for growth.
                        // Must run on the UI thread since this continuation runs on the default
                        // scheduler; guard against a reopen having superseded _document meanwhile.
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ReferenceEquals(_document, document))
                            {
                                // Bring VScrollBar.Maximum up to date *before* the TopLine jump below -
                                // otherwise the TopLine setter's PropertyChanged handler pushes the new
                                // TopLine into VScrollBar.Value while Maximum is still its stale/default
                                // 0, and Avalonia clamps Value back down, rendering the thumb at the top
                                // even though the view itself is already showing the end of the file.
                                if (document.IsBinary)
                                {
                                    RefreshHexVerticalScrollBounds();
                                    HexV.TopLine = long.MaxValue;
                                }
                                else
                                {
                                    RefreshTextVerticalScrollBounds();
                                    DocView.TopLine = long.MaxValue;
                                }

                                // UpdateStatusBar (the only thing that ever writes "Ready"/"Following"
                                // to the status bar) is otherwise only called from OnDocumentChanged -
                                // this jump deliberately forces "Following" and an end-of-file scroll
                                // regardless of the generic refresh's own idea of "was already at the
                                // bottom", so it needs its own explicit call here too.
                                UpdateStatusBar(isFollowing: true);
                            }
                        });
                    }
                }
                else if (task.IsFaulted)
                {
                    var error = task.Exception!.GetBaseException();
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_document, document))
                        {
                            // Unsubscribe first: a checkpoint-driven refresh from partial progress
                            // before the fault (already queued, possibly still in flight) would
                            // otherwise overwrite this message right back to "Indexing... N lines so
                            // far" - Index.IsComplete never becomes true for a faulted build, so
                            // nothing would ever correct it back afterward.
                            if (_documentChangedHandler is not null)
                            {
                                document.Changed -= _documentChangedHandler;
                                _documentChangedHandler = null;
                            }

                            SetStatusStateText("Indexing failed", error.Message);
                        }
                    });
                }

                // Dispose on the UI thread - the same thread OpenFile()/Closed already use for
                // every other _indexCts mutation - so this never races a concurrent dispose from
                // there. If a reopen already superseded and disposed this same instance, _indexCts
                // no longer references it and the guard skips re-disposing it.
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_indexCts, cts))
                    {
                        _indexCts = null;
                        cts.Dispose();
                    }
                });
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// PsvDocument.Changed's handler - fires on whatever background thread mutated the document
    /// (a checkpoint appended, the build completing, a tail catch-up, an encoding rebuild), so this
    /// only marshals to the UI thread and coalesces. See <see cref="RefreshForDocumentChange"/> for
    /// the actual refresh logic.
    /// </summary>
    private void OnDocumentChanged(PsvDocument document)
    {
        // Changed can fire many times in quick succession - once per checkpoint during a large
        // initial build (every 4096 lines/1MB), or once per tail catch-up iteration - so this
        // coalesces bursts into a single pending UI refresh instead of flooding the dispatcher queue
        // with one Post per event. Mirrors PsvDocument's own _tailBusy coalescing pattern.
        if (Interlocked.CompareExchange(ref _refreshPending, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);

            // Guards against a reopen having superseded _document, or the window having closed,
            // since this Post was queued - same pattern as every other posted continuation here.
            if (ReferenceEquals(_document, document))
            {
                RefreshForDocumentChange();
            }
        });
    }

    /// <summary>
    /// Redraws and recomputes scrollbar/follow/status state for the current document - called only
    /// in reaction to a real change (see <see cref="OnDocumentChanged"/>), replacing what used to be
    /// a 150ms polling timer that diffed against cached "last known" values to detect whether
    /// anything had actually changed. Since this now only runs when something genuinely did, that
    /// diffing (and the cache fields it required) is gone entirely.
    /// </summary>
    private void RefreshForDocumentChange()
    {
        if (_document is not { } document)
        {
            return;
        }

        bool wasFollowing;

        if (document.IsBinary)
        {
            // Only apply follow-mode auto-scroll once at least one refresh has happened. Without
            // this, the very first refresh after opening a file has TopLine == 0 and _lastMaxTop
            // == 0 - trivially "at the bottom" by the >= check - which would snap a freshly-opened
            // file straight to its end instead of leaving it at the top.
            wasFollowing = _initialIndexSeen && HexV.TopLine >= _lastMaxTop;

            HexV.InvalidateVisual();
            RefreshHexVerticalScrollBounds();

            if (wasFollowing)
            {
                _syncingScroll = true;
                HexV.TopLine = _lastMaxTop;
                VScrollBar.Value = _lastMaxTop;
                _syncingScroll = false;
            }

            _initialIndexSeen = true;
        }
        else
        {
            wasFollowing = _initialIndexSeen && DocView.TopLine >= _lastMaxTop;

            DocView.InvalidateVisual();

            // FullyVisibleLineCount, not VisibleLineCount: must match DocView's own MaxTopLine() so
            // the scrollbar's Maximum never lets the user drag past the point where DocView clamps
            // TopLine itself, which would otherwise snap back visually on every such drag.
            long newMaxTop = RefreshTextVerticalScrollBounds();

            if (wasFollowing)
            {
                _syncingScroll = true;
                DocView.TopLine = newMaxTop;
                VScrollBar.Value = newMaxTop;
                _syncingScroll = false;
            }

            if (document.Index.IsComplete)
            {
                _initialIndexSeen = true;
            }
        }

        UpdateStatusBar(wasFollowing);
    }

    // --- Status bar ---

    /// <summary>
    /// Recomputes "Following" vs "Ready" from the current scroll position and refreshes the status
    /// bar immediately - called from the TopLine PropertyChanged handlers so a manual scroll away
    /// from (or back to) the bottom updates the status text right away. A scroll alone never raises
    /// PsvDocument.Changed (nothing about the document itself changed), so OnDocumentChanged's
    /// refresh would never pick this up on its own - without this, scrolling away from the bottom of
    /// an idle tailed file left the status bar stuck on "Following" until the file grew again.
    /// </summary>
    private void RefreshFollowStatus()
    {
        if (_document is not { } document)
        {
            return;
        }

        bool isFollowing = _initialIndexSeen &&
            (document.IsBinary ? HexV.TopLine >= _lastMaxTop : DocView.TopLine >= _lastMaxTop);

        UpdateStatusBar(isFollowing);
    }

    private void UpdateStatusBar(bool isFollowing)
    {
        if (_document is not { } document || _currentFilePath is not { } path)
        {
            return;
        }

        Title = $"psv - {path}";
        StatusSizeText.Text = FormatFileSize(document.FileSizeBytes);

        if (!document.IsBinary)
        {
            string encodingName = EncodingNames.ToDisplayName(document.Encoding);
            StatusEncodingText.Text = document.IsManualEncoding ? $"{encodingName} (manual)" : $"{encodingName} (auto)";

            StatusLineEndingText.Text = document.Index.DominantLineEnding switch
            {
                LineEndingKind.Lf => "LF",
                LineEndingKind.Cr => "CR",
                LineEndingKind.CrLf => "CRLF",
                _ => "—",
            };
        }

        UpdatePositionStatus();

        SetStatusStateText(document.IsBinary
            ? (isFollowing ? "Following" : "Ready")
            : !document.Index.IsComplete
                ? $"Indexing... {document.Index.KnownLineCount:N0} lines so far"
                : isFollowing ? "Following" : "Ready");
    }

    /// <summary>
    /// Sets the status-bar state text, with an optional full-detail tooltip for messages too long
    /// to comfortably fit inline (e.g. an exception message) - hovering reveals the rest instead of
    /// the status bar clipping or growing to fit. Passing null for <paramref name="detail"/> (the
    /// default, used for every normal Indexing/Ready/Following state) clears any tooltip left behind
    /// by an earlier error, so it doesn't linger over unrelated later status text.
    /// </summary>
    private void SetStatusStateText(string text, string? detail = null)
    {
        StatusStateText.Text = text;
        ToolTip.SetTip(StatusStateText, detail);
    }

    private void UpdatePositionStatus()
    {
        if (_document is not { } document)
        {
            StatusPositionText.Text = string.Empty;
            return;
        }

        if (document.IsBinary)
        {
            long topOffset = HexV.TopLine * HexV.BytesPerRow;
            StatusPositionText.Text = $"Offset 0x{topOffset:X8}  |  {document.FileSizeBytes:N0} bytes";
            return;
        }

        long line = DocView.TopLine + 1;
        long totalLines = document.Index.KnownLineCount;
        long col = DocView.HorizontalOffset + 1;
        long maxCol = DocView.LastMaxLineLength;
        StatusPositionText.Text = $"Line {line:N0} / {totalLines:N0}  |  Col {col:N0} / {maxCol:N0}";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{size:N1} {units[unit]}";
    }

    // --- File menu / view toggles / appearance ---

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            AllowMultiple = false,
        });

        string? path = files.FirstOrDefault()?.Path.LocalPath;
        if (path is not null)
        {
            OpenFile(path);
        }
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (_currentFilePath is not { } path)
        {
            return;
        }

        // Reload keeps whatever the current file is doing right now, manual or not - the same
        // encoding it's showing, and the same text/hex view mode, rather than re-running
        // auto-detection and risking a surprise flip the user didn't ask for.
        TextEncodingKind? forcedEncoding = _document is { IsManualEncoding: true } document ? document.Encoding : null;
        OpenFile(path, forcedEncoding, forceBinary: _document?.IsBinary);
    }

    private void OnEditCopyClick(object? sender, RoutedEventArgs e) =>
        _ = _document is { IsBinary: true } ? HexV.CopySelectionToClipboardAsync() : DocView.CopySelectionToClipboardAsync();

    private void OnToggleHexView(object? sender, RoutedEventArgs e)
    {
        if (_document is not { } document || _currentFilePath is not { } path)
        {
            return;
        }

        TextEncodingKind? forcedEncoding = document.IsManualEncoding ? document.Encoding : null;

        // A full reopen, not an in-place flip: DocView's line index and HexV's raw byte access
        // are mutually exclusive on a single PsvDocument (see PsvDocument.IsBinary), so there's no
        // "hybrid" document with both live at once to swap views on top of. Reopening is cheap
        // regardless of file size (memory-mapped), at the cost of resetting scroll position -
        // matching how reference hex editors behave switching modes on a file.
        OpenFile(path, forcedEncoding, enableTailing: null, forceBinary: !document.IsBinary);
    }

    /// <summary>Shows/hides DocView vs. HexV to match the current document's mode, and disables menu items that don't apply to hex-viewed content.</summary>
    private void ApplyViewMode()
    {
        bool hex = _document is { IsBinary: true };

        DocView.IsVisible = !hex;
        HexV.IsVisible = hex;
        DocView.Document = hex ? null : _document;
        HexV.Document = hex ? _document : null;
        HexViewMenuItem.IsChecked = hex;

        // Keyboard focus must follow the visible view. Nothing focuses either view explicitly on
        // open - DocView only ends up focused because it's the first focusable control when the
        // window opens - so a file opened (or Ctrl+B-toggled) into hex mode would leave focus on
        // the now-hidden DocView and every navigation key would be dead until the user clicked
        // inside the hex view.
        FocusActiveView();

        StatusEncodingText.IsVisible = !hex;
        StatusLineEndingText.IsVisible = !hex;

        // Find, Go To Line, and Cycle Encoding all operate on the line index / text search
        // machinery, which a binary document never builds (see PsvDocument.IsBinary) - disabled
        // rather than silently doing nothing when clicked.
        FindMenuItem.IsEnabled = !hex;
        GoToLineMenuItem.IsEnabled = !hex;
        CycleEncodingMenuItem.IsEnabled = !hex;

        UpdateHScrollBarState();
        RefreshHexVerticalScrollBounds();
    }

    private void OnToggleLineNumbers(object? sender, RoutedEventArgs e)
    {
        DocView.ShowLineNumbers = LineNumbersMenuItem.IsChecked;
        UpdateHScrollBarState();
    }

    private void OnToggleColumnRuler(object? sender, RoutedEventArgs e)
    {
        DocView.ShowColumnRuler = ColumnRulerMenuItem.IsChecked;
    }

    private void OnToggleWordWrap(object? sender, RoutedEventArgs e)
    {
        DocView.WordWrap = WordWrapMenuItem.IsChecked;
        UpdateHScrollBarState();
    }

    private void OnToggleZebraStriping(object? sender, RoutedEventArgs e)
    {
        DocView.ZebraStriping = ZebraStripingMenuItem.IsChecked;
        HexV.ZebraStriping = ZebraStripingMenuItem.IsChecked;
    }

    private void OnBytesPerRowChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int bytesPerRow))
        {
            HexV.BytesPerRow = bytesPerRow;
        }
    }

    /// <summary>Reflects HexV's current BytesPerRow in the View menu's radio selection - not automatic, since the menu items aren't data-bound to it (see HexV.PropertyChanged for BytesPerRowProperty).</summary>
    private void SyncBytesPerRowMenu()
    {
        (HexV.BytesPerRow switch
        {
            16 => BytesPerRow16MenuItem,
            64 => BytesPerRow64MenuItem,
            _ => BytesPerRow32MenuItem,
        }).IsChecked = true;
    }

    private void OnToggleLiveTail(object? sender, RoutedEventArgs e)
    {
        _tailingEnabled = LiveTailMenuItem.IsChecked;
        SyncTailingToCurrentDocument();
    }

    internal void SetTailingEnabledForTests(bool enabled)
    {
        LiveTailMenuItem.IsChecked = enabled;
        OnToggleLiveTail(this, new RoutedEventArgs());
    }

    private void OnToggleExitOnEscape(object? sender, RoutedEventArgs e)
    {
        _exitOnEscape = ExitOnEscapeMenuItem.IsChecked;
    }

    internal void SetExitOnEscapeForTests(bool enabled)
    {
        ExitOnEscapeMenuItem.IsChecked = enabled;
        OnToggleExitOnEscape(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Starts or stops tailing on the current document to match <see cref="_tailingEnabled"/>.
    /// If the initial index build hasn't finished yet, does nothing on enable - the build's own
    /// completion continuation checks <see cref="_tailingEnabled"/> and starts tailing then;
    /// starting it early would race the still-running initial Build() against Continue().
    /// </summary>
    private void SyncTailingToCurrentDocument()
    {
        if (_document is not { } document)
        {
            return;
        }

        if (_tailingEnabled)
        {
            // A binary document never builds a line index (see PsvDocument.IsBinary), so there's
            // no Build()-vs-Continue() race to wait out - tailing can start immediately.
            if (document.IsBinary || document.Index.IsComplete)
            {
                document.StartTailing();

                // Same Maximum-before-jump ordering as OpenFile's initial-tail jump - see the
                // comment there for why skipping this leaves the scrollbar thumb stuck at the top.
                if (document.IsBinary)
                {
                    RefreshHexVerticalScrollBounds();
                    HexV.TopLine = long.MaxValue;
                }
                else
                {
                    RefreshTextVerticalScrollBounds();
                    DocView.TopLine = long.MaxValue;
                }

                // Same reasoning as OpenFile's jump-to-end callback: this deliberately forces
                // "Following" regardless of current scroll position, which a generic
                // OnDocumentChanged-triggered refresh wouldn't do on its own.
                UpdateStatusBar(isFollowing: true);
            }
        }
        else
        {
            document.StopTailing();
            UpdateStatusBar(isFollowing: false);
        }
    }

    /// <summary>Focuses whichever of DocView/HexV is currently visible, per ApplyViewMode.</summary>
    private void FocusActiveView() =>
        ((InputElement)(_document is { IsBinary: true } ? HexV : DocView)).Focus();

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        await new AboutWindow().ShowDialog(this);
        FocusActiveView();
    }

    private async void OnAppearanceClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AppearanceWindow();
        dialog.LoadFrom(DocView);
        await dialog.ShowDialog(this);
        FocusActiveView();
        if (dialog.Applied)
        {
            dialog.ApplyTo(DocView);

            // HexView has no dedicated appearance settings of its own - see ApplySettings.
            HexV.FontFamily = DocView.FontFamily;
            HexV.FontSize = DocView.FontSize;
            HexV.FollowSystemTheme = DocView.FollowSystemTheme;
            HexV.TextColor = DocView.TextColor;
            HexV.ZebraEvenColor = DocView.ZebraEvenColor;
            HexV.ZebraOddColor = DocView.ZebraOddColor;
        }
    }

    private void UpdateHScrollBarState()
    {
        if (_document is { IsBinary: true })
        {
            // Unlike DocView's line-length scan, HexV's content width is a closed-form function of
            // BytesPerRow and font metrics - MaxHorizontalOffsetValue is exact the instant either
            // changes, no "measured" event to wait for.
            double overflow = HexV.MaxHorizontalOffsetValue;

            _syncingScroll = true;
            HScrollBar.Maximum = overflow;
            HScrollBar.SmallChange = HexHorizontalSmallChange;
            HScrollBar.IsVisible = overflow > 0;
            _syncingScroll = false;
            return;
        }

        if (DocView.WordWrap)
        {
            HScrollBar.IsVisible = false;
            return;
        }

        int textOverflow = Math.Max(0, DocView.LastMaxLineLength - DocView.VisibleCharCount);

        _syncingScroll = true;
        HScrollBar.Maximum = textOverflow;
        HScrollBar.SmallChange = 4;
        HScrollBar.IsVisible = textOverflow > 0;
        _syncingScroll = false;
    }

    /// <summary>
    /// Recomputes the vertical scrollbar's Maximum/IsVisible for hex mode from the document's
    /// current byte length, HexV.BytesPerRow, and HexV.FullyVisibleRowCount - callable independently
    /// of OnDocumentChanged's refresh (which only reacts to the document itself changing, e.g. the
    /// file growing). Row count and visible-row count both depend on state that can change without
    /// the file's length ever moving - BytesPerRow via the View menu, or the viewport height via a
    /// window resize - neither of which raises PsvDocument.Changed. Without this as a separate,
    /// unconditional recompute, a wider/taller layout that later shrinks back down to needing a
    /// scrollbar would never actually show one - the same bug this fixes for the horizontal
    /// scrollbar (see UpdateHScrollBarState) via BytesPerRowProperty/BoundsProperty/font changes.
    /// </summary>
    private void RefreshHexVerticalScrollBounds()
    {
        if (_document is not { IsBinary: true } document)
        {
            return;
        }

        long totalRows = (document.FileSizeBytes + HexV.BytesPerRow - 1) / HexV.BytesPerRow;
        long newMaxTop = Math.Max(0, totalRows - HexV.FullyVisibleRowCount);

        _syncingScroll = true;
        VScrollBar.Maximum = newMaxTop;
        VScrollBar.IsVisible = newMaxTop > 0;
        _syncingScroll = false;

        _lastMaxTop = newMaxTop;
    }

    /// <summary>
    /// Text-mode counterpart to <see cref="RefreshHexVerticalScrollBounds"/> - recomputes
    /// VScrollBar's Maximum/IsVisible from the document's current known line count and
    /// DocView.FullyVisibleLineCount. Callable independently of a document change (e.g. from
    /// DocView's Bounds/FontFamily/FontSize handler) as well as from callers that jump
    /// DocView.TopLine straight to end-of-file (the initial live-tail jump in OpenFile and
    /// SyncTailingToCurrentDocument), which need Maximum brought up to date *before* that jump -
    /// otherwise the TopLine setter's own PropertyChanged handler pushes the new TopLine into
    /// VScrollBar.Value while Maximum is still stale (e.g. 0 on a freshly opened file), and
    /// Avalonia's RangeBase silently clamps Value back down, leaving the thumb at the top even
    /// though DocView is already showing the end of the file.
    /// </summary>
    private long RefreshTextVerticalScrollBounds()
    {
        if (_document is not { IsBinary: false } document)
        {
            return 0;
        }

        long known = document.Index.KnownLineCount;
        long newMaxTop = Math.Max(0, known - DocView.FullyVisibleLineCount);

        _syncingScroll = true;
        VScrollBar.Maximum = newMaxTop;
        VScrollBar.IsVisible = newMaxTop > 0;
        _syncingScroll = false;

        _lastMaxTop = newMaxTop;
        return newMaxTop;
    }

    // --- Go To Line ---

    private void OnGoToLineClick(object? sender, RoutedEventArgs e) => _ = ShowGoToLineDialogAsync();

    private async Task ShowGoToLineDialogAsync()
    {
        // Go To Line operates on the line index, which a binary document never builds (see
        // PsvDocument.IsBinary) - disabled in the menu too (ApplyViewMode), this guard covers the
        // Ctrl+G keybinding, which bypasses the menu item's IsEnabled entirely.
        if (_document is not { IsBinary: false } document)
        {
            return;
        }

        var dialog = new GoToLineWindow();
        dialog.SetLineRange(DocView.TopLine + 1, Math.Max(1, document.Index.KnownLineCount));

        // ShowDialog runs a nested dispatcher loop, so a live-tailed, actively-growing file can
        // keep advancing KnownLineCount while this dialog sits open - without this, the upper bound
        // stays frozen at whatever it was when the dialog opened, rejecting a line number that has
        // since become valid. UpdateMaxLine only widens the bound, never touching what the user has
        // already typed into the box.
        void OnDocumentChangedWhileOpen() => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_document, document))
            {
                dialog.UpdateMaxLine(Math.Max(1, document.Index.KnownLineCount));
            }
        });

        document.Changed += OnDocumentChangedWhileOpen;
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            document.Changed -= OnDocumentChangedWhileOpen;
        }

        if (dialog.ChosenLineNumber is { } lineNumber)
        {
            DocView.TopLine = lineNumber - 1;
        }
    }

    // --- Manual encoding override (plan §2.2.1) ---

    private void OnCycleEncodingClick(object? sender, RoutedEventArgs e) => _ = CycleEncodingAsync();

    private Task CycleEncodingAsync() =>
        _document is { IsBinary: false } document ? ApplyEncodingAsync(EncodingNames.Next(document.Encoding)) : Task.CompletedTask;

    /// <summary>Exercises exactly what a flyout MenuItem's Click handler does, without needing to drive the popup itself.</summary>
    internal Task SelectEncodingForTests(TextEncodingKind kind) => ApplyEncodingAsync(kind);

    /// <summary>
    /// Opens a popup on the status bar's encoding label listing every supported encoding, letting
    /// the user jump straight to one instead of stepping through <see cref="CycleEncodingAsync"/>
    /// one at a time. The currently-active encoding is shown checked.
    /// </summary>
    private void OnEncodingLabelClick(object? sender, PointerPressedEventArgs e)
    {
        if (_document is not { } document)
        {
            return;
        }

        var flyout = new MenuFlyout
        {
            // The label lives in the bottom status bar, so the default downward placement opens
            // mostly or entirely off-window - anchor above it instead.
            Placement = PlacementMode.Top,
        };
        foreach (var kind in EncodingNames.CycleOrder)
        {
            var item = new MenuItem
            {
                Header = EncodingNames.ToDisplayName(kind),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = kind == document.Encoding,
            };
            item.Click += (_, _) => _ = ApplyEncodingAsync(kind);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(StatusEncodingText);
    }

    private async Task ApplyEncodingAsync(TextEncodingKind newEncoding)
    {
        if (_document is not { } document)
        {
            return;
        }

        bool rebuilt = await document.ChangeEncodingAsync(newEncoding);

        if (rebuilt)
        {
            _lastMaxTop = 0;
            _initialIndexSeen = false;
            DocView.TopLine = 0;

            _syncingScroll = true;
            VScrollBar.Value = 0;
            _syncingScroll = false;
        }

        DocView.InvalidateVisual();
        UpdateStatusBar(isFollowing: false);
    }

    /// <summary>
    /// DocView/HexView's own OnKeyUp already reclaims keyboard focus after Avalonia's access-key
    /// handler steals it on Alt release (see their OnKeyUp for the full rationale), but that
    /// handler's visual side effect - leaving the File menu looking "selected" - is separate
    /// Menu-internal state (MenuBase.SelectedIndex) that reclaiming focus alone doesn't reset.
    /// Clearing it here, at the window level, cleans up the visual artifact left behind by a
    /// rectangular drag's Alt release.
    /// </summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);

        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            MainMenu.SelectedIndex = -1;
        }
    }

    // --- Search (plan §2.6 / milestone 6) ---

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            OpenFindBar();
            e.Handled = true;
        }
        else if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control)
        {
            _ = ShowGoToLineDialogAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.E && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            _ = CycleEncodingAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.B && e.KeyModifiers == KeyModifiers.Control)
        {
            // Unlike the checkbox-style toggles above, OnToggleHexView derives its target state
            // from the document itself (IsBinary), not from HexViewMenuItem.IsChecked - the menu
            // checkbox just reflects whatever ApplyViewMode decides after the reopen completes.
            OnToggleHexView(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            OnReloadClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
        {
            LineNumbersMenuItem.IsChecked = !LineNumbersMenuItem.IsChecked;
            OnToggleLineNumbers(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.Control)
        {
            ColumnRulerMenuItem.IsChecked = !ColumnRulerMenuItem.IsChecked;
            OnToggleColumnRuler(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
        {
            WordWrapMenuItem.IsChecked = !WordWrapMenuItem.IsChecked;
            OnToggleWordWrap(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Z && e.KeyModifiers == KeyModifiers.Control)
        {
            ZebraStripingMenuItem.IsChecked = !ZebraStripingMenuItem.IsChecked;
            OnToggleZebraStriping(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
        {
            LiveTailMenuItem.IsChecked = !LiveTailMenuItem.IsChecked;
            OnToggleLiveTail(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && FindBar.IsVisible)
        {
            bool forward = e.KeyModifiers != KeyModifiers.Shift;
            _ = RunSearchAsync(forward);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FindBar.IsVisible)
        {
            CloseFindBar();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _exitOnEscape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnFindMenuClick(object? sender, RoutedEventArgs e) => OpenFindBar();

    private void OpenFindBar()
    {
        // Find operates on DocumentSearcher/the line index, which a binary document never builds
        // (see PsvDocument.IsBinary) - disabled in the menu too (ApplyViewMode), this guard covers
        // the Ctrl+F keybinding, which bypasses the menu item's IsEnabled entirely.
        if (_document is { IsBinary: true })
        {
            return;
        }

        FindBar.IsVisible = true;
        FindTextBox.SelectAll();
        FindTextBox.Focus();
    }

    private void CloseFindBar()
    {
        _searchCts?.Cancel();
        FindBar.IsVisible = false;
        FindStatusText.Text = string.Empty;
        DocView.CurrentMatch = null;
        DocView.Focus();
    }

    private void OnFindCloseClick(object? sender, RoutedEventArgs e) => CloseFindBar();

    private void OnFindOptionChanged(object? sender, RoutedEventArgs e)
    {
        DocView.CurrentMatch = null;
        FindStatusText.Text = string.Empty;
    }

    private void OnFindTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            bool forward = e.KeyModifiers != KeyModifiers.Shift;
            _ = RunSearchAsync(forward);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private void OnFindNextClick(object? sender, RoutedEventArgs e) => _ = RunSearchAsync(forward: true);

    private void OnFindPreviousClick(object? sender, RoutedEventArgs e) => _ = RunSearchAsync(forward: false);

    private async Task RunSearchAsync(bool forward)
    {
        if (_document is null || string.IsNullOrEmpty(FindTextBox.Text))
        {
            return;
        }

        SearchMatcher matcher;
        try
        {
            matcher = new SearchMatcher(
                FindTextBox.Text,
                FindRegexCheckBox.IsChecked == true ? SearchMode.Regex : SearchMode.Substring,
                FindCaseSensitiveCheckBox.IsChecked == true);
        }
        catch (RegexParseException)
        {
            FindStatusText.Text = "Invalid regex";
            return;
        }
        catch (ArgumentException)
        {
            FindStatusText.Text = "Invalid pattern";
            return;
        }

        var searcher = _searcher ??= new DocumentSearcher(_document.Index, _document.Locator);

        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var current = DocView.CurrentMatch;
        long fromLine = current?.LineNumber ?? DocView.TopLine;
        int fromColumn = current is { } m ? (forward ? m.Column + m.Length : m.Column) : 0;

        FindStatusText.Text = "Searching...";

        try
        {
            // Task.Run, not a direct await: the searcher's scan loop is synchronous between
            // awaits, so calling it inline would run a potentially multi-second scan on the UI
            // thread and freeze the window.
            SearchMatch? result = forward
                ? await Task.Run(() => searcher.FindNextAsync(matcher, fromLine, fromColumn, cts.Token), cts.Token)
                : await Task.Run(() => searcher.FindPreviousAsync(matcher, fromLine, fromColumn, cts.Token), cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (result is { } match)
            {
                DocView.CurrentMatch = match;
                long viewportCenter = Math.Max(0, match.LineNumber - (DocView.VisibleLineCount / 2));
                DocView.TopLine = viewportCenter;
                FindStatusText.Text = string.Empty;
            }
            else
            {
                DocView.CurrentMatch = null;
                FindStatusText.Text = "Not found";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search (query changed, bar closed, window closed).
        }
        finally
        {
            // Whichever call created cts is the one that disposes it, exactly once, whether it
            // ran to completion or was cancelled by a newer search superseding it. The
            // ReferenceEquals guard stops a superseded call from nulling out a newer call's
            // _searchCts - only the call that's still current clears the field.
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }

            cts.Dispose();
        }
    }
}
