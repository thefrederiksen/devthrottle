using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Terminal.Avalonia.Rendering;
using CcDirector.Terminal.Core;
using CcDirector.Terminal.Core.Rendering;

namespace CcDirector.Terminal.Avalonia;

/// <summary>
/// Avalonia terminal control that renders ANSI terminal output using custom drawing.
/// Polls the session buffer via DispatcherTimer and parses VT100 sequences.
/// Port of CcDirector.Terminal.TerminalControl (WPF).
/// </summary>
public class TerminalControl : Control
{
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int ScrollbackLines = 1000;
    private const double PollIntervalMs = 50;

    /// <summary>
    /// Safety bound on synchronized-output (?2026) repaint holding. While an agent has an
    /// open frame we defer the repaint so it renders atomically (no flicker). A well-behaved
    /// agent closes the frame within a tick or two; if one opens a frame and never closes it,
    /// this many consecutive deferred ticks forces a repaint anyway so the view cannot freeze.
    /// At a 50ms poll that is ~300ms - imperceptible for real frames, safe against a stuck frame.
    /// </summary>
    private const int MaxDeferredSyncTicks = 6;

    // Consecutive PollTimer ticks whose repaint was deferred because a synchronized
    // output frame was still open. Reset to zero whenever a repaint actually happens.
    private int _deferredSyncTicks;

    /// <summary>
    /// How long after a Director-issued PTY resize the terminal-state detector should ignore
    /// byte activity. A resize makes Claude Code repaint its whole screen; that burst is our
    /// doing, not the agent working, so we tell the session to suppress it. Kept well under the
    /// detector's 10s quiet threshold so a genuine work-start that lands inside the window is at
    /// most delayed until the next byte after it.
    /// </summary>
    private static readonly TimeSpan RepaintSuppressionWindow = TimeSpan.FromMilliseconds(1500);

    // Cached typefaces - avoid creating new Typeface per character (4 variants for normal/bold/italic combinations)
    private static readonly FontFamily _fontFamily = new(TerminalFonts.Family);
    private static readonly Typeface _typefaceNormal = new(_fontFamily, FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface _typefaceBold = new(_fontFamily, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface _typefaceItalic = new(_fontFamily, FontStyle.Italic, FontWeight.Normal);
    private static readonly Typeface _typefaceBoldItalic = new(_fontFamily, FontStyle.Italic, FontWeight.Bold);

    // Cached brushes - avoid creating new SolidColorBrush per character
    private static readonly Dictionary<Color, SolidColorBrush> _brushCache = new();
    private static readonly object _brushCacheLock = new();

    private static SolidColorBrush GetCachedBrush(Color color)
    {
        lock (_brushCacheLock)
        {
            if (!_brushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                _brushCache[color] = brush;
            }
            return brush;
        }
    }

    private static Typeface GetCachedTypeface(bool bold, bool italic)
    {
        if (bold && italic) return _typefaceBoldItalic;
        if (bold) return _typefaceBold;
        if (italic) return _typefaceItalic;
        return _typefaceNormal;
    }

    // Link region for hover detection (uses Avalonia Rect)
    private readonly record struct LinkRegion(Rect Bounds, string Text, LinkDetector.LinkType Type);
    private readonly List<LinkRegion> _linkRegions = new();

    private Session? _session;
    private long _bufferPosition;
    private DispatcherTimer? _pollTimer;
    private AnsiParser? _parser;
    private bool _pendingLayoutAttach;

    // Cell grid
    private TerminalCell[,] _cells;
    private int _cols = DefaultCols;
    private int _rows = DefaultRows;

    // Scrollback
    private readonly List<TerminalCell[]> _scrollback = new();
    private int _scrollOffset; // 0 = bottom (current view), >0 = scrolled up
    private bool _userScrolled; // True when user has manually scrolled up (prevents auto-scroll)

    // Selection state
    private bool _isSelecting;
    private (int col, int row) _selectionStart;  // Anchor point (where mouse down occurred)
    private (int col, int row) _selectionEnd;    // Current drag point
    private bool _hasSelection;

    // Pointer capture reference (Avalonia requires storing IPointer for release)
    private IPointer? _capturedPointer;

    // Link detection state
    private ContextMenu? _linkContextMenu;

    // Paste state
    private bool _isPasting;

    // Renderer mode
    private ITerminalRenderer _renderer = new OriginalRenderer();

    // Path existence cache - avoids disk I/O in Render
    private readonly ConcurrentDictionary<string, bool> _pathExistsCache = new();
    private int _pathCacheInvalidateNeeded;

    /// <summary>Raised when scroll position or scrollback changes.</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Raised when the user requests to view a file from a terminal link.</summary>
    public event Action<string>? ViewFileRequested;

    /// <summary>
    /// Raised when an "Open in Browser" action fails (e.g. the remembered browser exe or profile
    /// folder no longer exists). The host shows the message to the user; the terminal never
    /// silently falls back to the system default.
    /// </summary>
    public event Action<string>? BrowserLaunchFailed;

    /// <summary>Raised when the renderer mode changes (so host can update background).</summary>
    public event Action<Color>? RendererBackgroundChanged;

    /// <summary>The currently active renderer.</summary>
    public ITerminalRenderer Renderer => _renderer;

    /// <summary>
    /// Switch to a different terminal renderer.
    /// </summary>
    public void SetRenderer(ITerminalRenderer renderer)
    {
        FileLog.Write($"[TerminalControl] SetRenderer: {renderer.Name}");
        _renderer = renderer;
        _renderer.ApplyControlSettings(this);
        RendererBackgroundChanged?.Invoke(_renderer.GetBackgroundColor());
        InvalidateVisual();
    }

    /// <summary>Number of lines scrolled up from bottom. 0 = current view.</summary>
    public int ScrollOffset
    {
        get => _scrollOffset;
        set
        {
            int clamped = Math.Max(0, Math.Min(_scrollback.Count, value));
            if (_scrollOffset != clamped)
            {
                _scrollOffset = clamped;
                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Total number of lines in scrollback buffer.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>
    /// True while the running application is on the alternate screen buffer.
    /// The alternate screen has no scrollback, so the host hides the local
    /// scrollbar and the wheel is forwarded to the application instead.
    /// </summary>
    public bool IsOnAlternateScreen => _parser?.IsAlternateScreen ?? false;

    /// <summary>Number of visible rows in the viewport.</summary>
    public int ViewportRows => _rows;

    /// <summary>
    /// Atomic snapshot of the three quantities the scrollbar UI needs
    /// (scrollback size, viewport height, current offset) read in a single
    /// expression. Prevents the scrollbar from setting Maximum/ViewportSize/
    /// Value from inconsistent intermediate states when scrollback is
    /// growing concurrently with a redraw.
    /// </summary>
    public ScrollSnapshot GetScrollSnapshot()
        => new(_scrollback.Count, _rows, _scrollOffset);

    /// <summary>Total number of lines (scrollback + current screen) - includes empty rows.</summary>
    public int TotalLineCount => _scrollback.Count + _rows;

    /// <summary>
    /// Count of lines with actual content (non-empty lines).
    /// Used for terminal verification to determine when we have enough content.
    /// </summary>
    public int ContentLineCount
    {
        get
        {
            int count = _scrollback.Count; // All scrollback lines have content

            // Count non-empty rows in current screen buffer
            for (int row = 0; row < _rows; row++)
            {
                bool hasContent = false;
                for (int col = 0; col < _cols; col++)
                {
                    char ch = _cells[col, row].Character;
                    if (ch != '\0' && ch != ' ')
                    {
                        hasContent = true;
                        break;
                    }
                }
                if (hasContent)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Get all terminal text (scrollback + current screen) as a single string.
    /// Used for terminal-to-JSONL verification.
    /// </summary>
    public string GetAllTerminalText()
    {
        var sb = new StringBuilder();

        // First, add all scrollback lines
        foreach (var line in _scrollback)
        {
            var lineBuilder = new StringBuilder();
            for (int col = 0; col < line.Length; col++)
            {
                char ch = line[col].Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(lineBuilder.ToString().TrimEnd());
        }

        // Then add current screen buffer lines
        for (int row = 0; row < _rows; row++)
        {
            var lineBuilder = new StringBuilder();
            for (int col = 0; col < _cols; col++)
            {
                char ch = _cells[col, row].Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }
            sb.AppendLine(lineBuilder.ToString().TrimEnd());
        }

        return sb.ToString();
    }

    // Font metrics
    private double _cellWidth;
    private double _cellHeight;
    private double _fontSize = 14;
    private double _dpiScale = 1.0;

    public TerminalControl()
    {
        _cells = new TerminalCell[DefaultCols, DefaultRows];
        InitializeCells();
        MeasureFontMetrics();

        Focusable = true;
        ClipToBounds = true;

        // Subscribe to size changes
        PropertyChanged += OnPropertyChanged;
    }

    /// <summary>
    /// Handle property changes -- specifically Bounds for size changes.
    /// In Avalonia, Bounds changes when the control is resized.
    /// </summary>
    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
        {
            HandleSizeChanged();
        }
    }

    /// <summary>
    /// Issue a PTY resize that the Director caused (attach/switch, force-refresh, layout change).
    /// Marks the resulting repaint burst as Director-induced first, so the terminal-state detector
    /// does not mistake it for the agent producing output and flip an idle session to "Working".
    /// </summary>
    private void ResizeSession(short cols, short rows)
    {
        if (_session is null) return;
        _session.SuppressActivityFor(RepaintSuppressionWindow);
        _session.Resize(cols, rows);
    }

    public void Attach(Session session)
    {
        FileLog.Write($"[TerminalControl] Attach: sessionId={session.Id}");

        Detach();
        _session = session;
        _bufferPosition = 0;
        _scrollOffset = 0;
        _userScrolled = false;
        _scrollback.Clear();
        _pathExistsCache.Clear();

        RecalculateGridSize();

        // If the control hasn't been laid out yet (Bounds are 0), defer the
        // rebuild until a size change provides real dimensions. Parsing at
        // the default 120x30 grid would put characters in wrong positions
        // and require a full re-replay anyway when the real size arrives.
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _pendingLayoutAttach = true;
            EnsureCellsMatchGrid();
            FileLog.Write($"[TerminalControl] Attach deferred: waiting for layout, cols={_cols}, rows={_rows}");
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        RebuildFromBuffer();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        // Send resize so Claude Code redraws for current terminal dimensions.
        // Without this, if the window was resized while on another session,
        // Claude Code keeps rendering at the old width.
        ResizeSession((short)_cols, (short)_rows);

        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
        FileLog.Write($"[TerminalControl] Attach complete: cols={_cols}, rows={_rows}, scrollback={_scrollback.Count}");
    }

    public void Detach()
    {
        FileLog.Write($"[TerminalControl] Detach: sessionId={_session?.Id}, scrollback={_scrollback.Count}, offset={_scrollOffset}");

        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _parser = null;
        _pendingLayoutAttach = false;
        _linkRegions.Clear();
        _pathExistsCache.Clear();
    }

    /// <summary>
    /// Rebuild the cell grid and scrollback by replaying the entire PTY ring
    /// buffer through a fresh ANSI parser at the current dimensions. The ring
    /// holds the last ~2 MB of raw output, so this gives back scrollback that
    /// survives any dimension change without juggling cached cell grids.
    /// Caller must guarantee Bounds and _session.Buffer are valid.
    /// </summary>
    private void RebuildFromBuffer()
    {
        EnsureCellsMatchGrid();
        _scrollback.Clear();
        _scrollOffset = 0;
        _userScrolled = false;
        _pathExistsCache.Clear();

        _parser = new AnsiParser(_cells, _cols, _rows, _scrollback, ScrollbackLines, FileLog.Write);

        long replayedBytes = 0;
        if (_session?.Buffer != null)
        {
            var (data, newPos) = _session.Buffer.GetWrittenSince(0);
            if (data.Length > 0)
                _parser.Parse(data);
            _bufferPosition = newPos;
            replayedBytes = data.Length;
        }

        FileLog.Write($"[TerminalControl] RebuildFromBuffer: cols={_cols}, rows={_rows}, replayedBytes={replayedBytes}, scrollback={_scrollback.Count}");
    }

    /// <summary>
    /// Ensure the backing cell array's dimensions match _cols/_rows, then
    /// reinitialize every cell to default. Call before allocating a parser
    /// so the parser writes into a correctly-sized grid.
    /// </summary>
    private void EnsureCellsMatchGrid()
    {
        if (_cells.GetLength(0) != _cols || _cells.GetLength(1) != _rows)
            _cells = new TerminalCell[_cols, _rows];

        InitializeCells();
    }

    /// <summary>
    /// Rebuild the terminal display by replaying the session buffer through a
    /// fresh parser, then send SIGWINCH so the running CLI redraws to current
    /// dimensions. User-facing escape hatch when the display or scrollbar
    /// gets into a wedged state.
    /// </summary>
    public void ForceRefresh()
    {
        FileLog.Write($"[TerminalControl] ForceRefresh: sessionId={_session?.Id}");

        if (_session?.Buffer == null)
            return;

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        RebuildFromBuffer();

        ResizeSession((short)_cols, (short)_rows);

        InvalidateVisual();
        ScrollChanged?.Invoke(this, EventArgs.Empty);

        FileLog.Write($"[TerminalControl] ForceRefresh complete: cols={_cols}, rows={_rows}, scrollback={_scrollback.Count}");
    }

    /// <summary>
    /// Dump comprehensive terminal diagnostic data to disk for debugging.
    /// Captures raw PTY bytes, parser state, cell grid, and screenshot.
    /// Triggered by Ctrl+Shift+F12 or the Capture button.
    /// </summary>
    /// <returns>The capture directory path, or null on failure.</returns>
    public string? DumpDiagnosticCapture()
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cc-director", "terminal-captures");
            System.IO.Directory.CreateDirectory(dir);

            var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            int rawByteCount = DumpRawPtyBytes(dir, ts);
            DumpMetadataJson(dir, ts, rawByteCount);
            DumpCellGrid(dir, ts);
            DumpScreenshot(dir, ts);

            FileLog.Write($"[TerminalControl] Diagnostic capture saved to {dir} (ts={ts})");
            return System.IO.Path.Combine(dir, $"capture-{ts}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] DumpDiagnosticCapture FAILED: {ex.Message}");
            return null;
        }
    }

    private int DumpRawPtyBytes(string dir, string ts)
    {
        if (_session?.Buffer == null)
            return 0;

        var rawBytes = _session.Buffer.DumpAll();
        var binPath = System.IO.Path.Combine(dir, $"capture-{ts}.bin");
        System.IO.File.WriteAllBytes(binPath, rawBytes);
        FileLog.Write($"[TerminalControl] Buffer dumped: {rawBytes.Length} bytes -> {binPath}");
        return rawBytes.Length;
    }

    private void DumpMetadataJson(string dir, string ts, int rawByteCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"timestamp\": \"{DateTime.Now:O}\",");
        sb.AppendLine($"  \"rawByteCount\": {rawByteCount},");

        sb.AppendLine("  \"viewport\": {");
        sb.AppendLine($"    \"cols\": {_cols},");
        sb.AppendLine($"    \"rows\": {_rows},");
        sb.AppendLine($"    \"cellWidth\": {_cellWidth.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    \"cellHeight\": {_cellHeight.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    \"fontSize\": {_fontSize.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    \"dpiScale\": {_dpiScale.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    \"boundsWidth\": {Bounds.Width.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"    \"boundsHeight\": {Bounds.Height.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("  },");

        sb.AppendLine("  \"scroll\": {");
        sb.AppendLine($"    \"offset\": {_scrollOffset},");
        sb.AppendLine($"    \"scrollbackCount\": {_scrollback.Count},");
        sb.AppendLine($"    \"totalLineCount\": {TotalLineCount},");
        sb.AppendLine($"    \"contentLineCount\": {ContentLineCount},");
        sb.AppendLine($"    \"userScrolled\": {(_userScrolled ? "true" : "false")}");
        sb.AppendLine("  },");

        sb.AppendLine("  \"selection\": {");
        sb.AppendLine($"    \"hasSelection\": {(_hasSelection ? "true" : "false")},");
        sb.AppendLine($"    \"startCol\": {_selectionStart.col}, \"startRow\": {_selectionStart.row},");
        sb.AppendLine($"    \"endCol\": {_selectionEnd.col}, \"endRow\": {_selectionEnd.row}");
        sb.AppendLine("  },");

        sb.AppendLine($"  \"renderer\": \"{_renderer.Name}\",");

        if (_parser != null)
        {
            var ps = _parser.GetDiagnosticState();
            sb.AppendLine("  \"parser\": {");
            sb.AppendLine($"    \"cursorCol\": {ps.CursorCol},");
            sb.AppendLine($"    \"cursorRow\": {ps.CursorRow},");
            sb.AppendLine($"    \"cursorVisible\": {(ps.CursorVisible ? "true" : "false")},");
            sb.AppendLine($"    \"pendingWrap\": {(ps.PendingWrap ? "true" : "false")},");
            sb.AppendLine($"    \"scrollTop\": {ps.ScrollTop},");
            sb.AppendLine($"    \"scrollBottom\": {ps.ScrollBottom},");
            sb.AppendLine($"    \"fgColor\": \"{ps.FgColor}\",");
            sb.AppendLine($"    \"bgColor\": \"{ps.BgColor}\",");
            sb.AppendLine($"    \"bold\": {(ps.Bold ? "true" : "false")},");
            sb.AppendLine($"    \"italic\": {(ps.Italic ? "true" : "false")},");
            sb.AppendLine($"    \"underline\": {(ps.Underline ? "true" : "false")},");
            sb.AppendLine($"    \"reverse\": {(ps.Reverse ? "true" : "false")},");
            sb.AppendLine($"    \"parserState\": \"{ps.ParserState}\",");
            if (ps.IntermediateChar != null)
                sb.AppendLine($"    \"intermediateChar\": \"{ps.IntermediateChar}\",");
            sb.AppendLine($"    \"csiParams\": [{string.Join(", ", ps.CsiParams)}],");
            sb.AppendLine($"    \"hasCurrentParam\": {(ps.HasCurrentParam ? "true" : "false")},");
            sb.AppendLine($"    \"currentParam\": {ps.CurrentParam},");
            sb.AppendLine($"    \"utf8Needed\": {ps.Utf8Needed},");
            sb.AppendLine($"    \"utf8Len\": {ps.Utf8Len},");
            sb.AppendLine($"    \"hasSavedScreen\": {(ps.HasSavedScreen ? "true" : "false")},");
            sb.AppendLine($"    \"savedCursorCol\": {ps.SavedCursorCol},");
            sb.AppendLine($"    \"savedCursorRow\": {ps.SavedCursorRow},");
            sb.AppendLine($"    \"totalBytesParsed\": {ps.TotalBytesParsed},");
            sb.AppendLine($"    \"gridCols\": {ps.GridCols},");
            sb.AppendLine($"    \"gridRows\": {ps.GridRows},");
            sb.AppendLine($"    \"scrollbackCount\": {ps.ScrollbackCount}");
            sb.AppendLine("  },");
        }

        sb.AppendLine("  \"session\": {");
        sb.AppendLine($"    \"id\": \"{_session?.Id.ToString() ?? "null"}\",");
        sb.AppendLine($"    \"status\": \"{_session?.Status.ToString() ?? "null"}\",");
        sb.AppendLine($"    \"bufferPosition\": {_bufferPosition}");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        var metaPath = System.IO.Path.Combine(dir, $"capture-{ts}.json");
        System.IO.File.WriteAllText(metaPath, sb.ToString());
    }

    private void DumpCellGrid(string dir, string ts)
    {
        var gridSb = new StringBuilder();
        gridSb.AppendLine($"Terminal Cell Grid Capture - {DateTime.Now:O}");
        gridSb.AppendLine($"Grid: {_cols}x{_rows}  Scrollback: {_scrollback.Count}  ScrollOffset: {_scrollOffset}");
        gridSb.AppendLine(new string('=', 80));

        // Dump scrollback lines near viewport if scrolled
        int scrollbackStart = Math.Max(0, _scrollback.Count - _scrollOffset - 5);
        int scrollbackEnd = Math.Min(_scrollback.Count, _scrollback.Count - _scrollOffset + _rows + 5);
        if (scrollbackStart < scrollbackEnd && _scrollOffset > 0)
        {
            gridSb.AppendLine($"--- Scrollback lines [{scrollbackStart}..{scrollbackEnd - 1}] ---");
            for (int i = scrollbackStart; i < scrollbackEnd; i++)
            {
                var line = _scrollback[i];
                gridSb.Append($"SB[{i,4}] ");
                for (int c = 0; c < Math.Min(line.Length, _cols); c++)
                {
                    var cell = line[c];
                    char ch = cell.Character == '\0' ? ' ' : cell.Character;
                    gridSb.Append(ch);
                }
                gridSb.AppendLine();
            }
            gridSb.AppendLine();
        }

        // Dump current screen buffer with color info
        gridSb.AppendLine("--- Current Screen Buffer (with colors) ---");
        gridSb.AppendLine("Format: [row] chars | fg colors | bg colors | attributes");
        gridSb.AppendLine();
        for (int row = 0; row < _rows; row++)
        {
            gridSb.Append($"R[{row,3}] ");
            for (int col = 0; col < _cols; col++)
            {
                char ch = _cells[col, row].Character;
                gridSb.Append(ch == '\0' ? ' ' : ch);
            }
            gridSb.AppendLine();

            if (!RowHasNonDefaultStyling(row))
                continue;

            // Background colors (compact: only show unique spans)
            gridSb.Append("  BG:  ");
            TerminalColor lastBg = default;
            int spanStart = 0;
            for (int col = 0; col <= _cols; col++)
            {
                var bg = col < _cols ? _cells[col, row].Background : default;
                if (col == _cols || (col > 0 && (bg.R != lastBg.R || bg.G != lastBg.G || bg.B != lastBg.B)))
                {
                    if (lastBg.R != 0 || lastBg.G != 0 || lastBg.B != 0)
                        gridSb.Append($"[{spanStart}-{col - 1}:#{lastBg.R:X2}{lastBg.G:X2}{lastBg.B:X2}] ");
                    spanStart = col;
                }
                lastBg = bg;
            }
            gridSb.AppendLine();

            // Foreground colors (compact)
            gridSb.Append("  FG:  ");
            TerminalColor lastFg = default;
            spanStart = 0;
            for (int col = 0; col <= _cols; col++)
            {
                var fg = col < _cols ? _cells[col, row].Foreground : default;
                if (col == _cols || (col > 0 && (fg.R != lastFg.R || fg.G != lastFg.G || fg.B != lastFg.B)))
                {
                    gridSb.Append($"[{spanStart}-{col - 1}:#{lastFg.R:X2}{lastFg.G:X2}{lastFg.B:X2}] ");
                    spanStart = col;
                }
                lastFg = fg;
            }
            gridSb.AppendLine();

            // Attributes (inline string building instead of List allocation)
            bool anyAttrib = false;
            for (int col = 0; col < _cols; col++)
            {
                var cell = _cells[col, row];
                if (cell.Bold || cell.Italic || cell.Underline)
                {
                    if (!anyAttrib) { gridSb.Append("  ATTR:"); anyAttrib = true; }
                    gridSb.Append($" [{col}:");
                    if (cell.Bold) gridSb.Append('B');
                    if (cell.Italic) gridSb.Append('I');
                    if (cell.Underline) gridSb.Append('U');
                    gridSb.Append(']');
                }
            }
            if (anyAttrib) gridSb.AppendLine();
        }

        var gridPath = System.IO.Path.Combine(dir, $"capture-{ts}.txt");
        System.IO.File.WriteAllText(gridPath, gridSb.ToString());
    }

    private bool RowHasNonDefaultStyling(int row)
    {
        for (int col = 0; col < _cols; col++)
        {
            var cell = _cells[col, row];
            if (cell.Background.R != 0 || cell.Background.G != 0 || cell.Background.B != 0 ||
                cell.Foreground.R != 204 || cell.Foreground.G != 204 || cell.Foreground.B != 204 ||
                cell.Bold || cell.Italic || cell.Underline)
                return true;
        }
        return false;
    }

    private void DumpScreenshot(string dir, string ts)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var pngPath = System.IO.Path.Combine(dir, $"capture-{ts}.png");
        var pixelSize = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
        var rtb = new global::Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize);
        rtb.Render(this);
        rtb.Save(pngPath);
        FileLog.Write($"[TerminalControl] Screenshot: {pngPath}");
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_session?.Buffer == null) return;

            var (data, newPos) = _session.Buffer.GetWrittenSince(_bufferPosition);
            if (data.Length > 0)
            {
                _bufferPosition = newPos;
                _parser?.Parse(data);

                // Clear path cache so links re-evaluate with new terminal content
                _pathExistsCache.Clear();
                Interlocked.Exchange(ref _pathCacheInvalidateNeeded, 0);

                // Let selection persist during output so users can
                // select text while Claude is generating

                // Only auto-scroll if user hasn't manually scrolled up
                // This lets users review history while output continues
                if (!_userScrolled && _scrollOffset > 0)
                    _scrollOffset = 0;

                // Synchronized output (?2026): if the agent is mid-frame, hold the repaint
                // so we never paint a half-drawn frame (that mid-frame paint is what makes
                // Grok flicker). Paint once the frame closes - or after a bounded number of
                // deferred ticks, so a frame that never closes cannot freeze the view.
                bool frameOpen = _parser?.InSynchronizedUpdate == true;
                if (frameOpen && _deferredSyncTicks < MaxDeferredSyncTicks)
                {
                    _deferredSyncTicks++;
                }
                else
                {
                    _deferredSyncTicks = 0;
                    InvalidateVisual();
                    ScrollChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] PollTimer_Tick FAILED: {ex.Message}");
        }
    }

    public override void Render(DrawingContext context)
    {
        // Always fill the full control area with the renderer background first.
        // This prevents: (a) grey flash during deferred attach when _parser is null
        // and Bounds are 0, and (b) edge gaps where grid is smaller
        // than Bounds due to integer truncation in RecalculateGridSize.
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            var bgFill = GetCachedBrush(_renderer.GetBackgroundColor());
            context.DrawRectangle(bgFill, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
        }

        // Reset so background path checks can schedule one more InvalidateVisual
        Interlocked.Exchange(ref _pathCacheInvalidateNeeded, 0);

        // Clear link regions for fresh hit-testing
        _linkRegions.Clear();

        if (_parser == null)
            return;  // Background already drawn above

        // Build link regions for this frame
        var renderLinkRegions = new List<LinkRegionInfo>();
        for (int row = 0; row < _rows; row++)
        {
            string lineText = GetLineText(row);
            var linkMatches = FindAllLinkMatches(lineText);

            foreach (var match in linkMatches)
            {
                double linkX = match.StartCol * _cellWidth;
                double linkY = row * _cellHeight;
                double linkWidth = (match.EndCol - match.StartCol) * _cellWidth;
                var termRect = new TerminalRect(linkX, linkY, linkWidth, _cellHeight);
                var avaloniaRect = new Rect(linkX, linkY, linkWidth, _cellHeight);

                var linkType = match.Type == LinkDetector.LinkType.Url ? TerminalLinkType.Url : TerminalLinkType.Path;
                renderLinkRegions.Add(new LinkRegionInfo(termRect, match.Text, linkType));
                _linkRegions.Add(new LinkRegion(avaloniaRect, match.Text, match.Type));
            }
        }

        // Build selection info
        int selStartCol = 0, selStartRow = 0, selEndCol = 0, selEndRow = 0;
        if (_hasSelection)
            (selStartCol, selStartRow, selEndCol, selEndRow) = NormalizeSelection();

        // Build cursor info
        bool cursorVisible = _parser.IsCursorVisible;
        var (curCol, curRow) = _parser.GetCursorPosition();

        var ctx = new RenderContext(
            _scrollback, _scrollOffset,
            _hasSelection, selStartCol, selStartRow, selEndCol, selEndRow,
            cursorVisible, curCol, curRow,
            renderLinkRegions,
            _dpiScale, _fontSize,
            _session?.RepoPath);

        try
        {
            _renderer.Render(context, _cells, _cols, _rows, _cellWidth, _cellHeight, ctx);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] Render FAILED ({_renderer.Name}): {ex.Message}");
            var errorBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            context.DrawRectangle(errorBrush, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
            var errorText = new FormattedText(
                $"Renderer error ({_renderer.Name}): {ex.Message}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _typefaceNormal,
                12,
                Brushes.Red);
            context.DrawText(errorText, new Point(10, 10));
        }
    }

    /// <summary>
    /// Handle control size changes - recalculate grid, copy cells, complete deferred attach.
    /// </summary>
    private void HandleSizeChanged()
    {
        // Don't resize when control is hidden (Bounds=0) - this would switch
        // to default 120x30, resize ConPTY to wrong dimensions, and cause
        // content misalignment when the control becomes visible again.
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        int oldCols = _cols;
        int oldRows = _rows;
        RecalculateGridSize();

        // Complete deferred attach: bounds just became valid, so build the
        // grid fresh from the PTY buffer at the real dimensions.
        if (_pendingLayoutAttach)
        {
            _pendingLayoutAttach = false;
            FileLog.Write($"[TerminalControl] Deferred attach completing: cols={_cols}, rows={_rows}");

            RebuildFromBuffer();

            _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            ResizeSession((short)_cols, (short)_rows);

            InvalidateVisual();
            // Fire after Bounds (and therefore ViewportRows) have a real value
            // so the host scrollbar reads correct viewport size on the first
            // update -- previously this happened during Attach when Bounds=0
            // and the thumb came out invisible until the next manual resize.
            ScrollChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_cols != oldCols || _rows != oldRows)
        {
            var oldCells = _cells;
            _cells = new TerminalCell[_cols, _rows];
            InitializeCells();

            int copyC = Math.Min(oldCols, _cols);
            int copyR = Math.Min(oldRows, _rows);
            for (int r = 0; r < copyR; r++)
                for (int c = 0; c < copyC; c++)
                    _cells[c, r] = oldCells[c, r];

            _parser?.UpdateGrid(_cells, _cols, _rows);
            ResizeSession((short)_cols, (short)_rows);
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        try
        {
            base.OnPointerPressed(e);
            Focus();

            var point = e.GetCurrentPoint(this);
            var pos = point.Position;

            if (point.Properties.IsLeftButtonPressed)
            {
                // Check if clicking on a link - show context menu
                foreach (var region in _linkRegions)
                {
                    if (region.Bounds.Contains(pos))
                    {
                        ShowLinkContextMenu(pos, region.Text, region.Type);
                        e.Handled = true;
                        return;
                    }
                }

                // Not clicking on a link - start selection
                var cell = HitTestCell(pos);

                _selectionStart = cell;
                _selectionEnd = cell;
                _isSelecting = true;
                _hasSelection = false;

                // Capture pointer for drag tracking
                _capturedPointer = e.Pointer;
                e.Pointer.Capture(this);

                InvalidateVisual();
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnPointerPressed FAILED: {ex.Message}");
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var pos = e.GetPosition(this);

        // Update cursor based on whether hovering over a link
        bool overLink = false;
        foreach (var region in _linkRegions)
        {
            if (region.Bounds.Contains(pos))
            {
                overLink = true;
                break;
            }
        }
        Cursor = overLink ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Ibeam);

        // Handle selection dragging
        if (!_isSelecting) return;

        var cell = HitTestCell(pos);

        if (cell != _selectionEnd)
        {
            _selectionEnd = cell;
            _hasSelection = (_selectionStart != _selectionEnd);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        try
        {
            base.OnPointerReleased(e);

            var point = e.GetCurrentPoint(this);

            // Left button released - finish selection
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                if (_isSelecting)
                {
                    _isSelecting = false;
                    _capturedPointer?.Capture(null);
                    _capturedPointer = null;

                    // Keep selection visible for copying
                    // _hasSelection stays true if start != end
                }
                e.Handled = true;
            }
            // Right button released - copy or paste context menu
            else if (e.InitialPressMouseButton == MouseButton.Right)
            {
                HandleRightClick(e);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnPointerReleased FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle right-click: copy selection or show paste context menu.
    /// </summary>
    private async void HandleRightClick(PointerReleasedEventArgs e)
    {
        try
        {
            // Right-click with selection copies to clipboard
            if (_hasSelection)
            {
                FileLog.Write("[TerminalControl] Right-click with selection, copying to clipboard");
                await CopySelectionToClipboardAsync();
                ClearSelection();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Right-click without selection: always show paste affordance for terminal input.
            if (_session != null)
            {
                var menu = await BuildPasteContextMenuAsync();
                this.ContextMenu = menu;
                menu.Open(this);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] HandleRightClick FAILED: {ex.Message}");
        }
    }

    private async Task<ContextMenu> BuildPasteContextMenuAsync()
    {
        string? clipText = null;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                clipText = await clipboard.GetTextAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] BuildPasteContextMenuAsync: clipboard read failed: {ex.Message}");
        }

        var menu = new ContextMenu();
        AddPasteItems(menu, !string.IsNullOrEmpty(clipText));
        return menu;
    }

    private void AddPasteItems(ContextMenu menu, bool hasClipboardText = true)
    {
        var pasteItem = new MenuItem
        {
            Header = "Paste to Terminal",
            IsEnabled = _session != null && !_isPasting && hasClipboardText,
        };
        pasteItem.Click += (_, _) => _ = PasteToTerminalAsync();
        menu.Items.Add(pasteItem);

        if (_isPasting)
        {
            var cancelItem = new MenuItem { Header = "Cancel Paste" };
            cancelItem.Click += (_, _) =>
            {
                FileLog.Write("[TerminalControl] Paste cancelled by user");
                _isPasting = false;
            };
            menu.Items.Add(cancelItem);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        try
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

            // Ctrl+C with selection = copy to clipboard (not SIGINT)
            // Ctrl+Shift+C = always copy to clipboard
            if (ctrl && e.Key == Key.C && _hasSelection)
            {
                FileLog.Write($"[TerminalControl] Ctrl+C detected with selection, copying to clipboard");
                _ = CopySelectionToClipboardAsync();
                ClearSelection();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (ctrl && shift && e.Key == Key.C)
            {
                FileLog.Write($"[TerminalControl] Ctrl+Shift+C detected, hasSelection={_hasSelection}");
                if (_hasSelection)
                {
                    _ = CopySelectionToClipboardAsync();
                    ClearSelection();
                    InvalidateVisual();
                }
                e.Handled = true;
                return;
            }

            // Ctrl+Shift+F12 = dump raw terminal buffer to file for debugging
            if (ctrl && shift && e.Key == Key.F12)
            {
                DumpDiagnosticCapture();
                e.Handled = true;
                return;
            }

            // Ctrl+V / Ctrl+Shift+V = paste clipboard as slow keystrokes
            if (ctrl && e.Key == Key.V)
            {
                FileLog.Write($"[TerminalControl] {(shift ? "Ctrl+Shift+V" : "Ctrl+V")} detected, pasting to terminal");
                _ = PasteToTerminalAsync();
                e.Handled = true;
                return;
            }

            if (_session == null) return;

            byte[]? data = MapKeyToBytes(e.Key, e.KeyModifiers);
            if (data != null)
            {
                _session.SendInput(data);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnKeyDown FAILED: {ex.Message}");
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        try
        {
            if (_session == null || string.IsNullOrEmpty(e.Text)) return;

            var bytes = Encoding.UTF8.GetBytes(e.Text);
            _session.SendInput(bytes);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnTextInput FAILED: {ex.Message}");
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        try
        {
            // When a full-screen application is on the alternate screen and has
            // requested mouse reporting (e.g. Claude Code), the local scrollback
            // is empty by design -- the application scrolls its own view. Forward
            // the wheel to it as mouse-wheel reports instead of scrolling local
            // history, which would do nothing and leave the user stuck.
            if (_session != null && _parser != null
                && _parser.IsAlternateScreen && _parser.MouseReportingEnabled)
            {
                ForwardWheelToApplication(e);
                e.Handled = true;
                return;
            }

            int lines = e.Delta.Y > 0 ? 3 : -3;
            ScrollOffset = _scrollOffset + lines; // Uses property to trigger event

            // Track if user is reviewing history (scrolled up)
            // Reset when user scrolls back to bottom
            _userScrolled = _scrollOffset > 0;

            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnPointerWheelChanged FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Forward a wheel event to the running application as mouse-wheel reports.
    /// Used when the application is on the alternate screen with mouse reporting
    /// enabled, so it scrolls its own view (the local scrollback is empty then).
    /// </summary>
    private void ForwardWheelToApplication(PointerWheelEventArgs e)
    {
        if (_session == null || _parser == null) return;

        var pos = e.GetPosition(this);
        int col = _cellWidth > 0 ? (int)(pos.X / _cellWidth) + 1 : 1;
        int row = _cellHeight > 0 ? (int)(pos.Y / _cellHeight) + 1 : 1;
        col = Math.Max(1, Math.Min(_cols, col));
        row = Math.Max(1, Math.Min(_rows, row));

        int button = e.Delta.Y > 0 ? MouseReportEncoder.WheelUp : MouseReportEncoder.WheelDown;

        // Send one wheel report per notch. Delta.Y is ~1 per standard wheel
        // notch; precise trackpads report fractional/larger values, so round up
        // and cap to keep a single gesture from flooding the application.
        int notches = (int)Math.Ceiling(Math.Abs(e.Delta.Y));
        notches = Math.Max(1, Math.Min(notches, 10));

        byte[] report = _parser.MouseSgrCoordinates
            ? MouseReportEncoder.EncodeSgr(button, col, row)
            : MouseReportEncoder.EncodeX10(button, col, row);

        for (int i = 0; i < notches; i++)
            _session.SendInput(report);

        FileLog.Write($"[TerminalControl] ForwardWheelToApplication: button={button}, col={col}, row={row}, notches={notches}, sgr={_parser.MouseSgrCoordinates}");
    }

    private void RecalculateGridSize()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _cellWidth <= 0 || _cellHeight <= 0)
        {
            _cols = DefaultCols;
            _rows = DefaultRows;
            return;
        }

        _cols = Math.Max(10, (int)(Bounds.Width / _cellWidth));
        _rows = Math.Max(3, (int)(Bounds.Height / _cellHeight));
    }

    private void InitializeCells()
    {
        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
                _cells[c, r] = new TerminalCell();
    }

    private void MeasureFontMetrics()
    {
        // In Avalonia, get DPI from VisualRoot if available
        if (this.VisualRoot is TopLevel topLevel)
            _dpiScale = topLevel.RenderScaling;
        else
            _dpiScale = 1.0;

        var formatted = new FormattedText(
            "M",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typefaceNormal,
            _fontSize,
            Brushes.White);

        _cellWidth = Math.Ceiling(formatted.Width);
        _cellHeight = Math.Ceiling(formatted.Height);

        // Fallback if metrics are zero (control not yet attached to visual tree)
        if (_cellWidth <= 0) _cellWidth = 8.4;
        if (_cellHeight <= 0) _cellHeight = 18.4;
    }

    /// <summary>
    /// Convert screen coordinates to cell (col, row).
    /// Accounts for scroll offset to give virtual row coordinates.
    /// </summary>
    private (int col, int row) HitTestCell(Point position)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
            return (0, 0);

        int col = (int)(position.X / _cellWidth);
        int row = (int)(position.Y / _cellHeight);

        // Clamp to valid range
        col = Math.Max(0, Math.Min(_cols - 1, col));
        row = Math.Max(0, Math.Min(_rows - 1, row));

        return (col, row);
    }

    /// <summary>
    /// Get a cell at the specified position, accounting for scrollback.
    /// </summary>
    private TerminalCell GetCellAt(int col, int row)
    {
        if (_scrollOffset > 0)
        {
            int virtualIndex = _scrollback.Count - _scrollOffset + row;

            if (virtualIndex < 0)
            {
                return default;
            }
            else if (virtualIndex < _scrollback.Count)
            {
                var line = _scrollback[virtualIndex];
                return col < line.Length ? line[col] : default;
            }
            else
            {
                int screenRow = virtualIndex - _scrollback.Count;
                return (screenRow >= 0 && screenRow < _rows)
                    ? _cells[col, screenRow]
                    : default;
            }
        }
        else
        {
            return (col >= 0 && col < _cols && row >= 0 && row < _rows)
                ? _cells[col, row]
                : default;
        }
    }

    /// <summary>
    /// Get the full text of a row.
    /// </summary>
    private string GetLineText(int row)
    {
        var sb = new StringBuilder();
        for (int col = 0; col < _cols; col++)
        {
            TerminalCell cell = GetCellAt(col, row);
            sb.Append(cell.Character == '\0' ? ' ' : cell.Character);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Find all link matches (paths and URLs) in a line of text.
    /// Delegates to LinkDetector with cache-backed path existence checking.
    /// </summary>
    private List<LinkDetector.LinkMatch> FindAllLinkMatches(string lineText)
    {
        return LinkDetector.FindAllLinkMatches(lineText, _session?.RepoPath, PathExistsCheckForRender);
    }

    /// <summary>
    /// Path existence check for render context: uses cache, schedules background check on miss.
    /// </summary>
    private bool PathExistsCheckForRender(string fullPath)
    {
        if (_pathExistsCache.TryGetValue(fullPath, out bool exists))
            return exists;

        // Cache miss - schedule background check, return false for now (don't block render)
        var capturedPath = fullPath;
        _ = Task.Run(() =>
        {
            bool found = File.Exists(capturedPath) || Directory.Exists(capturedPath);
            _pathExistsCache[capturedPath] = found;
            if (found && Interlocked.CompareExchange(ref _pathCacheInvalidateNeeded, 1, 0) == 0)
                Dispatcher.UIThread.Post(InvalidateVisual);
        });
        return false;
    }

    /// <summary>
    /// Path existence check for click context: uses cache, falls back to synchronous check.
    /// </summary>
    private bool PathExistsCheckForClick(string fullPath)
    {
        if (_pathExistsCache.TryGetValue(fullPath, out bool exists))
            return exists;

        bool found = File.Exists(fullPath) || Directory.Exists(fullPath);
        _pathExistsCache[fullPath] = found;
        return found;
    }

    /// <summary>
    /// Detect if there's a path or URL at the specified cell position.
    /// </summary>
    private (string? text, LinkDetector.LinkType type) DetectLinkAtCell(int col, int row)
    {
        string lineText = GetLineText(row);
        return LinkDetector.DetectLinkAtPosition(lineText, col, _session?.RepoPath, PathExistsCheckForClick);
    }

    /// <summary>
    /// Normalize selection so start is before end (reading order).
    /// </summary>
    private (int startCol, int startRow, int endCol, int endRow) NormalizeSelection()
    {
        int startRow = _selectionStart.row;
        int startCol = _selectionStart.col;
        int endRow = _selectionEnd.row;
        int endCol = _selectionEnd.col;

        // Swap if end is before start
        if (endRow < startRow || (endRow == startRow && endCol < startCol))
        {
            (startRow, endRow) = (endRow, startRow);
            (startCol, endCol) = (endCol, startCol);
        }

        return (startCol, startRow, endCol, endRow);
    }

    /// <summary>
    /// Get the selected text from the terminal buffer.
    /// </summary>
    private string GetSelectedText()
    {
        if (!_hasSelection) return string.Empty;

        var (startCol, startRow, endCol, endRow) = NormalizeSelection();
        var sb = new StringBuilder();

        for (int row = startRow; row <= endRow; row++)
        {
            int colStart = (row == startRow) ? startCol : 0;
            int colEnd = (row == endRow) ? endCol : _cols - 1;

            var lineBuilder = new StringBuilder();

            for (int col = colStart; col <= colEnd; col++)
            {
                TerminalCell cell;

                if (_scrollOffset > 0)
                {
                    // Same logic as Render for scrollback
                    int virtualIndex = _scrollback.Count - _scrollOffset + row;

                    if (virtualIndex < 0)
                    {
                        cell = default;
                    }
                    else if (virtualIndex < _scrollback.Count)
                    {
                        var line = _scrollback[virtualIndex];
                        cell = col < line.Length ? line[col] : default;
                    }
                    else
                    {
                        int screenRow = virtualIndex - _scrollback.Count;
                        cell = (screenRow >= 0 && screenRow < _rows)
                            ? _cells[col, screenRow]
                            : default;
                    }
                }
                else
                {
                    cell = _cells[col, row];
                }

                char ch = cell.Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }

            // Trim trailing whitespace from each line
            string lineText = lineBuilder.ToString().TrimEnd();
            sb.Append(lineText);

            if (row < endRow)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Copy the selected text to the clipboard (async for Avalonia).
    /// </summary>
    private async Task CopySelectionToClipboardAsync()
    {
        var text = GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                FileLog.Write($"[TerminalControl] Copied {text.Length} characters to clipboard");
            }
        }
    }

    /// <summary>
    /// Clear the current selection.
    /// </summary>
    private void ClearSelection()
    {
        _hasSelection = false;
        _isSelecting = false;
    }

    /// <summary>
    /// Paste clipboard text to the terminal as slow keystrokes.
    /// Sends each character individually with a small delay so the terminal
    /// can process them (required for interactive prompts like claude /login).
    /// </summary>
    private async Task PasteToTerminalAsync()
    {
        if (_session == null || _isPasting) return;

        string? text;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            text = await clipboard.GetTextAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] PasteToTerminalAsync: clipboard read failed: {ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        // Strip trailing newlines - user can press Enter themselves
        text = text.TrimEnd('\r', '\n');

        FileLog.Write($"[TerminalControl] PasteToTerminalAsync: pasting {text.Length} chars");
        _isPasting = true;

        try
        {
            foreach (var ch in text)
            {
                if (!_isPasting) break; // Cancelled

                var bytes = Encoding.UTF8.GetBytes(new[] { ch });
                _session.SendInput(bytes);
                await Task.Delay(15);
            }

            FileLog.Write($"[TerminalControl] PasteToTerminalAsync: complete ({text.Length} chars sent)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] PasteToTerminalAsync FAILED: {ex.Message}");
        }
        finally
        {
            _isPasting = false;
        }
    }

    /// <summary>
    /// Show the context menu for a detected link. The link items (View File / Copy / Open in File
    /// Manager / Open in Browser) come from the shared <see cref="LinkContextMenuBuilder"/> so the
    /// terminal and the History tab offer identical actions; the terminal then appends its own paste
    /// items.
    /// </summary>
    private void ShowLinkContextMenu(Point position, string link, LinkDetector.LinkType type)
    {
        _linkContextMenu = new ContextMenu();

        LinkContextMenuBuilder.PopulateLinkItems(_linkContextMenu, new LinkMenuContext
        {
            Link = link,
            Type = type,
            RepoPath = _session?.RepoPath,
            Owner = this,
            OnViewFile = path => ViewFileRequested?.Invoke(path),
            OnBrowserError = message => BrowserLaunchFailed?.Invoke(message),
        });

        if (_session != null)
        {
            _linkContextMenu.Items.Add(new Separator());
            AddPasteItems(_linkContextMenu);
        }

        this.ContextMenu = _linkContextMenu;
        _linkContextMenu.Open(this);
    }

    private static byte[]? MapKeyToBytes(Key key, KeyModifiers modifiers)
    {
        bool ctrl = (modifiers & KeyModifiers.Control) != 0;
        bool shift = (modifiers & KeyModifiers.Shift) != 0;

        // Ctrl+C
        if (ctrl && key == Key.C) return new byte[] { 0x03 };
        // Ctrl+D
        if (ctrl && key == Key.D) return new byte[] { 0x04 };
        // Ctrl+Z
        if (ctrl && key == Key.Z) return new byte[] { 0x1A };
        // Ctrl+L
        if (ctrl && key == Key.L) return new byte[] { 0x0C };

        // Shift+Tab (backtab) - used by Claude Code for mode cycling
        if (shift && key == Key.Tab) return "\x1b[Z"u8.ToArray();

        return key switch
        {
            Key.Enter => "\r"u8.ToArray(),
            Key.Back => new byte[] { 0x7F },
            Key.Tab => "\t"u8.ToArray(),
            Key.Escape => new byte[] { 0x1B },
            Key.Up => "\x1b[A"u8.ToArray(),
            Key.Down => "\x1b[B"u8.ToArray(),
            Key.Right => "\x1b[C"u8.ToArray(),
            Key.Left => "\x1b[D"u8.ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            Key.PageUp => "\x1b[5~"u8.ToArray(),
            Key.PageDown => "\x1b[6~"u8.ToArray(),
            Key.Insert => "\x1b[2~"u8.ToArray(),
            Key.F1 => "\x1bOP"u8.ToArray(),
            Key.F2 => "\x1bOQ"u8.ToArray(),
            Key.F3 => "\x1bOR"u8.ToArray(),
            Key.F4 => "\x1bOS"u8.ToArray(),
            Key.F5 => "\x1b[15~"u8.ToArray(),
            Key.F6 => "\x1b[17~"u8.ToArray(),
            Key.F7 => "\x1b[18~"u8.ToArray(),
            Key.F8 => "\x1b[19~"u8.ToArray(),
            Key.F9 => "\x1b[20~"u8.ToArray(),
            Key.F10 => "\x1b[21~"u8.ToArray(),
            Key.F11 => "\x1b[23~"u8.ToArray(),
            Key.F12 => "\x1b[24~"u8.ToArray(),
            _ => null
        };
    }
}
