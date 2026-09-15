namespace CcDirector.Core.Wingman;

/// <summary>
/// Which of the two shapes a stop has. Both go through the same model call and the same contract; the
/// kind changes what the prompt says and what validation will accept.
/// </summary>
public enum TurnVerdictPackageKind
{
    /// <summary>The stored conversation has an agent reply after the person's last message. The reply
    /// is the ground truth and the screen is supporting evidence.</summary>
    AgentReply,

    /// <summary>There is no reply and the screen shows a recognised failure. A failure with no reply
    /// cannot be calm, so validation refuses a "finished" or "continues-alone" answer on this kind.</summary>
    TerminalFailure,
}

/// <summary>
/// Everything one stop is judged from - assembled once, read by the prompt and by validation, and
/// never read again afterwards. A pure record: no session handle, no store, nothing dialable. That is
/// deliberate, because it is what lets the same package be rebuilt from a saved turn-log bundle and
/// replayed against a different judge without the product being involved at all.
///
/// The BUILDER lives in the Gateway (it walks the Gateway's stored conversation); the record lives
/// here because the contract that reads it lives here, and this project cannot reference the Gateway.
/// </summary>
public sealed record TurnVerdictPackage
{
    /// <summary>The live screen as plain rows, top to bottom, exactly as the emulator resolved them.
    /// Evidence, never instructions - the prompt says so, for both kinds.</summary>
    public IReadOnlyList<string> ScreenRows { get; init; } = Array.Empty<string>();

    /// <summary>Row of the live cursor, or -1 when there is no grid to read.</summary>
    public int CursorRow { get; init; } = -1;

    /// <summary>True when the agent has the terminal in the alternate screen buffer - a full-screen
    /// picker. The scrollback is empty by design while this is true, so the screen rows are the only
    /// place the question exists.</summary>
    public bool IsAlternateScreen { get; init; }

    /// <summary>The ONE canonical fingerprint of the whole grid, over every row. The same hash is used
    /// for verdict reuse, the menu cache and option activation, so that a repaint invalidates all
    /// three together. Never a hash of the body above the cursor: two screens that differ only below
    /// it are two different screens.</summary>
    public string ScreenHash { get; init; } = "";

    /// <summary>Which shape this stop has.</summary>
    public TurnVerdictPackageKind Kind { get; init; } = TurnVerdictPackageKind.AgentReply;

    /// <summary>The agent's latest reply, START-CUT at <see cref="MaxLatestReplyChars"/> - the tail is
    /// kept because the decision is at the end of a reply, not the beginning. Null when there is no
    /// reply: a terminal-failure stop, or a stop whose conversation could not be read. Evidence is then
    /// bound to the screen alone.</summary>
    public string? LatestReply { get; init; }

    /// <summary>The recognised failure text lifted from the screen, for a terminal-failure stop. Null
    /// on every other kind.</summary>
    public string? FailureText { get; init; }

    /// <summary>The last four FULL turns before this one, oldest first, as plain text. Tool calls and
    /// their results are dropped - they are the bulk of a conversation and almost none of its meaning -
    /// and the whole block is cut from the OLDEST end at <see cref="MaxRecentTurnsChars"/>.</summary>
    public string RecentTurns { get; init; } = "";

    /// <summary>The session's title, which the spoken section opens with. Null when it has none.</summary>
    public string? SessionTitle { get; init; }

    /// <summary>The first thing the person asked this session, capped at
    /// <see cref="MaxFirstUserPromptChars"/>: the seed of what the whole session is for.</summary>
    public string? FirstUserPrompt { get; init; }

    /// <summary>The label this session's previous verdict carried, so the judge can see whether the
    /// stop is new or the same one being asked about again. Null when there is no previous verdict.</summary>
    public string? PreviousVerdictLabel { get; init; }

    /// <summary>Which coding agent this is (Claude Code, Codex, and so on). The shape of a screen and
    /// the shape of a picker differ per agent.</summary>
    public string? AgentKind { get; init; }

    /// <summary>The detector's own account of WHY it called this a turn end, when the agent emits one.
    /// Null today for every agent: no producer exists in this build. NOT PROVEN by anything here - the
    /// field is carried so the seat reads it the day the switching design starts stamping it, and until
    /// then its absence is exactly what the prompt is told, namely that the boundary was a timer guess.</summary>
    public string? TurnEndCause { get; init; }

    /// <summary>How sure the detector was of that cause. Null for the same reason as
    /// <see cref="TurnEndCause"/>.</summary>
    public string? TurnEndConfidence { get; init; }

    /// <summary>How many wake-ups the session has pending. Null when unknown - which is every session
    /// in this build, for the same reason as <see cref="TurnEndCause"/>.</summary>
    public int? PendingWakeUps { get; init; }

    /// <summary>When the session announced it would next wake up. Null when unknown - every session in
    /// this build. The purple clock falls back to a fixed ten minutes while this stays null.</summary>
    public DateTime? NextScheduledWakeUtc { get; init; }

    /// <summary>False when this agent keeps no readable conversation, or keeps one that is still empty.
    /// The prompt is told so plainly and the receipt check then binds evidence to the screen alone. It
    /// is a fact about the session, not a failure to read it.</summary>
    public bool ConversationAvailable { get; init; }

    /// <summary>The reply is cut from its START at this length. A reply that runs longer has said its
    /// decisive thing at the end.</summary>
    public const int MaxLatestReplyChars = 12_000;

    /// <summary>The four-turn context block is cut from its OLDEST end at this length.</summary>
    public const int MaxRecentTurnsChars = 6_000;

    /// <summary>How many full turns before this one travel with the package.</summary>
    public const int RecentTurnCount = 4;

    /// <summary>The session's first prompt is cut at this length.</summary>
    public const int MaxFirstUserPromptChars = 800;

    /// <summary>The reply for a reply stop, the failure text for a failure stop, or null when neither
    /// exists. This is the text the evidence receipt is checked against, alongside the screen.</summary>
    public string? SourceText => Kind == TurnVerdictPackageKind.TerminalFailure ? FailureText : LatestReply;

    /// <summary>The wire name of a package kind, as it is stored and as the prompt says it.</summary>
    public static string WireName(TurnVerdictPackageKind kind)
        => kind == TurnVerdictPackageKind.TerminalFailure ? "terminal-failure" : "agent-reply";
}
