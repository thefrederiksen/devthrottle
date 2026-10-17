using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A terminal session over a scripted terminal: it keeps a composer the way an agent's prompt box does - printable
/// characters append, Backspace removes one, Enter submits the line and empties it, an escape sequence (a cursor key,
/// a focus or mouse report, a bracketed-paste marker, an encoded Enter) puts nothing in it - answers an Enter with a
/// turn's worth of output, and runs <see cref="OnWrite"/> as each write lands, which is how a test puts the owner's
/// keystroke in the middle of the send.
///
/// IT DRAWS CLAUDE CODE'S COMPOSER ON ITS SCREEN (the Prompt Delivery mission, 16 October 2026). A send looks before it
/// types: through the Session's own parser it reads the composer, and refuses when the screen shows no composer, a menu,
/// or text the send cannot account for. So after every write this terminal redraws what Claude Code draws - a rule, the
/// prompt row holding the composer, a rule, the footer - with the cursor straight after the composer's text, exactly as
/// <c>LookBeforeTypingTests</c> draws it in Core.Tests. Unless told not to echo: then the model changes and the screen
/// does not, which is a composer that does not echo.
/// </summary>
internal sealed class ScriptedTerminal : ISessionBackend
{
    /// <summary>Shorter than the Session's 120-column grid, so no drawn row wraps and moves the prompt row.</summary>
    private const int RuleWidth = 100;
    private const int TranscriptWidth = 90;

    public List<string> Writes { get; } = new();
    public Action<string>? OnWrite { get; set; }
    public bool Echo { get; set; } = true;
    public StringBuilder Composer { get; } = new();
    public List<string> Submitted { get; } = new();
    private readonly List<string> _transcript = new();
    private bool _inEscape;
    private bool _csi;
    private bool _ss3;
    public int ProcessId => 0;
    public string Status => "scripted";
    public bool IsRunning => true;
    public bool HasExited => false;
    public CircularTerminalBuffer? Buffer { get; } = new(1 << 16);
#pragma warning disable CS0067
    public event Action<string>? StatusChanged;
    public event Action<int>? ProcessExited;
#pragma warning restore CS0067
    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }

    public void Write(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        Writes.Add(text);
        foreach (var ch in text)
        {
            if (_inEscape)
            {
                ConsumeEscape(ch);
            }
            else if (ch == '\x1b')
            {
                _inEscape = true;
                _csi = false;
                _ss3 = false;
            }
            else if (ch == '\r')
            {
                var line = Composer.ToString();
                Submitted.Add(line);
                Composer.Clear();
                _transcript.Add(Clip("> " + line));
                _transcript.Add(Clip("● " + new string('.', 60)));
            }
            else if (ch == '\x7f')
            {
                if (Composer.Length > 0) Composer.Length--;
            }
            else if (ch >= ' ')
            {
                Composer.Append(ch);
            }
        }
        // An Enter starts a turn: the agent streams well past what the submit check waits for.
        if (text == "\r") Buffer!.Write(Encoding.UTF8.GetBytes(new string('.', 4096)));
        if (Echo) Redraw();
        OnWrite?.Invoke(text);
    }

    /// <summary>One character of an escape sequence: a CSI sequence ends at its final byte, an SS3 one after one
    /// character, and any other escape is ESC plus the one character that follows it.</summary>
    private void ConsumeEscape(char ch)
    {
        if (_csi)
        {
            if (ch >= '\x40' && ch <= '\x7e') _inEscape = false;
            return;
        }
        if (_ss3) { _inEscape = false; return; }
        if (ch == '[') { _csi = true; return; }
        if (ch == 'O') { _ss3 = true; return; }
        _inEscape = false;
    }

    private static string Clip(string row) => row.Length <= TranscriptWidth ? row : row[..TranscriptWidth];

    /// <summary>Put text in the composer as if it had always been there - text the Director's own keystroke tracking
    /// never saw - and show it on the screen.</summary>
    public void ShowInComposer(string text)
    {
        Composer.Append(text);
        Redraw();
    }

    /// <summary>Empty the composer the way the agent does when it consumes what was typed (a menu answer, a cursor
    /// key), and show it empty.</summary>
    public void ClearComposer()
    {
        Composer.Clear();
        Redraw();
    }

    /// <summary>
    /// Draw the screen as Claude Code draws it: the last two transcript rows, a rule, the prompt row with the composer,
    /// a rule, the footer, and the cursor on the prompt row straight after the composer's text (column 3 when empty:
    /// the glyph, its separator, the cursor). The prompt row is always the fourth row.
    /// </summary>
    public void Redraw()
    {
        var rule = new string('─', RuleWidth);
        var lines = _transcript.Count switch
        {
            0 => ("", ""),
            1 => ("", _transcript[0]),
            _ => (_transcript[^2], _transcript[^1]),
        };
        var body = lines.Item1 + "\r\n" + lines.Item2 + "\r\n" + rule + "\r\n❯ " + Composer + "\r\n" + rule +
                   "\r\n  ⏵⏵ bypass permissions on (shift+tab to cycle)" +
                   $"\x1b[4;{3 + Composer.Length}H";
        Buffer!.Write(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H" + body));
    }

    public Task SendTextAsync(string text) => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
    public void Dispose() { }

    /// <summary>A terminal session over a new scripted terminal, waiting for a prompt after one turn, its empty
    /// composer drawn.</summary>
    public static (Session Session, ScriptedTerminal Terminal) NewWaitingSession()
    {
        var terminal = new ScriptedTerminal();
        var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, terminal, SessionBackendType.ConPty);
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        terminal.Redraw();
        return (session, terminal);
    }

    /// <summary>The owner typing at the session's desktop terminal.</summary>
    public static void OwnerTypes(Session session, string keys) =>
        session.SendInput(Encoding.UTF8.GetBytes(keys), InputOrigin.DesktopTyped, SessionTestDoors.TestDoor);
}
