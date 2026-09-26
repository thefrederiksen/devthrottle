using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// A scripted agent terminal. It keeps a composer the way Claude Code and Codex do - typed characters append, a
/// bracketed paste folds into "[Pasted text #1 +N lines]", Backspace removes one character (or the whole paste), Enter
/// submits and empties it - and draws the screen those agents draw, so the Director's own composer reader reads it.
/// While <see cref="Working"/> it redraws a spinner every 30 milliseconds and never goes quiet. An accepted Enter writes
/// the prompt to the conversation file the way Claude Code does: a queue "enqueue" line while working, a user line when
/// idle.
/// </summary>
internal sealed class ScriptedAgentTerminal : ISessionBackend
{
    private readonly object _lock = new();
    private readonly AgentKind _agent;
    private readonly string _transcript;
    private readonly StringBuilder _composer = new();
    private readonly StringBuilder _typed = new();
    private readonly StringBuilder _paste = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _pasteHeld;
    private bool _inPaste;
    private int _frame;

    public ScriptedAgentTerminal(AgentKind agent, string transcript, string workingDirectory)
    {
        _agent = agent;
        _transcript = transcript;
        WorkingDirectory = workingDirectory;
    }

    public bool Working { get; set; }
    public bool SwallowEnter { get; set; }
    public TimeSpan PasteDrawDelay { get; set; } = TimeSpan.Zero;
    public TimeSpan SpinnerInterval { get; set; } = TimeSpan.FromMilliseconds(30);

    /// <summary>After the first Enter, draw a menu where the composer is (review finding 1): its hint in the footer,
    /// the composer's text still underneath it.</summary>
    public bool MenuAfterEnter { get; set; }

    /// <summary>For this long after the first Enter, draw frames the composer reader cannot read (review finding 2): the
    /// composer row without its prompt glyph, as a frame caught mid-repaint can look.</summary>
    public TimeSpan UnreadableAfterEnter { get; set; } = TimeSpan.Zero;

    /// <summary>How long after an accepted Enter the prompt reaches the conversation file (review finding 3): a working
    /// Claude Code holds a prompt sent mid-turn and writes it when its running tool ends.</summary>
    public TimeSpan RecordDelay { get; set; } = TimeSpan.Zero;

    /// <summary>An accepted Enter empties the composer, but the prompt never reaches the conversation file.</summary>
    public bool NeverRecord { get; set; }

    /// <summary>How long the agent takes to draw typed characters. Until then it answers a write with one invisible
    /// cursor sequence, so the terminal has reacted but shows nothing - a working agent that is behind on its input.</summary>
    public TimeSpan TypedDrawDelay { get; set; } = TimeSpan.Zero;
    private DateTime _drawTypedFrom = DateTime.MinValue;
    public int EntersAccepted { get; private set; }
    public int ClearKeysPressed { get; private set; }
    public DateTime? PasteDrawnAt { get; private set; }
    public DateTime? FirstEnterAt { get; private set; }
    public List<string> Recorded { get; } = new();

    /// <summary>
    /// Paint the way the Windows pseudo console hands a Claude Code screen to the Director (issue #3406): the frame is
    /// anchored to the bottom of a terminal of <see cref="Width"/> by <see cref="Height"/>; the first paint writes the
    /// rows in sequence and lets a full-width row (a rule) wrap by itself, with no line break after it; every later
    /// paint rewrites only the rows that changed, each at its absolute position. A reader that renders at the real size
    /// sees exactly the frame; a reader at any other size sees rows run together and rows left over from earlier frames.
    /// </summary>
    public bool ConPtyPaint { get; set; }

    /// <summary>Backspace does nothing, so a clear cannot empty the composer.</summary>
    public bool IgnoreClearKeys { get; set; }
    public int Width { get; set; } = 120;
    public int Height { get; set; } = 30;

    /// <summary>
    /// Lines the agent paints ABOVE its spinner and composer, the way Claude Code paints a transcript: each accepted
    /// prompt as "❯ <text>" (a past prompt in the transcript carries the same glyph as the live composer) followed by a
    /// one-line answer. Drawn only while <see cref="ShowTranscript"/> is true, and clipped to the screen: when the
    /// transcript outgrows the terminal the oldest lines scroll off the top, exactly as the real terminal does.
    /// </summary>
    public bool ShowTranscript { get; set; }
    public List<string> TranscriptLines { get; } = new();

    /// <summary>
    /// A line added to the transcript on the NEXT paint - the previous turn's answer landing while the next prompt is
    /// being typed. It pushes the whole transcript down, so the oldest visible line scrolls off the top of the screen
    /// at the same moment the composer draws the new text (Voice Delivery mission, phase 6: the echo check used to
    /// count copies of the text anywhere on screen, and a copy scrolling off cancelled the new one arriving).
    /// </summary>
    public string? TranscriptLineOnNextPaint { get; set; }
    private string[]? _painted;

    public string Composer { get { lock (_lock) return ComposerShown(); } }
    public string TypedText { get { lock (_lock) return _typed.ToString(); } }

    public int ProcessId => 0;
    public string Status => "scripted";
    public bool IsRunning => true;
    public bool HasExited => false;
    public string WorkingDirectory { get; }
    public CircularTerminalBuffer? Buffer { get; } = new(1 << 20);
#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }

    public void SetComposer(string text) { lock (_lock) { _composer.Clear(); _composer.Append(text); } }

    /// <summary>Turn on bracketed paste as the real agents do, draw the first frame, and start the spinner.</summary>
    public void StartDrawing()
    {
        Buffer!.Write(Encoding.UTF8.GetBytes("\x1b[?2004h"));
        Draw();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(SpinnerInterval);
                if (Working) Draw();
            }
        });
    }

    /// <summary>
    /// Characters an earlier send typed that the agent has not read yet - an agent starved of processor time, or frozen,
    /// reads its input late. They stay unread (nothing drawn) until the next write reaches the terminal, and are read
    /// first, ahead of it: a terminal's input is read in the order it was written.
    /// </summary>
    public void HoldUnreadInput(string text) { lock (_lock) _unread = text; }
    private string? _unread;

    public void Write(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        lock (_lock)
        {
            if (_unread is not null) { text = _unread + text; _unread = null; }
            for (var i = 0; i < text.Length; i++)
            {
                if (text.AsSpan(i).StartsWith("\x1b[200~")) { _inPaste = true; _paste.Clear(); i += 5; continue; }
                if (text.AsSpan(i).StartsWith("\x1b[201~")) { _inPaste = false; i += 5; EndPaste(); continue; }
                var ch = text[i];
                if (_inPaste) { _paste.Append(ch); continue; }
                if (ch == '\r') { Enter(); continue; }
                if (ch is '\x7f' or '\b')
                {
                    ClearKeysPressed++;
                    if (IgnoreClearKeys) continue;
                    if (_pasteHeld is not null) _pasteHeld = null;
                    else if (_composer.Length > 0) _composer.Length--;
                    continue;
                }
                if (ch < ' ') { if (ch is '\x05' or '\x15') ClearKeysPressed++; continue; }
                _composer.Append(ch);
                _typed.Append(ch);
            }
        }
        if (TypedDrawDelay > TimeSpan.Zero && !text.Contains('\r') && !text.Contains('\x1b'))
        {
            if (_drawTypedFrom == DateTime.MinValue) _drawTypedFrom = DateTime.UtcNow + TypedDrawDelay;
            if (DateTime.UtcNow < _drawTypedFrom)
            {
                Buffer!.Write(Encoding.UTF8.GetBytes("\x1b[?25h"));
                var wait = _drawTypedFrom - DateTime.UtcNow;
                _ = Task.Run(async () => { await Task.Delay(wait); Draw(); });
                return;
            }
        }
        Draw();
    }

    private void EndPaste()
    {
        var pasted = _paste.ToString();
        _typed.Append(pasted);
        if (PasteDrawDelay <= TimeSpan.Zero)
        {
            _pasteHeld = pasted;
            PasteDrawnAt = DateTime.UtcNow;
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(PasteDrawDelay);
            lock (_lock) { _pasteHeld = pasted; PasteDrawnAt = DateTime.UtcNow; }
            Draw();
        });
    }

    private void Enter()
    {
        FirstEnterAt ??= DateTime.UtcNow;
        if (SwallowEnter) return;
        var submitted = _pasteHeld is not null ? _composer + _pasteHeld : _composer.ToString();
        if (submitted.Length == 0) return;
        EntersAccepted++;
        _composer.Clear();
        _pasteHeld = null;
        if (ShowTranscript)
        {
            TranscriptLines.Add("❯ " + submitted);
            TranscriptLines.Add("OK");
        }
        var line = Working
            ? JsonSerializer.Serialize(new { type = "queue-operation", operation = "enqueue", content = submitted })
            : JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = submitted } });
        if (NeverRecord) return;
        if (RecordDelay <= TimeSpan.Zero)
        {
            Recorded.Add(submitted);
            File.AppendAllText(_transcript, line + "\n");
            return;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(RecordDelay);
            lock (_lock)
            {
                Recorded.Add(submitted);
                File.AppendAllText(_transcript, line + "\n");
            }
        });
    }

    private string ComposerShown() =>
        _pasteHeld is null ? _composer.ToString() : _composer + $"[Pasted text #1 +{_pasteHeld.Count(c => c == '\n')} lines]";

    /// <summary>Paint the current screen again, as the agent does when its state changes on its own.</summary>
    public void Redraw() => Draw();

    /// <summary>Paint every row again in sequence, as the pseudo console does after the terminal is resized.</summary>
    public void RepaintAll()
    {
        lock (_lock) _painted = null;
        Draw();
    }

    /// <summary>Draw the whole screen: a spinner row, then the agent's composer, then its footer.</summary>
    private void Draw()
    {
        if (ConPtyPaint) { PaintLikeConPty(); return; }
        string frame;
        lock (_lock)
        {
            _frame++;
            var shown = DateTime.UtcNow < _drawTypedFrom ? "" : ComposerShown();
            var spinner = Working ? $"* Working... ({_frame})" : "Done.";
            var afterEnter = FirstEnterAt is not null;
            var footer = afterEnter && MenuAfterEnter ? "  enter to select - esc to go back"
                : Working ? "  esc to interrupt" : "  ? for shortcuts";
            // A frame the composer reader cannot read: the composer row without its prompt glyph.
            var glyphless = afterEnter && DateTime.UtcNow < FirstEnterAt!.Value + UnreadableAfterEnter;
            var rule = new string('─', 80);
            var sb = new StringBuilder("\x1b[2J\x1b[H");
            if (_agent == AgentKind.Codex)
            {
                sb.Append(spinner).Append("\r\n\r\n").Append(glyphless ? "  " : "› ").Append(shown).Append("\r\n\r\n").Append(footer);
                sb.Append($"\x1b[3;{3 + shown.Length}H");
            }
            else
            {
                sb.Append(spinner).Append("\r\n").Append(rule).Append("\r\n").Append(glyphless ? "  " : "❯ ").Append(shown)
                    .Append("\r\n").Append(rule).Append("\r\n").Append(footer);
                sb.Append($"\x1b[3;{3 + shown.Length}H");
            }
            frame = sb.ToString();
        }
        Buffer!.Write(Encoding.UTF8.GetBytes(frame));
    }

    private void PaintLikeConPty()
    {
        string frame;
        lock (_lock)
        {
            if (TranscriptLineOnNextPaint is { } pending)
            {
                TranscriptLines.Add(pending);
                TranscriptLineOnNextPaint = null;
            }
            _frame++;
            var shown = DateTime.UtcNow < _drawTypedFrom ? "" : ComposerShown();
            var rule = new string('─', Width);
            var painted = new[]
            {
                Working ? $"* Working... ({_frame})" : "Done.",
                rule,
                "❯ " + shown,
                rule,
                Working ? "  esc to interrupt" : "  ? for shortcuts",
            };
            // The transcript sits above the frame, and the whole screen is clipped to the terminal: a transcript that
            // outgrows the screen scrolls its oldest lines off the top, as a real terminal does.
            var rows = (ShowTranscript ? TranscriptLines : []).Concat(painted).ToList();
            if (rows.Count > Height) rows = rows[^Height..];
            var top = Height - rows.Count;
            var composerRow = top + rows.Count - 3;
            var sb = new StringBuilder();
            // A clip window that moved (the transcript grew) shifts every row, so the previous frame's rows no longer
            // line up with these: paint everything again, as the terminal does when its content scrolls.
            if (_painted is not null && _painted.Length != rows.Count) _painted = null;
            if (_painted is null)
            {
                sb.Append($"\x1b[{top + 1};1H");
                for (var i = 0; i < rows.Count; i++)
                {
                    sb.Append(rows[i]);
                    if (rows[i].Length >= Width) continue; // a full row wraps by itself: no line break is sent
                    sb.Append("\x1b[K");
                    if (i < rows.Count - 1) sb.Append("\r\n");
                }
            }
            else
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i] == _painted[i]) continue;
                    sb.Append($"\x1b[{top + i + 1};1H").Append(rows[i]);
                    if (rows[i].Length < Width) sb.Append("\x1b[K");
                }
            }
            _painted = rows.ToArray();
            sb.Append($"\x1b[{composerRow + 1};{3 + shown.Length}H");
            frame = sb.ToString();
        }
        Buffer!.Write(Encoding.UTF8.GetBytes(frame));
    }

    public Task SendTextAsync(string text) => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;

    public void Dispose()
    {
        _stop.Cancel();
    }
}
