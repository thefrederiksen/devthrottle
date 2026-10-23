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
/// The one exception is a PERSON at the terminal: keystrokes typed or pasted into the desktop terminal itself (the
/// session's raw input with the desktop's typed origin, <see cref="IsAPersonAtTheTerminal"/>), inside
/// <see cref="PersonAtTheTerminal"/>. That person can see the dialog and choose. Until issue #4380 a desktop PASTE went
/// with no origin at all, so it was not counted as the person it is and was dropped.
///
/// A refused write is DROPPED and logged, never thrown: writers run on timer threads where an escaped exception ends
/// the process. Every sender that answers someone opens <see cref="WatchRefusals"/> around its send and answers refused
/// when it fired (issue #4380: every input route answers - the session's raw input, the terminal socket, the driver keys,
/// the phone's Enter and the prompt).
/// </summary>
internal sealed class UsageLimitInputGuard : ISessionBackend
{
    private static readonly AsyncLocal<bool> PersonTyping = new();
    private static readonly AsyncLocal<RefusalWatch?> Watching = new();

    private readonly Func<bool> _dialogShowing;
    private readonly Guid _sessionId;

    public UsageLimitInputGuard(ISessionBackend inner, Guid sessionId, Func<bool> dialogShowing)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sessionId = sessionId;
        _dialogShowing = dialogShowing ?? throw new ArgumentNullException(nameof(dialogShowing));
    }

    public ISessionBackend Inner { get; }

    /// <summary>
    /// THE OWNER'S OPEN QUESTION, AS ONE SWITCH (Terminal Parity mission, section 11.6): should a prompt from the phone or
    /// the Cockpit go through the usage-limit dialog, as a person at the desktop terminal's does? The Architect
    /// recommended yes - it is the owner either way. Until the owner answers it stays false, which is the behaviour before
    /// issue #4380: only the desktop terminal's own keys and pastes pass.
    /// </summary>
    internal static readonly bool PhoneAndCockpitPromptsPassTheDialog = false;

    /// <summary>What a sender is told when this guard dropped its input.</summary>
    internal const string RefusedReason =
        "the session is showing Claude Code's usage-limit dialog, and only Escape reaches it - Enter there confirms a " +
        "highlighted option, one of which turns on paid extra usage. Close it with Escape, then send again";

    /// <summary>
    /// A person at the terminal: input typed - or pasted - into the desktop terminal itself, which can see the dialog.
    /// Its keys pass the dialog (<see cref="PersonAtTheTerminal"/>).
    /// </summary>
    internal static bool IsAPersonAtTheTerminal(InputOrigin? origin) =>
        origin is { Modality: InputModality.Typed, Surface: InputSurface.Desktop };

    /// <summary>Whether a prompt from <paramref name="origin"/> passes the dialog: one from the phone or the Cockpit, and
    /// only while <see cref="PhoneAndCockpitPromptsPassTheDialog"/> is on.</summary>
    internal static bool PromptPassesTheDialog(InputOrigin? origin) =>
        PhoneAndCockpitPromptsPassTheDialog && origin is { Surface: InputSurface.Phone or InputSurface.Cockpit };

    /// <summary>Marks the writes inside the scope as a person's own keystrokes at the terminal.</summary>
    public static IDisposable PersonAtTheTerminal()
    {
        var was = PersonTyping.Value;
        PersonTyping.Value = true;
        return new Restore(was);
    }

    /// <summary>
    /// Counts the writes this guard refuses inside the scope - the caller's own send and everything it awaits - so the
    /// caller can tell a refused send from a delivered one (fix-round review of pull request 3863).
    /// </summary>
    public static RefusalWatch WatchRefusals()
    {
        var watch = new RefusalWatch(Watching.Value);
        Watching.Value = watch;
        return watch;
    }

    public int ProcessId => Inner.ProcessId;
    public string Status => Inner.Status;
    public bool IsRunning => Inner.IsRunning;
    public bool HasExited => Inner.HasExited;
    public CircularTerminalBuffer? Buffer => Inner.Buffer;
    public string WorkingDirectory => Inner.WorkingDirectory;
    public string PromptFileDirectory => Inner.PromptFileDirectory;
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
        Watching.Value?.Count();
        return false;
    }

    /// <summary>The refusals counted by one <see cref="WatchRefusals"/> scope. Disposing it ends the scope.</summary>
    internal sealed class RefusalWatch : IDisposable
    {
        private readonly RefusalWatch? _outer;
        private int _refused;

        public RefusalWatch(RefusalWatch? outer) => _outer = outer;

        /// <summary>True when at least one write was refused inside the scope.</summary>
        public bool Refused => Volatile.Read(ref _refused) > 0;

        internal void Count()
        {
            Interlocked.Increment(ref _refused);
            _outer?.Count();
        }

        public void Dispose() => Watching.Value = _outer;
    }

    private sealed class Restore : IDisposable
    {
        private readonly bool _was;
        public Restore(bool was) => _was = was;
        public void Dispose() => PersonTyping.Value = _was;
    }
}
