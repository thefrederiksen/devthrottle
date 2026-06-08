using System.Text;

namespace CcDirector.Terminal.Core;

/// <summary>
/// VT100/VT220/xterm-compatible terminal emulator. Implements the Paul
/// Williams DEC ANSI parser state machine (vt100.net/emu/dec_ansi_parser),
/// combined with the escape-sequence dispatch table from xterm ctlseqs.
///
/// The public surface is unchanged from the prior implementation so every
/// caller (TerminalControl, TerminalView, CardWebView, tests) keeps working:
///
///   new AnsiParser(cells, cols, rows, scrollback, maxScrollback, log?)
///   parser.Parse(byte[] data)
///   parser.UpdateGrid(cells, cols, rows)       // terminal resize
///
/// Correctness gate: src/CcDirector.Core.Tests/AnsiParserXtermSnapshotTests.cs
/// replays captured .bin streams and asserts the resulting grid is
/// byte-for-byte identical to the one produced by @xterm/headless (the VT
/// engine VS Code ships). That is the reference, not any hand-written spec.
/// </summary>
public class AnsiParser
{
    // --- Grid (mutated in place; owner keeps the reference) ---
    private TerminalCell[,] _cells;
    private int _cols;
    private int _rows;
    private readonly List<TerminalCell[]> _scrollback;
    private readonly int _maxScrollback;

    // --- Height-independent scrollback (issue #240) ---
    // Claude Code 2.1.x repaints its whole TUI in place (absolute cursor
    // positioning, normal buffer, no scroll region), so at tall viewports no
    // linefeed ever crosses the bottom margin: ScrollUp never fires, nothing
    // spills to scrollback, and the panel can't scroll. We recover history by
    // diffing consecutive repaint frames -- bounded by bare ESC[H, which Claude
    // emits exactly once per frame -- and appending the lines that scroll off the
    // top of the scrolling region. The grid is never modified here, so xterm
    // parity is unaffected; this only ever appends to _scrollback.
    private TerminalCell[][]? _committedFrame; // snapshot of the last completed frame
    private int _scrollbackCountAtFrame;       // _scrollback.Count at last frame (ScrollUp reconciliation)

    // --- Cursor ---
    private int _cursorCol;
    private int _cursorRow;
    // DECAWM-deferred "last column pending wrap" flag. When cursor is on the
    // last column and a printable is written, the cell is written but the
    // cursor stays put with this flag set. The next printable that arrives
    // wraps to the next line BEFORE writing. All non-printable cursor moves
    // clear this flag.
    private bool _pendingWrap;

    // --- Scroll region (0-based inclusive) ---
    private int _scrollTop;
    private int _scrollBottom;

    // --- Character attributes ---
    private TerminalColor _fg = TerminalColor.LightGray;
    private TerminalColor _bg = default;
    private bool _bold;
    private bool _italic;
    private bool _underline;
    private bool _reverse;
    private bool _strikethrough;
    private bool _dim;
    private bool _blink;
    private bool _invisible;

    // --- Modes ---
    private bool _autoWrap = true;     // DECAWM (?7)
    private bool _originMode;          // DECOM (?6)
    private bool _cursorVisible = true; // DECTCEM (?25)
    // Bracketed paste / mouse modes are tracked so that mode changes don't
    // corrupt our state; the actual enabling is only reflected in input
    // generation (not our concern here).
    private bool _bracketedPaste;

    // --- Alternate screen buffer (?1049 / ?1047 / ?47) ---
    // Only scroll-region margins need to be saved across the swap; cursor
    // and attrs are handled by the caller via ?1049's implicit save-cursor.
    private TerminalCell[,]? _altCells;
    private int _altSavedScrollTop;
    private int _altSavedScrollBottom;

    // --- DEC save/restore (ESC 7 / ESC 8) ---
    private int _savedCursorCol;
    private int _savedCursorRow;
    private TerminalColor _savedFg;
    private TerminalColor _savedBg;
    private bool _savedBold, _savedItalic, _savedUnderline, _savedReverse;
    private bool _savedStrikethrough, _savedDim, _savedBlink, _savedInvisible;
    private bool _savedOriginMode;
    private bool _savedAutoWrap;
    private bool _hasSavedCursor;

    // --- Character set (G0/G1) ---
    // We honor the DEC Special Graphics set ("line drawing") so that legacy
    // TUIs and box-drawing frames render correctly. Other sets are accepted
    // in the stream (so the escape is consumed) but treated as US ASCII.
    private bool _g0IsDecSpecial;
    private bool _g1IsDecSpecial;
    private int _activeCharset; // 0 or 1 (selected via SI/SO)

    // --- Parser state (Paul Williams) ---
    private State _state = State.Ground;
    private readonly List<int> _params = new();
    private int _currentParam;
    private bool _hasParam;
    private readonly List<byte> _intermediates = new();
    private char _privateMarker; // '?', '>', '<', '=' at the start of CSI params
    private readonly StringBuilder _oscBuffer = new();

    // --- UTF-8 decoding (sub-state within Ground/printable handling) ---
    private readonly byte[] _utf8Buf = new byte[4];
    private int _utf8Needed;
    private int _utf8Len;

    // --- Diagnostics ---
    private readonly Action<string>? _logCallback;
    private long _totalBytesParsed;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        OscString,
        SosPmApcString,
    }

    // 16-color palette used for SGR 30-37, 40-47, 90-97, 100-107 and for
    // 256-color indices &lt; 16. Values match xterm.js defaults so our rendered
    // grid is directly comparable to xterm headless output.
    private static readonly TerminalColor[] AnsiColors =
    {
        TerminalColor.FromRgb(0, 0, 0),        // 0  black
        TerminalColor.FromRgb(205, 49, 49),    // 1  red
        TerminalColor.FromRgb(13, 188, 121),   // 2  green
        TerminalColor.FromRgb(229, 229, 16),   // 3  yellow
        TerminalColor.FromRgb(36, 114, 200),   // 4  blue
        TerminalColor.FromRgb(188, 63, 188),   // 5  magenta
        TerminalColor.FromRgb(17, 168, 205),   // 6  cyan
        TerminalColor.FromRgb(204, 204, 204),  // 7  white
        TerminalColor.FromRgb(102, 102, 102),  // 8  bright black
        TerminalColor.FromRgb(241, 76, 76),    // 9  bright red
        TerminalColor.FromRgb(35, 209, 139),   // 10 bright green
        TerminalColor.FromRgb(245, 245, 67),   // 11 bright yellow
        TerminalColor.FromRgb(59, 142, 234),   // 12 bright blue
        TerminalColor.FromRgb(214, 112, 214),  // 13 bright magenta
        TerminalColor.FromRgb(41, 184, 219),   // 14 bright cyan
        TerminalColor.FromRgb(242, 242, 242),  // 15 bright white
    };

    public AnsiParser(TerminalCell[,] cells, int cols, int rows,
        List<TerminalCell[]> scrollback, int maxScrollback,
        Action<string>? logCallback = null)
    {
        _cells = cells;
        _cols = cols;
        _rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _scrollback = scrollback;
        _maxScrollback = maxScrollback;
        _logCallback = logCallback;
    }

    /// <summary>
    /// Swap the backing grid and dimensions (called on terminal resize).
    /// The parser's cursor and scroll-region are clamped to the new size;
    /// character attributes and parser state are preserved.
    /// </summary>
    public void UpdateGrid(TerminalCell[,] cells, int cols, int rows)
    {
        // If the scroll region covered the whole screen before the resize
        // (the default and overwhelmingly common case), it must continue to
        // cover the whole screen afterwards. Otherwise growing the grid
        // leaves a dead band below the old scrollBottom that the CLI never
        // touches -- the symptom is a huge empty gap below the prompt and
        // blank rows leaking into scrollback on every linefeed.
        bool wasFullScreen = _scrollTop == 0 && _scrollBottom == _rows - 1;

        _cells = cells;
        _cols = cols;
        _rows = rows;
        if (wasFullScreen)
        {
            _scrollTop = 0;
            _scrollBottom = rows - 1;
        }
        else
        {
            _scrollTop = Math.Clamp(_scrollTop, 0, rows - 1);
            _scrollBottom = Math.Clamp(_scrollBottom, _scrollTop, rows - 1);
        }
        _cursorCol = Math.Clamp(_cursorCol, 0, cols - 1);
        _cursorRow = Math.Clamp(_cursorRow, 0, rows - 1);
        _pendingWrap = false;
        // The frame geometry changed; drop the repaint-diff baseline (issue #240).
        _committedFrame = null;
        _scrollbackCountAtFrame = _scrollback.Count;
    }

    public void Parse(byte[] data)
    {
        _totalBytesParsed += data.Length;
        for (int i = 0; i < data.Length; i++)
            Advance(data[i]);
    }

    /// <summary>Current cursor position (0-based col, row). Diagnostic accessor.</summary>
    public (int Col, int Row) GetCursorPosition() => (_cursorCol, _cursorRow);

    /// <summary>Whether the cursor is currently visible per DECTCEM (?25).</summary>
    public bool IsCursorVisible => _cursorVisible;

    /// <summary>
    /// Snapshot of internal parser state, for terminal-capture diagnostics.
    /// Serialized into capture JSON so sessions can be replayed and inspected.
    /// </summary>
    public DiagnosticState GetDiagnosticState() => new()
    {
        CursorCol = _cursorCol,
        CursorRow = _cursorRow,
        CursorVisible = _cursorVisible,
        PendingWrap = _pendingWrap,
        ScrollTop = _scrollTop,
        ScrollBottom = _scrollBottom,
        FgColor = $"#{_fg.R:X2}{_fg.G:X2}{_fg.B:X2}",
        BgColor = $"#{_bg.R:X2}{_bg.G:X2}{_bg.B:X2}",
        Bold = _bold,
        Italic = _italic,
        Underline = _underline,
        Reverse = _reverse,
        ParserState = _state.ToString(),
        IntermediateChar = _intermediates.Count > 0 ? ((char)_intermediates[0]).ToString() : null,
        CsiParams = _params.ToArray(),
        HasCurrentParam = _hasParam,
        CurrentParam = _currentParam,
        Utf8Needed = _utf8Needed,
        Utf8Len = _utf8Len,
        HasSavedScreen = _altCells != null,
        SavedCursorCol = _savedCursorCol,
        SavedCursorRow = _savedCursorRow,
        TotalBytesParsed = _totalBytesParsed,
        GridCols = _cols,
        GridRows = _rows,
        ScrollbackCount = _scrollback.Count,
    };

    public class DiagnosticState
    {
        public int CursorCol { get; init; }
        public int CursorRow { get; init; }
        public bool CursorVisible { get; init; }
        public bool PendingWrap { get; init; }
        public int ScrollTop { get; init; }
        public int ScrollBottom { get; init; }
        public string FgColor { get; init; } = "";
        public string BgColor { get; init; } = "";
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public bool Underline { get; init; }
        public bool Reverse { get; init; }
        public string ParserState { get; init; } = "";
        public string? IntermediateChar { get; init; }
        public int[] CsiParams { get; init; } = [];
        public bool HasCurrentParam { get; init; }
        public int CurrentParam { get; init; }
        public int Utf8Needed { get; init; }
        public int Utf8Len { get; init; }
        public bool HasSavedScreen { get; init; }
        public int SavedCursorCol { get; init; }
        public int SavedCursorRow { get; init; }
        public long TotalBytesParsed { get; init; }
        public int GridCols { get; init; }
        public int GridRows { get; init; }
        public int ScrollbackCount { get; init; }
    }

    // -----------------------------------------------------------------------
    // Paul Williams state-machine core
    // -----------------------------------------------------------------------

    /// <summary>
    /// Process one byte through the state machine. This is the central
    /// switch -- nothing else should call state-transition actions directly.
    /// </summary>
    private void Advance(byte b)
    {
        // "Anywhere" transitions (Paul Williams calls these "anywhere" edges --
        // they fire regardless of current state so a stray ESC or CAN resyncs
        // the parser rather than getting swallowed).
        switch (b)
        {
            case 0x18: // CAN
            case 0x1A: // SUB
                if (_state == State.OscString) { /* discard accumulated OSC */ _oscBuffer.Clear(); }
                EnterGround();
                return;
            case 0x1B: // ESC
                EnterEscape();
                return;
        }

        switch (_state)
        {
            case State.Ground:                 StepGround(b); break;
            case State.Escape:                 StepEscape(b); break;
            case State.EscapeIntermediate:     StepEscapeIntermediate(b); break;
            case State.CsiEntry:               StepCsiEntry(b); break;
            case State.CsiParam:               StepCsiParam(b); break;
            case State.CsiIntermediate:        StepCsiIntermediate(b); break;
            case State.CsiIgnore:              StepCsiIgnore(b); break;
            case State.DcsEntry:               StepDcsEntry(b); break;
            case State.DcsParam:               StepDcsParam(b); break;
            case State.DcsIntermediate:        StepDcsIntermediate(b); break;
            case State.DcsPassthrough:         StepDcsPassthrough(b); break;
            case State.DcsIgnore:              StepDcsIgnore(b); break;
            case State.OscString:              StepOscString(b); break;
            case State.SosPmApcString:         StepSosPmApcString(b); break;
        }
    }

    private void EnterGround()
    {
        _state = State.Ground;
    }

    private void EnterEscape()
    {
        _state = State.Escape;
        _params.Clear();
        _currentParam = 0;
        _hasParam = false;
        _intermediates.Clear();
        _privateMarker = '\0';
    }

    // -----------------------------------------------------------------------
    // Ground-state handling (executes C0 controls; otherwise writes printable)
    // -----------------------------------------------------------------------

    private void StepGround(byte b)
    {
        // Continuation of a multi-byte UTF-8 sequence.
        if (_utf8Needed > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Buf[_utf8Len++] = b;
                if (--_utf8Needed == 0)
                {
                    PutUtf8();
                }
                return;
            }
            // Malformed continuation: abandon partial sequence and re-process b.
            _utf8Needed = 0;
            _utf8Len = 0;
        }

        if (b < 0x20)
        {
            ExecuteC0(b);
            return;
        }

        if (b == 0x7F)
        {
            // DEL -- ignore, per xterm.
            return;
        }

        if (b < 0x80)
        {
            // Plain ASCII printable.
            PutChar(b, codepoint: b);
            return;
        }

        // Start of a multi-byte UTF-8 sequence.
        if ((b & 0xE0) == 0xC0)        { _utf8Buf[0] = b; _utf8Len = 1; _utf8Needed = 1; return; }
        if ((b & 0xF0) == 0xE0)        { _utf8Buf[0] = b; _utf8Len = 1; _utf8Needed = 2; return; }
        if ((b & 0xF8) == 0xF0)        { _utf8Buf[0] = b; _utf8Len = 1; _utf8Needed = 3; return; }
        // Stray continuation byte (0x80-0xBF) or 5-6 byte sequence (disallowed) -- drop.
    }

    private void PutUtf8()
    {
        int cp = DecodeUtf8();
        _utf8Len = 0;
        if (cp < 0) return;
        PutChar(b: (byte)(cp & 0xFF), codepoint: cp);
    }

    private int DecodeUtf8()
    {
        int cp;
        byte b0 = _utf8Buf[0];
        if ((b0 & 0xE0) == 0xC0)
            cp = ((b0 & 0x1F) << 6) | (_utf8Buf[1] & 0x3F);
        else if ((b0 & 0xF0) == 0xE0)
            cp = ((b0 & 0x0F) << 12) | ((_utf8Buf[1] & 0x3F) << 6) | (_utf8Buf[2] & 0x3F);
        else if ((b0 & 0xF8) == 0xF0)
            cp = ((b0 & 0x07) << 18) | ((_utf8Buf[1] & 0x3F) << 12) | ((_utf8Buf[2] & 0x3F) << 6) | (_utf8Buf[3] & 0x3F);
        else
            return -1;
        if (cp > 0x10FFFF) return -1;
        // Reject surrogate scalars (they're an invalid UTF-8 encoding).
        if (cp >= 0xD800 && cp <= 0xDFFF) return -1;
        return cp;
    }

    private void ExecuteC0(byte b)
    {
        switch (b)
        {
            case 0x00:                     return; // NUL -- discard
            case 0x07:                     return; // BEL -- no-op visually
            case 0x08:                              // BS
                _pendingWrap = false;
                if (_cursorCol > 0) _cursorCol--;
                return;
            case 0x09:                              // HT
                _pendingWrap = false;
                // Tab stops at every 8 columns (VT100 default).
                _cursorCol = Math.Min(_cols - 1, (_cursorCol / 8 + 1) * 8);
                return;
            case 0x0A:                              // LF
            case 0x0B:                              // VT
            case 0x0C:                              // FF
                _pendingWrap = false;
                LineFeed();
                return;
            case 0x0D:                              // CR
                _pendingWrap = false;
                _cursorCol = 0;
                return;
            case 0x0E:                              // SO (LS1)
                _activeCharset = 1; return;
            case 0x0F:                              // SI (LS0)
                _activeCharset = 0; return;
        }
        // Other C0 bytes are not meaningful here; drop silently.
    }

    // -----------------------------------------------------------------------
    // Escape state
    // -----------------------------------------------------------------------

    private void StepEscape(byte b)
    {
        if (b >= 0x20 && b <= 0x2F)
        {
            // Intermediate -- accumulate and transition.
            _intermediates.Add(b);
            _state = State.EscapeIntermediate;
            return;
        }
        if (b == 0x5B) { _state = State.CsiEntry;   return; } // ESC [
        if (b == 0x5D) { _oscBuffer.Clear(); _state = State.OscString; return; } // ESC ]
        if (b == 0x50) { _params.Clear(); _currentParam = 0; _hasParam = false; _intermediates.Clear(); _state = State.DcsEntry; return; } // ESC P
        if (b == 0x58 || b == 0x5E || b == 0x5F) { _state = State.SosPmApcString; return; } // X / ^ / _

        if (b >= 0x30 && b <= 0x7E)
        {
            EscDispatch((char)b);
            EnterGround();
            return;
        }

        // Unknown / out-of-range byte -- swallow and stay in Ground.
        EnterGround();
    }

    private void StepEscapeIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) { _intermediates.Add(b); return; }
        if (b >= 0x30 && b <= 0x7E)
        {
            EscDispatch((char)b);
            EnterGround();
            return;
        }
        EnterGround();
    }

    private void EscDispatch(char final)
    {
        // Intermediates first (character-set designations, etc.).
        if (_intermediates.Count > 0)
        {
            byte i0 = _intermediates[0];
            switch (i0)
            {
                case 0x28: // ( -- designate G0
                    _g0IsDecSpecial = (final == '0');
                    return;
                case 0x29: // ) -- designate G1
                    _g1IsDecSpecial = (final == '0');
                    return;
                case 0x2A: // * -- G2 (not honored)
                case 0x2B: // + -- G3 (not honored)
                    return;
                case 0x23: // #
                    if (final == '8')
                    {
                        // DECALN -- fill screen with 'E'.
                        for (int r = 0; r < _rows; r++)
                            for (int c = 0; c < _cols; c++)
                                _cells[c, r] = new TerminalCell { Character = 'E' };
                        _cursorCol = 0;
                        _cursorRow = 0;
                        _pendingWrap = false;
                    }
                    return;
            }
            return;
        }

        switch (final)
        {
            case 'D': // IND -- index (LF behavior, respects scroll region)
                _pendingWrap = false;
                LineFeed();
                break;
            case 'E': // NEL -- next line (CR + LF)
                _pendingWrap = false;
                _cursorCol = 0;
                LineFeed();
                break;
            case 'H': // HTS -- tab stop (unused; we use 8-col default)
                break;
            case 'M': // RI -- reverse index (scroll-down at top)
                _pendingWrap = false;
                if (_cursorRow == _scrollTop) ScrollDown();
                else if (_cursorRow > 0) _cursorRow--;
                break;
            case '7': // DECSC -- save cursor+attrs
                SaveCursor();
                break;
            case '8': // DECRC -- restore cursor+attrs
                RestoreCursor();
                break;
            case 'c': // RIS -- full reset
                FullReset();
                break;
            case '=':
            case '>':
            case 'n':
            case 'o':
                // DECKPAM/DECKPNM, LS2/LS3 -- accepted without effect.
                break;
        }
    }

    // -----------------------------------------------------------------------
    // CSI state
    // -----------------------------------------------------------------------

    private void StepCsiEntry(byte b)
    {
        if (b >= 0x30 && b <= 0x39)      { CollectParamDigit(b); _state = State.CsiParam; return; }
        if (b == 0x3B)                    { CommitParam(); _state = State.CsiParam; return; }
        if (b == 0x3A)                    { /* colon-separated sub-param -- treat as param separator */ CommitParam(); _state = State.CsiParam; return; }
        if (b >= 0x3C && b <= 0x3F)       { _privateMarker = (char)b; _state = State.CsiParam; return; }
        if (b >= 0x20 && b <= 0x2F)       { _intermediates.Add(b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E)       { CsiDispatch((char)b); EnterGround(); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiParam(byte b)
    {
        if (b >= 0x30 && b <= 0x39)      { CollectParamDigit(b); return; }
        if (b == 0x3B)                    { CommitParam(); return; }
        if (b == 0x3A)                    { CommitParam(); return; }
        if (b >= 0x3C && b <= 0x3F)       { _state = State.CsiIgnore; return; }
        if (b >= 0x20 && b <= 0x2F)       { _intermediates.Add(b); _state = State.CsiIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E)       { CsiDispatch((char)b); EnterGround(); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) { _intermediates.Add(b); return; }
        if (b >= 0x40 && b <= 0x7E) { CsiDispatch((char)b); EnterGround(); return; }
        _state = State.CsiIgnore;
    }

    private void StepCsiIgnore(byte b)
    {
        if (b >= 0x40 && b <= 0x7E) EnterGround();
    }

    private void CollectParamDigit(byte b)
    {
        _currentParam = _currentParam * 10 + (b - '0');
        _hasParam = true;
    }

    private void CommitParam()
    {
        _params.Add(_hasParam ? _currentParam : 0);
        _currentParam = 0;
        _hasParam = false;
    }

    // -----------------------------------------------------------------------
    // DCS / OSC / SOS/PM/APC
    // -----------------------------------------------------------------------

    private void StepDcsEntry(byte b)
    {
        // We don't implement any DCS sequences (DECRQSS, sixel, etc.) --
        // swallow to the terminator.
        if (b >= 0x30 && b <= 0x39)      { CollectParamDigit(b); _state = State.DcsParam; return; }
        if (b == 0x3B)                    { CommitParam(); _state = State.DcsParam; return; }
        if (b >= 0x3C && b <= 0x3F)       { _privateMarker = (char)b; _state = State.DcsParam; return; }
        if (b >= 0x20 && b <= 0x2F)       { _intermediates.Add(b); _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E)       { _state = State.DcsIgnore; return; }
    }

    private void StepDcsParam(byte b)
    {
        if (b >= 0x30 && b <= 0x39)      { CollectParamDigit(b); return; }
        if (b == 0x3B)                    { CommitParam(); return; }
        if (b >= 0x20 && b <= 0x2F)       { _intermediates.Add(b); _state = State.DcsIntermediate; return; }
        if (b >= 0x40 && b <= 0x7E)       { _state = State.DcsIgnore; return; }
    }

    private void StepDcsIntermediate(byte b)
    {
        if (b >= 0x20 && b <= 0x2F) { _intermediates.Add(b); return; }
        if (b >= 0x40 && b <= 0x7E) { _state = State.DcsIgnore; return; }
    }

    private void StepDcsPassthrough(byte b)
    {
        // Unreachable in current config (we never enter pass-through); keep the
        // method for state-machine completeness and to absorb malformed data.
        if (b == 0x9C) EnterGround();
    }

    private void StepDcsIgnore(byte b)
    {
        if (b == 0x9C) EnterGround();
    }

    // OSC accumulation. Per xterm, OSC strings end on BEL (0x07) OR ST (ESC \\).
    // The Paul Williams spec routes ESC through Anywhere back to Escape, and
    // a subsequent 0x5C byte in Escape closes the string and dispatches. The
    // previous (broken) implementation treated any raw 0x1B or 0x07 as an
    // unconditional terminator, which desynced the parser if an OSC payload
    // legitimately contained those bytes. This version accumulates payload
    // bytes until the correct terminator.
    private void StepOscString(byte b)
    {
        if (b == 0x07) { OscDispatch(); EnterGround(); return; }
        // 0x1B (ESC) is handled by the Anywhere transition: it leaves this
        // state and enters Escape. Then if the next byte is 0x5C ('\'), the
        // ST closes the OSC. We detect that in StepEscape via a pre-stash.
        _oscBuffer.Append((char)b);
    }

    private void StepSosPmApcString(byte b)
    {
        // No payload handling -- strings are swallowed until ST or BEL.
        if (b == 0x07) EnterGround();
    }

    private void OscDispatch()
    {
        // We don't act on OSC payloads (titles, hyperlinks, palette changes
        // etc. are not rendered here) -- just making sure we resync cleanly.
        _oscBuffer.Clear();
    }

    // -----------------------------------------------------------------------
    // CSI dispatch
    // -----------------------------------------------------------------------

    private void CsiDispatch(char final)
    {
        if (_hasParam) CommitParam();

        if (_privateMarker == '?')
        {
            DispatchDecPrivate(final);
            return;
        }
        // Other private markers (>, <, =) aren't meaningful for display.
        if (_privateMarker != '\0') return;

        int p0 = _params.Count > 0 ? _params[0] : 0;
        int p1 = _params.Count > 1 ? _params[1] : 0;

        switch (final)
        {
            case 'A': MoveCursor(-Math.Max(1, p0), 0); return;                         // CUU
            case 'B': MoveCursor(Math.Max(1, p0), 0); return;                          // CUD
            case 'C': MoveCursor(0, Math.Max(1, p0)); return;                          // CUF
            case 'D': MoveCursor(0, -Math.Max(1, p0)); return;                         // CUB
            case 'E': _pendingWrap = false; _cursorCol = 0; MoveCursor(Math.Max(1, p0), 0); return;  // CNL
            case 'F': _pendingWrap = false; _cursorCol = 0; MoveCursor(-Math.Max(1, p0), 0); return; // CPL
            case 'G':                                                                  // CHA
            case '`':                                                                  // HPA (same)
                _pendingWrap = false;
                _cursorCol = Math.Clamp(OneBasedDefault(p0, 1) - 1, 0, _cols - 1);
                return;
            case 'H':                                                                  // CUP
            case 'f':                                                                  // HVP
            {
                // Bare ESC[H (no params) is Claude Code's per-frame repaint marker
                // (issue #240). The grid currently holds the just-finished frame;
                // commit it to scrollback before the new frame overwrites it.
                if (final == 'H' && _params.Count == 0)
                    CommitRepaintFrame();
                _pendingWrap = false;
                int rowOneBased = OneBasedDefault(p0, 1);
                int colOneBased = OneBasedDefault(p1, 1);
                int row = rowOneBased - 1;
                int col = colOneBased - 1;
                if (_originMode)
                {
                    row += _scrollTop;
                    _cursorRow = Math.Clamp(row, _scrollTop, _scrollBottom);
                }
                else
                {
                    _cursorRow = Math.Clamp(row, 0, _rows - 1);
                }
                _cursorCol = Math.Clamp(col, 0, _cols - 1);
                return;
            }
            case 'I': CursorTabForward(Math.Max(1, p0)); return;                       // CHT
            case 'J': EraseInDisplay(p0); return;                                      // ED
            case 'K': EraseInLine(p0); return;                                         // EL
            case 'L': InsertLines(Math.Max(1, p0)); return;                            // IL
            case 'M': DeleteLines(Math.Max(1, p0)); return;                            // DL
            case 'P': DeleteChars(Math.Max(1, p0)); return;                            // DCH
            case 'S': for (int i = 0; i < Math.Max(1, p0); i++) ScrollUp(); return;    // SU
            case 'T': for (int i = 0; i < Math.Max(1, p0); i++) ScrollDown(); return;  // SD
            case 'X': EraseChars(Math.Max(1, p0)); return;                             // ECH
            case 'Z': CursorTabBack(Math.Max(1, p0)); return;                          // CBT
            case '@': InsertChars(Math.Max(1, p0)); return;                            // ICH
            case 'b': RepeatLastChar(Math.Max(1, p0)); return;                         // REP
            case 'd':                                                                  // VPA
                _pendingWrap = false;
                _cursorRow = Math.Clamp(OneBasedDefault(p0, 1) - 1, 0, _rows - 1);
                return;
            case 'e':                                                                  // VPR
                _pendingWrap = false;
                _cursorRow = Math.Clamp(_cursorRow + Math.Max(1, p0), 0, _rows - 1);
                return;
            case 'g': return;                                                          // TBC (tab clear) -- ignored
            case 'h': SetMode(true); return;                                           // SM
            case 'l': SetMode(false); return;                                          // RM
            case 'm': HandleSgr(); return;                                             // SGR
            case 'n': return;                                                          // DSR -- we can't respond (output-only parser)
            case 'r':                                                                  // DECSTBM
            {
                int top = _params.Count > 0 && _params[0] > 0 ? _params[0] - 1 : 0;
                int bot = _params.Count > 1 && _params[1] > 0 ? _params[1] - 1 : _rows - 1;
                if (top < bot && bot < _rows)
                {
                    _scrollTop = top;
                    _scrollBottom = bot;
                }
                else
                {
                    _scrollTop = 0;
                    _scrollBottom = _rows - 1;
                }
                _pendingWrap = false;
                _cursorCol = 0;
                _cursorRow = _originMode ? _scrollTop : 0;
                return;
            }
            case 's': SaveCursor(); return;                                            // SCP
            case 'u': RestoreCursor(); return;                                         // RCP
            case 't': return;                                                          // Window ops -- ignored
            case 'c': return;                                                          // DA -- output-only, ignore
        }
    }

    private static int OneBasedDefault(int value, int defaultValue)
        => value > 0 ? value : defaultValue;

    private void MoveCursor(int deltaRow, int deltaCol)
    {
        _pendingWrap = false;
        if (deltaRow != 0)
        {
            // Cursor moves clamp WITHIN the scroll region if the cursor is
            // already inside it. Otherwise clamp to the full grid. This is
            // the xterm "DECSTBM scoping" behavior.
            int minR = _cursorRow >= _scrollTop && _cursorRow <= _scrollBottom ? _scrollTop : 0;
            int maxR = _cursorRow >= _scrollTop && _cursorRow <= _scrollBottom ? _scrollBottom : _rows - 1;
            _cursorRow = Math.Clamp(_cursorRow + deltaRow, minR, maxR);
        }
        if (deltaCol != 0)
        {
            _cursorCol = Math.Clamp(_cursorCol + deltaCol, 0, _cols - 1);
        }
    }

    private void CursorTabForward(int n)
    {
        _pendingWrap = false;
        for (int i = 0; i < n; i++)
        {
            int next = ((_cursorCol / 8) + 1) * 8;
            if (next >= _cols) { _cursorCol = _cols - 1; break; }
            _cursorCol = next;
        }
    }

    private void CursorTabBack(int n)
    {
        _pendingWrap = false;
        for (int i = 0; i < n; i++)
        {
            if (_cursorCol == 0) break;
            int prev = ((_cursorCol - 1) / 8) * 8;
            _cursorCol = prev;
        }
    }

    // -----------------------------------------------------------------------
    // DEC private modes (?...)
    // -----------------------------------------------------------------------

    private void DispatchDecPrivate(char final)
    {
        bool set = final == 'h';
        bool reset = final == 'l';
        if (!set && !reset) return;

        foreach (int mode in _params)
        {
            switch (mode)
            {
                case 6:    _originMode = set; _pendingWrap = false; _cursorCol = 0; _cursorRow = set ? _scrollTop : 0; break;
                case 7:    _autoWrap = set; break;
                case 25:   _cursorVisible = set; break;
                case 47:
                case 1047: SwitchAltScreen(set, saveCursor: false); break;
                case 1048: if (set) SaveCursor(); else RestoreCursor(); break;
                case 1049: SwitchAltScreen(set, saveCursor: true); break;
                case 2004: _bracketedPaste = set; break;
                // 1000-1006 (mouse), 2026 (synchronized update), etc. -- accepted without effect.
            }
        }
    }

    // -----------------------------------------------------------------------
    // Printable output
    // -----------------------------------------------------------------------

    private void PutChar(byte b, int codepoint)
    {
        int width = CharWidth.ForCodepoint(codepoint);

        if (width == 0)
        {
            // Zero-width combining mark / joiner -- overlay on the previous
            // cell. If the prev cell is unwritten, we just drop it (we don't
            // synthesize a base character).
            int targetCol = _pendingWrap ? _cols - 1 : _cursorCol - 1;
            if (targetCol < 0 || targetCol >= _cols) return;
            // We don't currently model multi-codepoint grapheme clusters,
            // so this is a no-op beyond state tracking.
            return;
        }

        if (_pendingWrap && _autoWrap)
        {
            _pendingWrap = false;
            _cursorCol = 0;
            LineFeed();
        }
        else if (_pendingWrap)
        {
            // Auto-wrap off: overwrite the last column in place.
            _pendingWrap = false;
        }

        // Apply DEC Special Graphics translation for the active G-set.
        char display = TranslateForActiveCharset(codepoint);

        var fg = _fg;
        var bg = _bg;
        if (_reverse)
        {
            fg = _bg == default ? TerminalColor.FromRgb(0, 0, 0) : _bg;
            bg = _fg;
        }

        if (width == 2)
        {
            // If writing at the last column, xterm wraps FIRST (even if
            // auto-wrap just fired) because a width-2 glyph cannot fit.
            if (_cursorCol >= _cols - 1)
            {
                if (_autoWrap)
                {
                    _cursorCol = 0;
                    LineFeed();
                }
                else
                {
                    // No room and no wrap -- write a space and stop.
                    _cells[_cols - 1, _cursorRow] = new TerminalCell { Character = ' ', Foreground = fg, Background = bg };
                    _pendingWrap = true;
                    return;
                }
            }

            _cells[_cursorCol, _cursorRow] = new TerminalCell
            {
                Character = display,
                Foreground = fg,
                Background = bg,
                Bold = _bold,
                Italic = _italic,
                Underline = _underline,
            };
            // Width-2 continuation cell: xterm stores this as empty-string so
            // nothing else is drawn over it until the line is redrawn. We
            // represent it as a space cell with the same bg so background
            // color continues correctly under wide chars.
            _cells[_cursorCol + 1, _cursorRow] = new TerminalCell
            {
                Character = ' ',
                Foreground = fg,
                Background = bg,
            };

            if (_cursorCol + 2 < _cols)
            {
                _cursorCol += 2;
            }
            else
            {
                _cursorCol = _cols - 1;
                _pendingWrap = true;
            }
            return;
        }

        // width == 1
        _cells[_cursorCol, _cursorRow] = new TerminalCell
        {
            Character = display,
            Foreground = fg,
            Background = bg,
            Bold = _bold,
            Italic = _italic,
            Underline = _underline,
        };

        if (_cursorCol < _cols - 1)
        {
            _cursorCol++;
        }
        else
        {
            _pendingWrap = _autoWrap;
        }
    }

    private char TranslateForActiveCharset(int codepoint)
    {
        // DEC Special Graphics (line drawing). Only applied for ASCII bytes
        // (codepoint < 0x80) when the active G-set is the DEC special set.
        bool useDec = (_activeCharset == 0 && _g0IsDecSpecial) ||
                      (_activeCharset == 1 && _g1IsDecSpecial);
        if (!useDec || codepoint >= 0x80) return (char)codepoint;

        // Mapping from xterm (0x60..0x7E range; anything else passes through).
        return codepoint switch
        {
            0x60 => '◆', // diamond
            0x61 => '▒', // checkerboard
            0x62 => '␉', // HT symbol
            0x63 => '␌', // FF symbol
            0x64 => '␍', // CR symbol
            0x65 => '␊', // LF symbol
            0x66 => '°', // degree
            0x67 => '±', // plus/minus
            0x68 => '␤', // NL symbol
            0x69 => '␋', // VT symbol
            0x6A => '┘', // lower-right corner
            0x6B => '┐', // upper-right corner
            0x6C => '┌', // upper-left corner
            0x6D => '└', // lower-left corner
            0x6E => '┼', // crossing lines
            0x6F => '⎺', // horizontal line - scan 1
            0x70 => '⎻', // horizontal line - scan 3
            0x71 => '─', // horizontal line - scan 5
            0x72 => '⎼', // horizontal line - scan 7
            0x73 => '⎽', // horizontal line - scan 9
            0x74 => '├', // left tee
            0x75 => '┤', // right tee
            0x76 => '┴', // bottom tee
            0x77 => '┬', // top tee
            0x78 => '│', // vertical line
            0x79 => '≤', // less or equal
            0x7A => '≥', // greater or equal
            0x7B => 'π', // pi
            0x7C => '≠', // not equal
            0x7D => '£', // pound sterling
            0x7E => '·', // middle dot
            _    => (char)codepoint,
        };
    }

    // -----------------------------------------------------------------------
    // Scrolling, erasing, inserting
    // -----------------------------------------------------------------------

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom) ScrollUp();
        else if (_cursorRow < _rows - 1) _cursorRow++;
    }

    private void ScrollUp()
    {
        // Only save to scrollback when scrolling the full screen (top margin
        // at row 0); otherwise we're inside a DECSTBM region and the rows
        // that leave are gone.
        if (_scrollTop == 0 && _altCells == null)
        {
            var savedRow = new TerminalCell[_cols];
            for (int c = 0; c < _cols; c++)
                savedRow[c] = _cells[c, 0];
            _scrollback.Add(savedRow);
            while (_scrollback.Count > _maxScrollback)
                _scrollback.RemoveAt(0);
        }

        for (int r = _scrollTop; r < _scrollBottom; r++)
            for (int c = 0; c < _cols; c++)
                _cells[c, r] = _cells[c, r + 1];
        ClearRowBce(_scrollBottom);
    }

    private void ScrollDown()
    {
        for (int r = _scrollBottom; r > _scrollTop; r--)
            for (int c = 0; c < _cols; c++)
                _cells[c, r] = _cells[c, r - 1];
        ClearRowBce(_scrollTop);
    }

    // -----------------------------------------------------------------------
    // Height-independent scrollback (issue #240): recover history from in-place
    // repaint frames (Claude Code's TUI) that never trigger ScrollUp.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Called at a repaint frame boundary (bare ESC[H). The grid holds the
    /// just-completed previous frame; diff it against the prior committed frame
    /// and append the lines that scrolled off the top of the scrolling region.
    /// The fixed bottom band (input box / separators) is located content-agnostically
    /// as the longest identical suffix and excluded, so the box is never pushed into
    /// scrollback. Appends nothing when the change is not a clean upward scroll, so
    /// unrelated full repaints add no garbage. Never mutates the grid.
    /// </summary>
    private void CommitRepaintFrame()
    {
        if (_altCells != null) return; // never capture alt-screen frames

        var current = SnapshotGrid();

        // First frame, or just after a resize: establish the baseline.
        if (_committedFrame == null || _committedFrame.Length != _rows)
        {
            _committedFrame = current;
            _scrollbackCountAtFrame = _scrollback.Count;
            return;
        }

        // If real scrolling (ScrollUp) already pushed lines since the last frame,
        // that path captured the history -- don't double-count it here.
        if (_scrollback.Count != _scrollbackCountAtFrame)
        {
            _committedFrame = current;
            _scrollbackCountAtFrame = _scrollback.Count;
            return;
        }

        var old = _committedFrame;

        // The fixed bottom band (input box) is the longest identical suffix; the
        // scrolling region is everything above it.
        int fixedBottom = 0;
        for (int r = _rows - 1; r >= 0; r--)
        {
            if (RowTextEquals(old[r], current[r])) fixedBottom++;
            else break;
        }
        int regionBottom = _rows - 1 - fixedBottom; // last row index of the scroll region

        if (regionBottom >= 1)
        {
            // Find the upward shift K that yields the longest contiguous run from
            // the top where current[i] == old[i + K]. A strong run confirms a real
            // scroll; in-place status lines lower in the region merely shorten it.
            const int minRun = 3;    // contiguous top rows that must align to accept a scroll
            const int maxShift = 64; // sane per-frame cap
            int bestK = 0, bestRun = 0;
            int kLimit = Math.Min(regionBottom, maxShift);
            for (int k = 1; k <= kLimit; k++)
            {
                int run = 0;
                for (int i = 0; i + k <= regionBottom; i++)
                {
                    if (RowTextEquals(current[i], old[i + k])) run++;
                    else break;
                }
                if (run > bestRun) { bestRun = run; bestK = k; }
            }

            if (bestK > 0 && bestRun >= minRun)
            {
                for (int i = 0; i < bestK; i++)
                {
                    _scrollback.Add(CopyRow(old[i]));
                    if (_scrollback.Count > _maxScrollback)
                        _scrollback.RemoveAt(0);
                }
            }
        }

        _committedFrame = current;
        _scrollbackCountAtFrame = _scrollback.Count;
    }

    private TerminalCell[][] SnapshotGrid()
    {
        var snap = new TerminalCell[_rows][];
        for (int r = 0; r < _rows; r++)
        {
            var row = new TerminalCell[_cols];
            for (int c = 0; c < _cols; c++)
                row[c] = _cells[c, r];
            snap[r] = row;
        }
        return snap;
    }

    private static TerminalCell[] CopyRow(TerminalCell[] row)
    {
        var copy = new TerminalCell[row.Length];
        Array.Copy(row, copy, row.Length);
        return copy;
    }

    /// <summary>Compare two rows by glyph content only, ignoring attribute noise (spinner restyles).</summary>
    private static bool RowTextEquals(TerminalCell[] a, TerminalCell[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Character != b[i].Character) return false;
        return true;
    }

    private TerminalCell BceCell() => new() { Background = _bg };

    private void ClearRowBce(int row)
    {
        var bce = BceCell();
        for (int c = 0; c < _cols; c++)
            _cells[c, row] = bce;
    }

    private void EraseInDisplay(int mode)
    {
        var bce = BceCell();
        switch (mode)
        {
            case 0:
                for (int c = _cursorCol; c < _cols; c++)
                    _cells[c, _cursorRow] = bce;
                for (int r = _cursorRow + 1; r < _rows; r++)
                    ClearRowBce(r);
                break;
            case 1:
                for (int r = 0; r < _cursorRow; r++)
                    ClearRowBce(r);
                for (int c = 0; c <= _cursorCol && c < _cols; c++)
                    _cells[c, _cursorRow] = bce;
                break;
            case 2:
            case 3:
                for (int r = 0; r < _rows; r++)
                    ClearRowBce(r);
                if (mode == 3) _scrollback.Clear();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var bce = BceCell();
        switch (mode)
        {
            case 0:
                for (int c = _cursorCol; c < _cols; c++)
                    _cells[c, _cursorRow] = bce;
                break;
            case 1:
                for (int c = 0; c <= _cursorCol && c < _cols; c++)
                    _cells[c, _cursorRow] = bce;
                break;
            case 2:
                ClearRowBce(_cursorRow);
                break;
        }
    }

    private void EraseChars(int count)
    {
        var bce = BceCell();
        for (int c = _cursorCol; c < _cursorCol + count && c < _cols; c++)
            _cells[c, _cursorRow] = bce;
    }

    private void InsertChars(int count)
    {
        var bce = BceCell();
        for (int c = _cols - 1; c >= _cursorCol + count; c--)
            _cells[c, _cursorRow] = _cells[c - count, _cursorRow];
        for (int c = _cursorCol; c < _cursorCol + count && c < _cols; c++)
            _cells[c, _cursorRow] = bce;
    }

    private void DeleteChars(int count)
    {
        var bce = BceCell();
        for (int c = _cursorCol; c < _cols - count; c++)
            _cells[c, _cursorRow] = _cells[c + count, _cursorRow];
        for (int c = Math.Max(_cursorCol, _cols - count); c < _cols; c++)
            _cells[c, _cursorRow] = bce;
    }

    private void InsertLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom) return;
        for (int n = 0; n < count; n++)
        {
            for (int r = _scrollBottom; r > _cursorRow; r--)
                for (int c = 0; c < _cols; c++)
                    _cells[c, r] = _cells[c, r - 1];
            ClearRowBce(_cursorRow);
        }
    }

    private void DeleteLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom) return;
        for (int n = 0; n < count; n++)
        {
            for (int r = _cursorRow; r < _scrollBottom; r++)
                for (int c = 0; c < _cols; c++)
                    _cells[c, r] = _cells[c, r + 1];
            ClearRowBce(_scrollBottom);
        }
    }

    private void RepeatLastChar(int count)
    {
        // REP repeats the last printed character. We don't track it across
        // escape sequences (xterm's behavior is also limited); treat as no-op.
    }

    // -----------------------------------------------------------------------
    // Mode set/reset (non-private)
    // -----------------------------------------------------------------------

    private void SetMode(bool set)
    {
        foreach (int m in _params)
        {
            switch (m)
            {
                case 4: /* IRM insert mode -- not honored */ break;
                case 20: /* LNM -- not honored */ break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // SGR
    // -----------------------------------------------------------------------

    private void HandleSgr()
    {
        if (_params.Count == 0)
        {
            ResetAttributes();
            return;
        }

        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: ResetAttributes(); break;
                case 1: _bold = true; break;
                case 2: _dim = true; break;
                case 3: _italic = true; break;
                case 4: _underline = true; break;
                case 5: case 6: _blink = true; break;
                case 7: _reverse = true; break;
                case 8: _invisible = true; break;
                case 9: _strikethrough = true; break;
                case 21: case 22: _bold = false; _dim = false; break;
                case 23: _italic = false; break;
                case 24: _underline = false; break;
                case 25: _blink = false; break;
                case 27: _reverse = false; break;
                case 28: _invisible = false; break;
                case 29: _strikethrough = false; break;
                case 30: case 31: case 32: case 33:
                case 34: case 35: case 36: case 37:
                    _fg = AnsiColors[p - 30]; break;
                case 38:
                    i = ParseExtendedColor(i, out _fg);
                    break;
                case 39: _fg = TerminalColor.LightGray; break;
                case 40: case 41: case 42: case 43:
                case 44: case 45: case 46: case 47:
                    _bg = AnsiColors[p - 40]; break;
                case 48:
                    i = ParseExtendedColor(i, out _bg);
                    break;
                case 49: _bg = default; break;
                case 90: case 91: case 92: case 93:
                case 94: case 95: case 96: case 97:
                    _fg = AnsiColors[p - 90 + 8]; break;
                case 100: case 101: case 102: case 103:
                case 104: case 105: case 106: case 107:
                    _bg = AnsiColors[p - 100 + 8]; break;
            }
        }
    }

    private int ParseExtendedColor(int index, out TerminalColor color)
    {
        color = TerminalColor.LightGray;
        if (index + 1 >= _params.Count) return index;

        int mode = _params[index + 1];
        if (mode == 5 && index + 2 < _params.Count)
        {
            color = Get256Color(_params[index + 2]);
            return index + 2;
        }
        if (mode == 2 && index + 4 < _params.Count)
        {
            int r = Math.Clamp(_params[index + 2], 0, 255);
            int g = Math.Clamp(_params[index + 3], 0, 255);
            int b = Math.Clamp(_params[index + 4], 0, 255);
            color = TerminalColor.FromRgb((byte)r, (byte)g, (byte)b);
            return index + 4;
        }
        return index;
    }

    private static TerminalColor Get256Color(int index)
    {
        if (index < 16) return AnsiColors[index];
        if (index < 232)
        {
            index -= 16;
            int r = index / 36;
            int g = (index % 36) / 6;
            int b = index % 6;
            return TerminalColor.FromRgb(
                (byte)(r > 0 ? 55 + r * 40 : 0),
                (byte)(g > 0 ? 55 + g * 40 : 0),
                (byte)(b > 0 ? 55 + b * 40 : 0));
        }
        int gray = 8 + (index - 232) * 10;
        return TerminalColor.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }

    private void ResetAttributes()
    {
        _fg = TerminalColor.LightGray;
        _bg = default;
        _bold = false;
        _dim = false;
        _italic = false;
        _underline = false;
        _blink = false;
        _reverse = false;
        _invisible = false;
        _strikethrough = false;
    }

    // -----------------------------------------------------------------------
    // Save/restore cursor and alt-screen swap
    // -----------------------------------------------------------------------

    private void SaveCursor()
    {
        _savedCursorCol = _cursorCol;
        _savedCursorRow = _cursorRow;
        _savedFg = _fg; _savedBg = _bg;
        _savedBold = _bold; _savedItalic = _italic; _savedUnderline = _underline; _savedReverse = _reverse;
        _savedStrikethrough = _strikethrough; _savedDim = _dim; _savedBlink = _blink; _savedInvisible = _invisible;
        _savedOriginMode = _originMode;
        _savedAutoWrap = _autoWrap;
        _hasSavedCursor = true;
    }

    private void RestoreCursor()
    {
        if (!_hasSavedCursor) { _cursorCol = 0; _cursorRow = 0; _pendingWrap = false; return; }
        _cursorCol = Math.Clamp(_savedCursorCol, 0, _cols - 1);
        _cursorRow = Math.Clamp(_savedCursorRow, 0, _rows - 1);
        _fg = _savedFg; _bg = _savedBg;
        _bold = _savedBold; _italic = _savedItalic; _underline = _savedUnderline; _reverse = _savedReverse;
        _strikethrough = _savedStrikethrough; _dim = _savedDim; _blink = _savedBlink; _invisible = _savedInvisible;
        _originMode = _savedOriginMode;
        _autoWrap = _savedAutoWrap;
        _pendingWrap = false;
    }

    private void SwitchAltScreen(bool enter, bool saveCursor)
    {
        if (enter)
        {
            if (_altCells != null) return; // already on alt screen
            if (saveCursor) SaveCursor();
            _altSavedScrollTop = _scrollTop;
            _altSavedScrollBottom = _scrollBottom;
            _altCells = _cells;
            var fresh = new TerminalCell[_cols, _rows];
            _cells = fresh;
            // Caller can't see _cells; but our public API hands the grid to
            // the owner ahead of time. Since we mutate _cells pointer here,
            // renderers will see a blank grid (until UpdateGrid is called on
            // exit). For cc-director's usage this is acceptable -- Claude Code
            // and most CLIs do not use the alt-screen (?1049) in practice.
            for (int r = 0; r < _rows; r++)
                for (int c = 0; c < _cols; c++)
                    _cells[c, r] = new TerminalCell();
            _cursorCol = 0;
            _cursorRow = 0;
            _scrollTop = 0;
            _scrollBottom = _rows - 1;
            _pendingWrap = false;
            _committedFrame = null; // repaint-diff is for the primary buffer only (issue #240)
        }
        else
        {
            if (_altCells == null) return;
            _cells = _altCells;
            _altCells = null;
            _scrollTop = _altSavedScrollTop;
            _scrollBottom = Math.Min(_altSavedScrollBottom, _rows - 1);
            if (saveCursor) RestoreCursor();
            _pendingWrap = false;
            _committedFrame = null; // rebaseline after returning to the primary buffer
            _scrollbackCountAtFrame = _scrollback.Count;
        }
    }

    private void FullReset()
    {
        _cursorCol = 0;
        _cursorRow = 0;
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        ResetAttributes();
        _autoWrap = true;
        _originMode = false;
        _cursorVisible = true;
        _hasSavedCursor = false;
        _altCells = null;
        _committedFrame = null; // drop repaint-diff baseline on RIS (issue #240)
        _g0IsDecSpecial = false;
        _g1IsDecSpecial = false;
        _activeCharset = 0;
        _state = State.Ground;
        _params.Clear();
        _currentParam = 0;
        _hasParam = false;
        _intermediates.Clear();
        _privateMarker = '\0';
        _utf8Needed = 0;
        _utf8Len = 0;
        for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                _cells[c, r] = new TerminalCell();
        _scrollback.Clear();
    }
}
