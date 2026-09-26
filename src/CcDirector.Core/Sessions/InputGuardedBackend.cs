using System.Globalization;
using System.Text;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;

namespace CcDirector.Core.Sessions;

/// <summary>
/// Thrown to a guarded send's own submit when its input section was abandoned (it ran past
/// <see cref="Session.GuardedSendLimit"/>, or it tried to do something a guarded send never does). The text it typed
/// has already been taken back out of the composer; nothing more of the send is written.
/// </summary>
public sealed class GuardedSendAbandonedException : InvalidOperationException
{
    public GuardedSendAbandonedException(string message) : base(message) { }
}

/// <summary>
/// One guarded send's hold on the session's input (the Fleet Manager's events, step 4). While it is open, the session
/// writes only this send's bytes: the owner's keystrokes are held and written, in order, when it closes, and every
/// other input waits for it. It closes when the send's submitting Enter is written, or when the send is abandoned -
/// and an abandoned section first takes back out of the composer the text this send typed. Every field is read and
/// written under the session's input lock.
/// </summary>
internal sealed class GuardedInputSection
{
    public GuardedInputSection(DateTime deadlineUtc) => DeadlineUtc = deadlineUtc;

    /// <summary>When the section is abandoned if it has not closed.</summary>
    public DateTime DeadlineUtc { get; }

    /// <summary>True until the Enter is written or the section is abandoned.</summary>
    public bool Open { get; set; } = true;

    /// <summary>Set when the section closed without the send's Enter; the reason says why.</summary>
    public string? AbandonReason { get; set; }

    /// <summary>The input count when the section closed, so a later nudge can tell whether the owner has typed since.</summary>
    public long GenerationAtClose { get; set; }

    /// <summary>The printable bytes this send put in the composer, so an abandoned send can remove exactly them.</summary>
    public MemoryStream Typed { get; } = new();

    /// <summary>Completes when the section closes, either way. Input waiting for the section waits on this.</summary>
    public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes only if the section is abandoned, so the send can answer at once.</summary>
    public TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Stops the deadline timer once the section closes.</summary>
    public CancellationTokenSource DeadlineTimer { get; } = new();

    /// <summary>
    /// How many Backspace keys remove what this send typed: one per character as the composer shows it. A guarded
    /// send never pastes (a paste can collapse into one placeholder of unknown width), so every character it put
    /// there was typed and is removed by one Backspace.
    /// </summary>
    public int TypedCharacters() =>
        new StringInfo(Encoding.UTF8.GetString(Typed.ToArray())).LengthInTextElements;

    /// <summary>Record the printable part of one write: escape sequences and control bytes put no character in the composer.</summary>
    public void RecordTyped(byte[] data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == 0x1B)
            {
                // Skip a whole escape sequence: ESC [ parameters final-byte, or ESC and one byte.
                i++;
                if (i < data.Length && data[i] == (byte)'[')
                {
                    i++;
                    while (i < data.Length && (data[i] < 0x40 || data[i] > 0x7E)) i++;
                }
                continue;
            }
            if (b >= 0x20 && b != 0x7F) Typed.WriteByte(b);
        }
    }
}

/// <summary>
/// The terminal a guarded send writes through. Every write goes to the session, which writes it only while this
/// send's input section is open (<see cref="Session.WriteInGuardedSection"/>). The section closes as the submitting
/// Enter is written; after that the only writes a submit makes are the submit check's nudges, and the session makes
/// those only if nobody has typed since, because a nudge over the owner's own typing would submit their words.
/// </summary>
internal sealed class InputGuardedBackend : ISessionBackend
{
    private readonly Session _session;
    private readonly GuardedInputSection _section;

    public InputGuardedBackend(ISessionBackend inner, Session session, GuardedInputSection section)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _section = section ?? throw new ArgumentNullException(nameof(section));
    }

    /// <summary>The session's own terminal. State kept per terminal (the retained composer) is kept against it.</summary>
    public ISessionBackend Inner { get; }

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

    public void Write(byte[] data) => _session.WriteInGuardedSection(_section, data);

    // A one-call submit cannot be bounded: once the call has started it cannot be taken back, so the owner's held keys
    // would wait for as long as it runs. A guarded send is never made to a session whose terminal submits that way
    // (Session.SendTextOnlyWhenWaitingForInputAsync refuses it first), and the terminal submit writes a terminal
    // session byte by byte. These refuse before anything is sent, so the send is abandoned with nothing typed.
    public Task SendTextAsync(string text)
        => throw new GuardedSendAbandonedException("[InputGuardedBackend] a guarded send never submits in one call; nothing was sent");

    public Task SendEnterAsync()
        => throw new GuardedSendAbandonedException("[InputGuardedBackend] a guarded send never submits in one call; nothing was sent");

    public void Resize(short cols, short rows) => Inner.Resize(cols, rows);

    public Task GracefulShutdownAsync(int timeoutMs = 5000)
        => throw new InvalidOperationException("[InputGuardedBackend] a guarded send never shuts the terminal down");

    /// <summary>The session owns the terminal; a guarded send only borrows it.</summary>
    public void Dispose() { }
}

/// <summary>How <see cref="Session.SendTextOnlyWhenWaitingForInputAsync"/> ended.</summary>
public sealed record GuardedSendResult(bool Accepted, bool RefusedBusy, bool Exited, ActivityState ActivityState, string? Reason)
{
    /// <summary>Which fixed refusal this was, or null for the others.</summary>
    public PromptRefusal? RefusedFor { get; init; }

    /// <summary>True when nothing was typed because the prompt was strictly older than the delivery age limit (phase
    /// 5, QA finding F6). Not a busy refusal: the sender does not wait and try again, it answers the delivery
    /// not-delivered with the too-old reason.</summary>
    public bool RefusedTooOld { get; init; }

    public static GuardedSendResult Sent(ActivityState state) => new(true, false, false, state, null);

    /// <summary>The session was not waiting for a prompt, or the send was abandoned and its text taken back out.</summary>
    public static GuardedSendResult Busy(ActivityState state, string reason) => new(false, true, false, state, reason);

    /// <summary>The owner has unsent text in the composer; nothing was typed.</summary>
    public static GuardedSendResult OwnerDraft(ActivityState state) => new(false, true, false, state,
        "the owner has unsent text in the composer; nothing was typed, so the owner's words are never sent with this text")
    { RefusedFor = PromptRefusal.OwnerDraft };

    /// <summary>The session's terminal submits a whole turn in one call; a guarded send is never made to it.</summary>
    public static GuardedSendResult OneCallSubmit(ActivityState state, string backend) => new(false, true, false, state,
        $"the session's {backend} terminal submits a whole turn in one call, which cannot be taken back once started, " +
        "so a send that holds the owner's input is never made to it; nothing was sent")
    { RefusedFor = PromptRefusal.OneCallSubmit };

    /// <summary>The prompt was strictly older than the delivery age limit (phase 5, QA finding F6); nothing was
    /// typed. A refusal the sender must NOT retry: the delivery is answered not-delivered with the too-old
    /// reason, not held for the session's next idle moment.</summary>
    public static GuardedSendResult TooOld(ActivityState state, string reason) => new(false, false, false, state, reason)
    { RefusedTooOld = true };

    public static GuardedSendResult NotRunning(ActivityState state) => new(false, false, true, state, "session has exited");
}

/// <summary>The refusals of a guarded send a sender needs to tell apart.</summary>
public enum PromptRefusal
{
    /// <summary>The owner has unsent text in the composer.</summary>
    OwnerDraft,

    /// <summary>The session's terminal submits in one call that cannot be taken back.</summary>
    OneCallSubmit,
}
