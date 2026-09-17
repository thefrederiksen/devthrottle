using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Thrown by <see cref="InputGuardedBackend"/> when other input reached the session after the send it guards made its
/// check. Nothing more of that send is written.
/// </summary>
public sealed class InputSupersededException : InvalidOperationException
{
    public InputSupersededException(string message) : base(message) { }
}

/// <summary>
/// The terminal a send that may type ONLY while nobody else is typing writes through (the Fleet Manager's events,
/// step 4). Every write - the text, each chunk of it, an Escape over a retained composer, the Enter, a nudge - is made
/// under the session's input lock, the same lock every other input to the session takes, and only if no other input
/// has reached the session since the check. Otherwise it throws <see cref="InputSupersededException"/> and writes
/// nothing. So the owner's keystroke and this send's next byte are ordered, and a send the owner overtook stops
/// before its Enter.
/// </summary>
internal sealed class InputGuardedBackend : ISessionBackend
{
    private readonly Session _session;
    private readonly long _checkedGeneration;

    public InputGuardedBackend(ISessionBackend inner, Session session, long checkedGeneration)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _checkedGeneration = checkedGeneration;
    }

    /// <summary>The session's own terminal. State kept per terminal (the retained composer) is kept against it.</summary>
    public ISessionBackend Inner { get; }

    /// <summary>Whether any byte of this send reached the terminal.</summary>
    public bool WroteAny { get; private set; }

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
        => throw new InvalidOperationException("[InputGuardedBackend] a guarded send never starts a process");

    public void Write(byte[] data)
    {
        _session.WriteIfNoInputSince(_checkedGeneration, () => Inner.Write(data));
        WroteAny = true;
    }

    public async Task SendTextAsync(string text)
    {
        // A backend that submits in one call is checked once, under the lock, as the call begins.
        Task? sending = null;
        _session.WriteIfNoInputSince(_checkedGeneration, () => sending = Inner.SendTextAsync(text));
        WroteAny = true;
        await sending!;
    }

    public async Task SendEnterAsync()
    {
        Task? sending = null;
        _session.WriteIfNoInputSince(_checkedGeneration, () => sending = Inner.SendEnterAsync());
        WroteAny = true;
        await sending!;
    }

    public void Resize(short cols, short rows) => Inner.Resize(cols, rows);

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
        => throw new InvalidOperationException("[InputGuardedBackend] a guarded send never shuts the terminal down");

    /// <summary>The session owns the terminal; a guarded send only borrows it.</summary>
    public void Dispose() { }
}

/// <summary>How <see cref="Session.SendTextOnlyWhenWaitingForInputAsync"/> ended.</summary>
public sealed record GuardedSendResult(bool Accepted, bool RefusedBusy, bool Exited, ActivityState ActivityState, string? Reason)
{
    public static GuardedSendResult Sent(ActivityState state) => new(true, false, false, state, null);

    /// <summary>The session was not waiting for a prompt, or other input reached it before the send finished.</summary>
    public static GuardedSendResult Busy(ActivityState state, string reason) => new(false, true, false, state, reason);

    public static GuardedSendResult NotRunning(ActivityState state) => new(false, false, true, state, "session has exited");
}
