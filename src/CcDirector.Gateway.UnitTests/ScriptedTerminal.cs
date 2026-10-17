using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A terminal session over a scripted terminal: it keeps a composer the way an agent's prompt box does - characters
/// append, Backspace removes one, Enter submits the line and empties it - draws it on screen the way the agent does
/// (unless told not to echo), answers an Enter with a turn's worth of output, and runs <see cref="OnWrite"/> as each
/// write lands, which is how a test puts the owner's keystroke in the middle of the send.
///
/// THE SCREEN IS THE AGENT'S. A send looks before it types (pull request 4072): it reads the composer between the
/// agent's rules and refuses when none is drawn, so a terminal that only echoed raw keys would have every send refused.
/// Text put straight into <see cref="Composer"/> is not drawn until the next write or <see cref="ClearComposer"/> -
/// that is how a test holds text the screen does not show.
/// </summary>
internal sealed class ScriptedTerminal : ISessionBackend
{
    public List<string> Writes { get; } = new();
    public Action<string>? OnWrite { get; set; }
    public bool Echo { get; set; } = true;
    public StringBuilder Composer { get; } = new();
    public List<string> Submitted { get; } = new();
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
            if (ch == '\r')
            {
                Submitted.Add(Composer.ToString());
                Composer.Clear();
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
        if (Echo) Buffer!.Write(data);
        // An Enter starts a turn: the agent streams well past what the submit check waits for.
        if (text == "\r") Buffer!.Write(Encoding.UTF8.GetBytes(new string('.', 4096)));
        if (Echo) Redraw();
        OnWrite?.Invoke(text);
    }

    /// <summary>Empty the composer and draw it empty, as the agent does when its prompt box is cleared.</summary>
    public void ClearComposer()
    {
        Composer.Clear();
        Redraw();
    }

    /// <summary>
    /// Draw the agent's screen: a line of output, the composer between its two rules, and the cursor left in the
    /// composer straight after what it holds - the shape the Director's composer reader locates.
    /// </summary>
    private void Redraw()
    {
        var rule = new string('─', 80);
        var frame = "\x1b[2J\x1b[H● Done.\r\n\r\n" + rule + "\r\n❯ " + Composer + "\r\n" + rule +
                    "\r\n  ⏵⏵ bypass permissions on (shift+tab to cycle)" + $"\x1b[4;{3 + Composer.Length}H";
        Buffer!.Write(Encoding.UTF8.GetBytes(frame));
    }

    public Task SendTextAsync(string text) => Task.CompletedTask;
    public void Resize(short cols, short rows) { }
    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
    public void Dispose() { }

    /// <summary>A terminal session over a new scripted terminal, waiting for a prompt after one turn.</summary>
    public static (Session Session, ScriptedTerminal Terminal) NewWaitingSession()
    {
        var terminal = new ScriptedTerminal();
        var session = new Session(Guid.NewGuid(), Path.GetTempPath(), Path.GetTempPath(), null, terminal, SessionBackendType.ConPty);
        // Drawn once the session is attached, so its screen holds the agent's empty composer from the start.
        terminal.Redraw();
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        return (session, terminal);
    }

    /// <summary>The owner typing at the session's desktop terminal.</summary>
    public static void OwnerTypes(Session session, string keys) =>
        session.SendInput(Encoding.UTF8.GetBytes(keys), InputOrigin.DesktopTyped, SessionTestDoors.TestDoor);
}
