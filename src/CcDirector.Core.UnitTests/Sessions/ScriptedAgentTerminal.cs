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

    public void Write(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        lock (_lock)
        {
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

    /// <summary>Draw the whole screen: a spinner row, then the agent's composer, then its footer.</summary>
    private void Draw()
    {
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

    public Task SendTextAsync(string text) => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;

    public void Dispose()
    {
        _stop.Cancel();
    }
}
