using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Terminal.Rendering;

namespace CcDirector.Terminal;

/// <summary>
/// Pure WPF terminal control that renders ANSI terminal output using DrawingVisual.
/// Polls the session buffer via DispatcherTimer and parses VT100 sequences.
/// </summary>
public class TerminalControl : FrameworkElement
{
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int ScrollbackLines = 1000;
    private const double PollIntervalMs = 50;

    // Link detection delegated to LinkDetector (CcDirector.Core) for testability

    // Cached typefaces - avoid creating new Typeface per character (4 variants for normal/bold/italic combinations)
    private static readonly FontFamily _fontFamily = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface _typefaceNormal = new(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface _typefaceBold = new(_fontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface _typefaceItalic = new(_fontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface _typefaceBoldItalic = new(_fontFamily, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

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
                brush.Freeze();
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

    // Link region for hover detection
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

    // Link detection state
    private ContextMenu? _linkContextMenu;
    private string? _detectedLink;
    private LinkDetector.LinkType _detectedLinkType;

    // Paste state
    private bool _isPasting;

    // Renderer mode
    private ITerminalRenderer _renderer = new OriginalRenderer();

    // Path existence cache - avoids disk I/O in OnRender
    private readonly ConcurrentDictionary<string, bool> _pathExistsCache = new();
    private int _pathCacheInvalidateNeeded;

    /// <summary>Raised when scroll position or scrollback changes.</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>Raised when the user requests to view a file from a terminal link.</summary>
    public event Action<string>? ViewFileRequested;

    /// <summary>Raised when the renderer mode changes (so MainWindow can update TerminalArea background).</summary>
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

    /// <summary>Number of visible rows in the viewport.</summary>
    public int ViewportRows => _rows;

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
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    public void Attach(Session session)
    {
        FileLog.Write($"[TerminalControl] Attach: sessionId={session.Id}");

        Detach();
        _session = session;
        _bufferPosition = 0;
        _scrollOffset = 0;
        _scrollback.Clear();
        _pathExistsCache.Clear();

        RecalculateGridSize();
        InitializeCells();

        _parser = new AnsiParser(_cells, _cols, _rows, _scrollback, ScrollbackLines, FileLog.Write);

        // If the control hasn't been laid out yet (ActualWidth/Height are 0),
        // defer buffer parsing and poll timer until OnRenderSizeChanged provides real dimensions.
        // Parsing now would use the default 120x30 grid, causing text to appear at wrong positions.
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            _pendingLayoutAttach = true;
            // Advance buffer position past existing content so poll timer won't re-parse it
            if (session.Buffer != null)
            {
                var (_, pos) = session.Buffer.GetWrittenSince(0);
                _bufferPosition = pos;
            }
            FileLog.Write($"[TerminalControl] Attach deferred: waiting for layout, cols={_cols}, rows={_rows}");
            InvalidateVisual();
            return;
        }

        // Control already has real dimensions - parse buffer immediately (re-attach case)
        if (session.Buffer != null)
        {
            var (initial, pos) = session.Buffer.GetWrittenSince(0);
            _bufferPosition = pos;
            if (initial.Length > 0)
            {
                _parser.Parse(initial);
            }
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        InvalidateVisual();
        FileLog.Write($"[TerminalControl] Attach complete: cols={_cols}, rows={_rows}");
    }

    public void Detach()
    {
        FileLog.Write($"[TerminalControl] Detach: sessionId={_session?.Id}");

        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _parser = null;
        _pendingLayoutAttach = false;
        _linkRegions.Clear();
        _pathExistsCache.Clear();
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

                InvalidateVisual();
                ScrollChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] PollTimer_Tick FAILED: {ex.Message}");
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Always fill the full control area with the renderer background first.
        // This prevents: (a) grey flash during deferred attach when _parser is null
        // and ActualWidth/Height are 0, and (b) edge gaps where grid is smaller
        // than ActualWidth/Height due to integer truncation in RecalculateGridSize.
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            var bgFill = GetCachedBrush(_renderer.GetBackgroundColor());
            drawingContext.DrawRectangle(bgFill, null, new Rect(0, 0, ActualWidth, ActualHeight));
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
                var wpfRect = new Rect(linkX, linkY, linkWidth, _cellHeight);

                var linkType = match.Type == LinkDetector.LinkType.Url ? TerminalLinkType.Url : TerminalLinkType.Path;
                renderLinkRegions.Add(new LinkRegionInfo(termRect, match.Text, linkType));
                _linkRegions.Add(new LinkRegion(wpfRect, match.Text, match.Type));
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
            _renderer.Render(drawingContext, _cells, _cols, _rows, _cellWidth, _cellHeight, ctx);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnRender FAILED ({_renderer.Name}): {ex.Message}");
            var errorBrush = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            errorBrush.Freeze();
            drawingContext.DrawRectangle(errorBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
            var errorText = new FormattedText(
                $"Renderer error ({_renderer.Name}): {ex.Message}",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                12,
                Brushes.Red,
                _dpiScale);
            drawingContext.DrawText(errorText, new Point(10, 10));
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // Re-measure font metrics if DPI was unavailable at construction time
        // (PresentationSource is null in constructor, available once in visual tree).
        // Also handles DPI changes when dragging between monitors.
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            double currentDpi = source.CompositionTarget.TransformToDevice.M11;
            if (Math.Abs(currentDpi - _dpiScale) > 0.001)
            {
                FileLog.Write($"[TerminalControl] DPI updated: {_dpiScale} -> {currentDpi}");
                MeasureFontMetrics();
            }
        }

        int oldCols = _cols;
        int oldRows = _rows;
        RecalculateGridSize();

        if (_cols != oldCols || _rows != oldRows)
        {
            _cells = new TerminalCell[_cols, _rows];
            InitializeCells();
            // Don't copy old cells - ConPTY's resize triggers a full redraw that populates correctly.
            // Copying stale cells causes text doubling when the app's redraw is partial.

            _parser?.UpdateGrid(_cells, _cols, _rows);
            _session?.Resize((short)_cols, (short)_rows);
            InvalidateVisual();
            ScrollChanged?.Invoke(this, EventArgs.Empty);
        }

        // Complete deferred attach now that we have real dimensions
        if (_pendingLayoutAttach)
        {
            _pendingLayoutAttach = false;
            FileLog.Write($"[TerminalControl] Deferred attach completing: cols={_cols}, rows={_rows}");

            // Start poll timer - new content from Claude's redraw will arrive here
            _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        try
        {
            base.OnMouseLeftButtonDown(e);
            Focus();

            var pos = e.GetPosition(this);

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

            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnMouseLeftButtonDown FAILED: {ex.Message}");
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

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
        Cursor = overLink ? Cursors.Hand : Cursors.IBeam;

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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();

            // Keep selection visible for copying
            // _hasSelection stays true if start != end
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        try
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            // Ctrl+C with selection = copy to clipboard (not SIGINT)
            // Ctrl+Shift+C = always copy to clipboard
            if (ctrl && e.Key == Key.C && _hasSelection)
            {
                FileLog.Write($"[TerminalControl] Ctrl+C detected with selection, copying to clipboard");
                CopySelectionToClipboard();
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
                    CopySelectionToClipboard();
                    ClearSelection();
                    InvalidateVisual();
                }
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

            byte[]? data = MapKeyToBytes(e.Key, Keyboard.Modifiers);
            if (data != null)
            {
                _session.SendInput(data);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnPreviewKeyDown FAILED: {ex.Message}");
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        try
        {
            base.OnMouseRightButtonUp(e);

            // Right-click with selection copies to clipboard
            if (_hasSelection)
            {
                FileLog.Write("[TerminalControl] Right-click with selection, copying to clipboard");
                CopySelectionToClipboard();
                ClearSelection();
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // Right-click without selection: always show paste affordance for terminal input.
            if (_session != null)
            {
                var menu = new ContextMenu();
                AddPasteItems(menu, ClipboardContainsTextSafe());
                menu.PlacementTarget = this;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnMouseRightButtonUp FAILED: {ex.Message}");
        }
    }

    private bool ClipboardContainsTextSafe()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] ClipboardContainsTextSafe failed: {ex.Message}");
            return false;
        }
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

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        try
        {
            if (_session == null || string.IsNullOrEmpty(e.Text)) return;

            var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
            _session.SendInput(bytes);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnTextInput FAILED: {ex.Message}");
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        try
        {
            int lines = e.Delta > 0 ? 3 : -3;
            ScrollOffset = _scrollOffset + lines; // Uses property to trigger event

            // Track if user is reviewing history (scrolled up)
            // Reset when user scrolls back to bottom
            _userScrolled = _scrollOffset > 0;

            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OnMouseWheel FAILED: {ex.Message}");
        }
    }

    private void RecalculateGridSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _cellWidth <= 0 || _cellHeight <= 0)
        {
            _cols = DefaultCols;
            _rows = DefaultRows;
            return;
        }

        _cols = Math.Max(10, (int)(ActualWidth / _cellWidth));
        _rows = Math.Max(3, (int)(ActualHeight / _cellHeight));
    }

    private void InitializeCells()
    {
        for (int c = 0; c < _cols; c++)
            for (int r = 0; r < _rows; r++)
                _cells[c, r] = new TerminalCell();
    }

    private void MeasureFontMetrics()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            _dpiScale = source.CompositionTarget.TransformToDevice.M11;
        else
            _dpiScale = 1.0;

        var formatted = new FormattedText(
            "M",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typefaceNormal,
            _fontSize,
            Brushes.White,
            _dpiScale);

        _cellWidth = formatted.WidthIncludingTrailingWhitespace;
        _cellHeight = formatted.Height;
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
                Dispatcher.BeginInvoke(InvalidateVisual);
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
        var sb = new System.Text.StringBuilder();

        for (int row = startRow; row <= endRow; row++)
        {
            int colStart = (row == startRow) ? startCol : 0;
            int colEnd = (row == endRow) ? endCol : _cols - 1;

            var lineBuilder = new System.Text.StringBuilder();

            for (int col = colStart; col <= colEnd; col++)
            {
                TerminalCell cell;

                if (_scrollOffset > 0)
                {
                    // Same logic as OnRender for scrollback
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
    /// Copy the selected text to the clipboard.
    /// </summary>
    private void CopySelectionToClipboard()
    {
        var text = GetSelectedText();
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            FileLog.Write($"[TerminalControl] Copied {text.Length} characters to clipboard");
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

        string text;
        try
        {
            text = Clipboard.GetText();
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
    /// Show context menu for detected link.
    /// </summary>
    private void ShowLinkContextMenu(Point position, string link, LinkDetector.LinkType type)
    {
        _detectedLink = link;
        _detectedLinkType = type;

        _linkContextMenu = new ContextMenu();

        if (type == LinkDetector.LinkType.Path)
        {
            bool addedViewerItem = false;

            if (FileExtensions.IsViewable(link))
            {
                var viewItem = new MenuItem { Header = "View File" };
                viewItem.Click += (_, _) => OpenFileViewer();
                _linkContextMenu.Items.Add(viewItem);
                addedViewerItem = true;
            }

            if (FileExtensions.IsHtml(link))
            {
                var browserItem = new MenuItem { Header = "Open in Browser" };
                browserItem.Click += (_, _) => OpenPathInBrowser();
                _linkContextMenu.Items.Add(browserItem);
                addedViewerItem = true;
            }

            if (addedViewerItem)
            {
                _linkContextMenu.Items.Add(new Separator());
            }

            var copyItem = new MenuItem { Header = "Copy Path" };
            copyItem.Click += (_, _) => CopyLinkToClipboard();
            _linkContextMenu.Items.Add(copyItem);

            var explorerItem = new MenuItem { Header = "Open in Explorer" };
            explorerItem.Click += (_, _) => OpenInExplorer();
            _linkContextMenu.Items.Add(explorerItem);
        }
        else if (type == LinkDetector.LinkType.Url)
        {
            var copyItem = new MenuItem { Header = "Copy URL" };
            copyItem.Click += (_, _) => CopyLinkToClipboard();
            _linkContextMenu.Items.Add(copyItem);

            var browserItem = new MenuItem { Header = "Open in Browser" };
            browserItem.Click += (_, _) => OpenInBrowser();
            _linkContextMenu.Items.Add(browserItem);
        }

        if (_session != null)
        {
            _linkContextMenu.Items.Add(new Separator());
            AddPasteItems(_linkContextMenu);
        }

        _linkContextMenu.PlacementTarget = this;
        _linkContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _linkContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Copy detected link to clipboard.
    /// </summary>
    private void CopyLinkToClipboard()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        string textToCopy = _detectedLinkType == LinkDetector.LinkType.Path
            ? ResolvePath(_detectedLink).Replace('/', '\\').TrimEnd('\\')
            : _detectedLink;

        Clipboard.SetText(textToCopy);
        FileLog.Write($"[TerminalControl] Copied link: {textToCopy}");
    }

    /// <summary>
    /// Open path in Windows Explorer.
    /// </summary>
    private void OpenInExplorer()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        try
        {
            string path = ResolvePath(_detectedLink).Replace('/', '\\').TrimEnd('\\');
            string target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;

            Process.Start("explorer.exe", $"\"{target}\"");
            FileLog.Write($"[TerminalControl] Opened in Explorer: {target}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OpenInExplorer FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open in Explorer:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open URL in default browser.
    /// </summary>
    private void OpenInBrowser()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        try
        {
            var startInfo = new ProcessStartInfo(_detectedLink)
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
            FileLog.Write($"[TerminalControl] Opened in browser: {_detectedLink}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OpenInBrowser FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open in browser:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open a local file path in the default browser (used for .html/.htm).
    /// </summary>
    private void OpenPathInBrowser()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        try
        {
            string path = ResolvePath(_detectedLink).Replace('/', '\\').TrimEnd('\\');
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true
            };
            Process.Start(startInfo);
            FileLog.Write($"[TerminalControl] Opened path in browser: {path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TerminalControl] OpenPathInBrowser FAILED: {ex.Message}");
            MessageBox.Show($"Failed to open in browser:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Open a file in the built-in viewer.
    /// </summary>
    private void OpenFileViewer()
    {
        if (string.IsNullOrEmpty(_detectedLink)) return;

        string path = ResolvePath(_detectedLink);
        FileLog.Write($"[TerminalControl] OpenFileViewer: {path}");
        ViewFileRequested?.Invoke(path);
    }

    /// <summary>
    /// Resolve a detected path to an absolute Windows path.
    /// </summary>
    private string ResolvePath(string path)
    {
        return LinkDetector.ResolvePath(path, _session?.RepoPath);
    }

    private static byte[]? MapKeyToBytes(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

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
