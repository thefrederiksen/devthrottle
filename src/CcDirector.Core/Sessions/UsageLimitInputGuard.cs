using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The License Switching mission, trap 4 (contract rule 6.3): the one door every keystroke the Director sends a session
/// goes through. While the session's screen shows Claude Code's usage-limit dialog (<see cref="UsageLimitDialog"/>),
/// nothing the Director does on its own reaches it - not a prompt from the Gateway or the phone, not a fleet message,
/// not the doorbell, not a queue drain, not a driver's nudge or Enter. Only Escape passes.
///
/// It wraps the session's backend once, when the session is made, so every writer inside the session - its own sends
/// and every driver it hands its backend to - is behind it, and a writer added later is behind it too without anyone
/// remembering to check.
///
/// The one exception is a PERSON at the terminal: keystrokes typed into the terminal itself (the session's raw input
/// with a typed origin), inside <see cref="PersonAtTheTerminal"/>. That person can see the dialog and choose.
///
/// A refused write is DROPPED and logged, never thrown: writers run on timer threads where an escaped exception ends
/// the process, and every sender that matters already verifies its own submit and reports the failure as its own.
/// </summary>
internal sealed class UsageLimitInputGuard : ISessionBackend
{
    private static readonly AsyncLocal<bool> PersonTyping = new();

    private readonly Func<bool> _dialogShowing;
    private readonly Guid _sessionId;

    public UsageLimitInputGuard(ISessionBackend inner, Guid sessionId, Func<bool> dialogShowing)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sessionId = sessionId;
        _dialogShowing = dialogShowing ?? throw new ArgumentNullException(nameof(dialogShowing));
    }

    public ISessionBackend Inner { get; }

    /// <summary>Marks the writes inside the scope as a person's own keystrokes at the terminal.</summary>
    public static IDisposable PersonAtTheTerminal()
    {
        var was = PersonTyping.Value;
        PersonTyping.Value = true;
        return new Restore(was);
    }

    public int ProcessId => Inner.ProcessId;
    public string Status => Inner.Status;
    public bool IsRunning => Inner.IsRunning;
    public bool HasExited => Inner.HasExited;
    public CircularTerminalBuffer? Buffer => Inner.Buffer;
    public string WorkingDirectory => Inner.WorkingDirectory;
    public string? LastShutdownFailure => Inner.LastShutdownFailure;

    public event Action<string>? StatusChanged
    {
        add => Inner.StatusChanged += value;
        remove => Inner.StatusChanged -= value;
    }

    public event Action<int>? ProcessExited
    {
        add => Inner.ProcessExited += value;
        remove => Inner.ProcessExited -= value;
    }

    public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null)
        => Inner.Start(executable, args, workingDir, cols, rows, environmentVars);

    public void Write(byte[] data)
    {
        if (!MayPass(data.Length == 1 && data[0] == 0x1B, $"{data.Length} byte(s), first 0x{(data.Length > 0 ? data[0].ToString("X2") : "00")}")) return;
        Inner.Write(data);
    }

    public Task SendTextAsync(string text)
    {
        if (!MayPass(isEscapeOnly: false, $"text of {text.Length} character(s)")) return Task.CompletedTask;
        return Inner.SendTextAsync(text);
    }

    public Task SendEnterAsync()
    {
        if (!MayPass(isEscapeOnly: false, "Enter")) return Task.CompletedTask;
        return Inner.SendEnterAsync();
    }

    public void Resize(short cols, short rows) => Inner.Resize(cols, rows);

    public Task GracefulShutdownAsync(int timeoutMs = 5000) => Inner.GracefulShutdownAsync(timeoutMs);

    public void Dispose() => Inner.Dispose();

    private bool MayPass(bool isEscapeOnly, string what)
    {
        if (isEscapeOnly || PersonTyping.Value || !_dialogShowing()) return true;
        FileLog.Write($"[UsageLimitInputGuard] session={_sessionId}: the usage-limit dialog is showing; REFUSED automatic input ({what}). Only Escape reaches it.");
        return false;
    }

    private sealed class Restore : IDisposable
    {
        private readonly bool _was;
        public Restore(bool was) => _was = was;
        public void Dispose() => PersonTyping.Value = _was;
    }
}
