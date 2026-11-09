using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CcDirector.Terminal.Rendering;

namespace CcDirector.Terminal;

/// <summary>
/// Standalone WPF terminal control. Renders ANSI terminal output using DrawingVisual.
/// Feed raw bytes via Feed(), receive keyboard input via InputReceived event.
/// No external dependencies (no Session, no FileLog, no LinkDetector).
/// </summary>
public class TerminalView : FrameworkElement
{
    private const int DefaultCols = 120;
    private const int DefaultRows = 30;
    private const int ScrollbackLines = 1000;

    private static readonly FontFamily _fontFamily = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Typeface _typefaceNormal = new(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

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

    private AnsiParser? _parser;

    // Cell grid
    private TerminalCell[,] _cells;
    private int _cols = DefaultCols;
    private int _rows = DefaultRows;

    // Scrollback
    private readonly List<TerminalCell[]> _scrollback = new();
    private int _scrollOffset;
    private bool _userScrolled;

    // Selection state
    private bool _isSelecting;
    private (int col, int row) _selectionStart;
    private (int col, int row) _selectionEnd;
    private bool _hasSelection;

    // Renderer mode
    private ITerminalRenderer _renderer = new ProRenderer();

    // Font metrics
    private double _cellWidth;
    private double _cellHeight;
    private double _fontSize = 14;
    private double _dpiScale = 1.0;

    /// <summary>Raised when keyboard input should be sent to the process.</summary>
    public event Action<byte[]>? InputReceived;

    /// <summary>Raised when the terminal grid size changes (cols, rows).</summary>
    public event Action<int, int>? TerminalSizeChanged;

    /// <summary>Raised when the renderer mode changes.</summary>
    public event Action<Color>? RendererBackgroundChanged;

    /// <summary>Number of visible columns.</summary>
    public int Cols => _cols;

    /// <summary>Number of visible rows.</summary>
    public int Rows => _rows;

    /// <summary>The currently active renderer.</summary>
    public ITerminalRenderer Renderer => _renderer;

    /// <summary>
    /// Switch to a different terminal renderer.
    /// </summary>
    public void SetRenderer(ITerminalRenderer renderer)
    {
        _renderer = renderer;
        _renderer.ApplyControlSettings(this);
        RendererBackgroundChanged?.Invoke(_renderer.GetBackgroundColor());
        InvalidateVisual();
    }

    /// <summary>Number of lines scrolled up from bottom.</summary>
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
            }
        }
    }

    public TerminalView()
    {
        _cells = new TerminalCell[DefaultCols, DefaultRows];
        InitializeCells();
        MeasureFontMetrics();

        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;

        _parser = new AnsiParser(_cells, _cols, _rows, _scrollback, ScrollbackLines);
    }

    /// <summary>
    /// Feed raw bytes from a process into the terminal parser.
    /// Call this from the UI thread.
    /// </summary>
    public void Feed(byte[] data)
    {
        _parser?.Parse(data);

        if (!_userScrolled && _scrollOffset > 0)
            _scrollOffset = 0;

        InvalidateVisual();
    }

    /// <summary>
    /// Capture the terminal rendering to a PNG file.
    /// </summary>
    public void CaptureScreenshot(string outputPath)
    {
        var dpi = 96 * _dpiScale;
        var width = (int)(ActualWidth > 0 ? ActualWidth : _cols * _cellWidth);
        var height = (int)(ActualHeight > 0 ? ActualHeight : _rows * _cellHeight);

        if (width <= 0 || height <= 0) return;

        var rtb = new RenderTargetBitmap(
            (int)(width * _dpiScale),
            (int)(height * _dpiScale),
            dpi, dpi, PixelFormats.Pbgra32);

        rtb.Render(this);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));

        using var stream = System.IO.File.Create(outputPath);
        encoder.Save(stream);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // Always fill the full control area with the renderer background first.
        // Prevents grey flash during deferred attach and edge gaps from grid truncation.
        if (ActualWidth > 0 && ActualHeight > 0)
        {
            var bgFill = GetCachedBrush(_renderer.GetBackgroundColor());
            drawingContext.DrawRectangle(bgFill, null, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        if (_parser == null)
            return;  // Background already drawn above

        // No link regions in standalone mode
        var emptyLinks = new List<LinkRegionInfo>();

        int selStartCol = 0, selStartRow = 0, selEndCol = 0, selEndRow = 0;
        if (_hasSelection)
            (selStartCol, selStartRow, selEndCol, selEndRow) = NormalizeSelection();

        bool cursorVisible = _parser.IsCursorVisible;
        var (curCol, curRow) = _parser.GetCursorPosition();

        var ctx = new RenderContext(
            _scrollback, _scrollOffset,
            _hasSelection, selStartCol, selStartRow, selEndCol, selEndRow,
            cursorVisible, curCol, curRow,
            emptyLinks,
            _dpiScale, _fontSize,
            null);

        _renderer.Render(drawingContext, _cells, _cols, _rows, _cellWidth, _cellHeight, ctx);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        int oldCols = _cols;
        int oldRows = _rows;
        RecalculateGridSize();

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
            InvalidateVisual();
            TerminalSizeChanged?.Invoke(_cols, _rows);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var cell = HitTestCell(e.GetPosition(this));
        _selectionStart = cell;
        _selectionEnd = cell;
        _isSelecting = true;
        _hasSelection = false;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Cursors.IBeam;

        if (!_isSelecting) return;

        var cell = HitTestCell(e.GetPosition(this));
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
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.C && _hasSelection)
        {
            CopySelectionToClipboard();
            ClearSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.C)
        {
            if (_hasSelection)
            {
                CopySelectionToClipboard();
                ClearSelection();
                InvalidateVisual();
            }
            e.Handled = true;
            return;
        }

        byte[]? data = MapKeyToBytes(e.Key, Keyboard.Modifiers);
        if (data != null)
        {
            InputReceived?.Invoke(data);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        var bytes = Encoding.UTF8.GetBytes(e.Text);
        InputReceived?.Invoke(bytes);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int lines = e.Delta > 0 ? 3 : -3;
        ScrollOffset = _scrollOffset + lines;
        _userScrolled = _scrollOffset > 0;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_hasSelection)
        {
            CopySelectionToClipboard();
            ClearSelection();
            InvalidateVisual();
            e.Handled = true;
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
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typefaceNormal,
            _fontSize,
            Brushes.White,
            _dpiScale);

        _cellWidth = formatted.WidthIncludingTrailingWhitespace;
        _cellHeight = formatted.Height;
    }

    private (int col, int row) HitTestCell(Point position)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
            return (0, 0);

        int col = Math.Max(0, Math.Min(_cols - 1, (int)(position.X / _cellWidth)));
        int row = Math.Max(0, Math.Min(_rows - 1, (int)(position.Y / _cellHeight)));
        return (col, row);
    }

    private (int startCol, int startRow, int endCol, int endRow) NormalizeSelection()
    {
        int startRow = _selectionStart.row, startCol = _selectionStart.col;
        int endRow = _selectionEnd.row, endCol = _selectionEnd.col;

        if (endRow < startRow || (endRow == startRow && endCol < startCol))
        {
            (startRow, endRow) = (endRow, startRow);
            (startCol, endCol) = (endCol, startCol);
        }
        return (startCol, startRow, endCol, endRow);
    }

    private void CopySelectionToClipboard()
    {
        if (!_hasSelection) return;

        var (startCol, startRow, endCol, endRow) = NormalizeSelection();
        var sb = new StringBuilder();

        for (int row = startRow; row <= endRow; row++)
        {
            int colStart = (row == startRow) ? startCol : 0;
            int colEnd = (row == endRow) ? endCol : _cols - 1;
            var lineBuilder = new StringBuilder();

            for (int col = colStart; col <= colEnd; col++)
            {
                var cell = _cells[col, row];
                char ch = cell.Character;
                lineBuilder.Append(ch == '\0' ? ' ' : ch);
            }

            sb.Append(lineBuilder.ToString().TrimEnd());
            if (row < endRow) sb.AppendLine();
        }

        var text = sb.ToString();
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
    }

    private void ClearSelection()
    {
        _hasSelection = false;
        _isSelecting = false;
    }

    private static byte[]? MapKeyToBytes(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && key == Key.C) return new byte[] { 0x03 };
        if (ctrl && key == Key.D) return new byte[] { 0x04 };
        if (ctrl && key == Key.Z) return new byte[] { 0x1A };
        if (ctrl && key == Key.L) return new byte[] { 0x0C };
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
