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
    private static readonly TimeSpan ProgressTickInterval = TimeSpan.FromMilliseconds(150);

    // Bounds how long window close waits on settings persistence - a local per-user JSON file
    // normally writes near-instantly, but a hung/contended config store (e.g. a roaming profile
    // on a flaky network share) must not freeze shutdown indefinitely. The write itself isn't
    // cancelled at the timeout, just no longer waited on, so a save that's merely slow still lands.
    private static readonly TimeSpan SettingsSaveTimeout = TimeSpan.FromSeconds(2);

    private PsvDocument? _document;
    private string? _currentFilePath;
    private CancellationTokenSource? _indexCts;
    private DispatcherTimer? _progressTimer;
    private bool _syncingScroll;
    private long _lastMaxTop;
    private long _lastKnownLineCount = -1;
    private long _lastKnownByteLength = -1;
    private bool _initialIndexSeen;

    private DocumentSearcher? _searcher;
    private CancellationTokenSource? _searchCts;

    // Read from the background index-build continuation (TaskScheduler.Default), written from
    // the UI thread (OnToggleLiveTail, ApplySettings) - volatile for cross-thread visibility,
    // matching PsvDocument's _disposed/_pendingGrow/_pendingReplace flags.
    private volatile bool _tailingEnabled;

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
            DocView.HorizontalOffset = (long)e.NewValue;
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
            _progressTimer?.Stop();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _indexCts?.Cancel();
            _indexCts?.Dispose();
            _indexCts = null;
            _document?.Dispose();
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

    /// <param name="enableTailing">
    /// Overrides the current live-tail setting for this open (e.g. the CLI --tail switch) - null
    /// leaves whatever the user/settings already have it set to untouched. Updates the View menu
    /// checkbox either way, since it must always reflect whether tailing is actually going to run.
    /// </param>
    /// <param name="forceBinary">
    /// Forces binary/hex or text mode rather than auto-detecting from the file's leading bytes
    /// (the <c>--bin</c> CLI flag or the Ctrl+B view-mode toggle, which reopens the file with the
    /// opposite of its current <see cref="PsvDocument.IsBinary"/>). Null lets detection decide.
    /// </param>
    public void OpenFile(string path, TextEncodingKind? forcedEncoding = null, bool? enableTailing = null, bool? forceBinary = null)
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
            StatusStateText.Text = $"Failed to open '{path}': {ex.Message}";
            return;
        }

        _progressTimer?.Stop();
        _indexCts?.Cancel();
        _indexCts?.Dispose();
        _indexCts = null;
        _document?.Dispose();
        CloseFindBar();
        _searcher = null;

        _document = document;
        _currentFilePath = path;

        // Must run before resetting TopLine below: it's what points DocView/HexV at the new
        // document in the first place. The old document was just disposed above - a TopLine reset
        // against a view still pointing at it wouldn't just show stale content, it would throw the
        // instant HexView.MaxTopLine() touches the disposed MappedFileByteSource's ReaderWriterLockSlim.
        ApplyViewMode();

        DocView.TopLine = 0;
        DocView.HorizontalOffset = 0;
        HexV.TopLine = 0;
        VScrollBar.Value = 0;
        HScrollBar.Value = 0;
        _lastMaxTop = 0;
        _lastKnownLineCount = -1;
        _lastKnownByteLength = -1;
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
                                if (document.IsBinary)
                                {
                                    HexV.TopLine = long.MaxValue;
                                }
                                else
                                {
                                    DocView.TopLine = long.MaxValue;
                                }
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
                            // Otherwise the next tick's UpdateStatusBar() overwrites this with
                            // "Indexing... 0 lines so far" - Index.IsComplete never becomes true
                            // for a document whose build faulted, so nothing would ever correct
                            // that back to the failure message.
                            _progressTimer?.Stop();
                            StatusStateText.Text = $"Indexing failed: {error.Message}";
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

        _progressTimer = new DispatcherTimer(ProgressTickInterval, DispatcherPriority.Background, (_, _) => OnProgressTick());
        _progressTimer.Start();
    }

    private void OnProgressTick()
    {
        if (_document is not { } document)
        {
            return;
        }

        bool wasFollowing;

        if (document.IsBinary)
        {
            long length = document.FileSizeBytes;

            // Nothing changed since the last tick - same early-out as text mode below, just keyed
            // on byte length instead of line count since there's no index to be "complete".
            if (length == _lastKnownByteLength)
            {
                return;
            }

            long totalRows = (length + HexView.BytesPerRow - 1) / HexView.BytesPerRow;
            long newMaxTop = Math.Max(0, totalRows - HexV.FullyVisibleRowCount);

            // Same _initialIndexSeen guard as text mode, evaluated before it's set below - without
            // it, the very first tick's TopLine == 0 and _lastMaxTop == 0 would trivially satisfy
            // ">=" and snap a freshly-opened file straight to its end.
            wasFollowing = _initialIndexSeen && HexV.TopLine >= _lastMaxTop;

            HexV.InvalidateVisual();

            _syncingScroll = true;
            VScrollBar.Maximum = newMaxTop;
            VScrollBar.IsVisible = newMaxTop > 0;
            if (wasFollowing)
            {
                HexV.TopLine = newMaxTop;
                VScrollBar.Value = newMaxTop;
            }
            _syncingScroll = false;

            _lastMaxTop = newMaxTop;
            _lastKnownByteLength = length;
            _initialIndexSeen = true;
        }
        else
        {
            long known = document.Index.KnownLineCount;

            // Nothing changed since the last tick (common once a static file finishes indexing, or
            // between growth bursts on a tailed file) — skip all UI work, not just the redraw. Gate
            // on the actual line count, not maxTop: a file small enough to never need scrolling keeps
            // maxTop at 0 both before and after new lines arrive, which would otherwise mask growth.
            if (known == _lastKnownLineCount && document.Index.IsComplete)
            {
                return;
            }

            // FullyVisibleLineCount, not VisibleLineCount: must match DocView's own MaxTopLine() so
            // the scrollbar's Maximum never lets the user drag past the point where DocView clamps
            // TopLine itself, which would otherwise snap back visually on every such drag.
            long newMaxTop = Math.Max(0, known - DocView.FullyVisibleLineCount);

            // Only apply follow-mode auto-scroll once the initial index build has completed at least
            // once. Without this, the very first tick after opening a file has TopLine == 0 and
            // _lastMaxTop == 0 — trivially "at the bottom" by the >= check — which would snap a
            // freshly-opened file straight to its end instead of leaving it at the top.
            wasFollowing = _initialIndexSeen && DocView.TopLine >= _lastMaxTop;

            DocView.InvalidateVisual();

            _syncingScroll = true;
            VScrollBar.Maximum = newMaxTop;
            VScrollBar.IsVisible = newMaxTop > 0;
            if (wasFollowing)
            {
                DocView.TopLine = newMaxTop;
                VScrollBar.Value = newMaxTop;
            }
            _syncingScroll = false;

            _lastMaxTop = newMaxTop;
            _lastKnownLineCount = known;

            if (document.Index.IsComplete)
            {
                _initialIndexSeen = true;
            }
        }

        UpdateStatusBar(wasFollowing);
    }

    // --- Status bar ---

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

        StatusStateText.Text = document.IsBinary
            ? (isFollowing ? "Following" : "Ready")
            : !document.Index.IsComplete
                ? $"Indexing... {document.Index.KnownLineCount:N0} lines so far"
                : isFollowing ? "Following" : "Ready";
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
            long topOffset = HexV.TopLine * HexView.BytesPerRow;
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
        ((InputElement)(hex ? HexV : DocView)).Focus();

        StatusEncodingText.IsVisible = !hex;
        StatusLineEndingText.IsVisible = !hex;

        // Find, Go To Line, and Cycle Encoding all operate on the line index / text search
        // machinery, which a binary document never builds (see PsvDocument.IsBinary) - disabled
        // rather than silently doing nothing when clicked.
        FindMenuItem.IsEnabled = !hex;
        GoToLineMenuItem.IsEnabled = !hex;
        CycleEncodingMenuItem.IsEnabled = !hex;

        if (hex)
        {
            HScrollBar.IsVisible = false;
        }
        else
        {
            UpdateHScrollBarState();
        }
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
                if (document.IsBinary)
                {
                    HexV.TopLine = long.MaxValue;
                }
                else
                {
                    DocView.TopLine = long.MaxValue;
                }
            }
        }
        else
        {
            document.StopTailing();
        }
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    private async void OnAppearanceClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new AppearanceWindow();
        dialog.LoadFrom(DocView);
        await dialog.ShowDialog(this);
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
        // Hex view rows are always exactly HexView.BytesPerRow bytes wide, so there's no
        // analogous "line longer than the viewport" concept - ApplyViewMode forces the horizontal
        // scrollbar off outright whenever hex mode is active, before this can run.
        if (_document is { IsBinary: true } || DocView.WordWrap)
        {
            HScrollBar.IsVisible = false;
            return;
        }

        int overflow = Math.Max(0, DocView.LastMaxLineLength - DocView.VisibleCharCount);

        _syncingScroll = true;
        HScrollBar.Maximum = overflow;
        HScrollBar.IsVisible = overflow > 0;
        _syncingScroll = false;
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
        await dialog.ShowDialog(this);

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
            _lastKnownLineCount = -1;
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
