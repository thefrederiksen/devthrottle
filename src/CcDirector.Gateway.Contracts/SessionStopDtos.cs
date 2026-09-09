namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The four verdicts a stop can carry (mission "Stop a session", Ruling 3). Every one of them is a
/// SUCCESS - a stop never fails because there is nothing left to stop.
///
/// <see cref="AlreadyStopped"/> and <see cref="NotOnFleet"/> must never be folded into one word. The
/// first means a machine WAS asked and looked and found no live process; the second means no machine
/// was asked at all, because nothing in the account carries that identifier. Saying "it is gone" when
/// nothing looked is the worse of the two mistakes.
/// </summary>
public static class SessionStopVerdict
{
    /// <summary>A process was running, it was ended, and the row was removed.</summary>
    public const string Stopped = "stopped";

    /// <summary>No live process was found. A row that was still there has been removed anyway.</summary>
    public const string AlreadyStopped = "alreadyStopped";

    /// <summary>No session in the account carries that identifier, so no machine was asked.</summary>
    public const string NotOnFleet = "notOnFleet";

    /// <summary>
    /// The stop was carried out, and this answer cannot say what it found.
    ///
    /// THERE ARE THREE CAUSES, AND THEY SHARE THIS ONE WORD because they are one fact about the ANSWER, not
    /// three different things that happened. The headline tells them apart in words; the fields differ per
    /// cause, and the list below is the only correct account of which fields mean anything:
    ///
    /// 1. AN OLDER DIRECTOR. The Gateway and the Directors do not deploy together - the Gateway ships in a
    ///    container image and each Director updates itself on its own machine - so during a rollout this
    ///    Gateway can be handed an answer from a Director that predates this mission and reports only the
    ///    original hardcoded <c>killed</c> / <c>removed</c> pair. Here EVERY description field is meaningless:
    ///    the process id is null, and <c>ProcessEnded</c>, <c>RowRemoved</c> and the worktree fields are all
    ///    at their empty values because nothing established them, NOT because they were established to be
    ///    false. A reader that sees this cause must read the headline and ignore the fields.
    ///
    /// 2. THE LIVENESS CHECK COULD NOT BE READ. The Director looked and the operating system would not
    ///    answer - a process it may not open throws rather than reporting a state. Added after inspection 1,
    ///    which found "already stopped" being inferred from a check that had thrown.
    ///
    /// 3. THE SESSION CARRIED NO PROCESS IDENTIFIER, so nothing was ever checked - the remote workflow
    ///    backend reports zero while a remote run is actively going, and the pipe and studio backends report
    ///    zero always. Also from inspection 1.
    ///
    /// UNDER CAUSES 2 AND 3 THE ESTABLISHED FIELDS ARE REAL AND MUST BE READ: the process identifier taken
    /// off the row, <c>RowRemoved</c>, and the worktree path and its uncommitted-changes answer are all
    /// things that stop genuinely established, and Ruling 2 REQUIRES the worktree sentence. Only
    /// <c>ProcessEnded</c> is empty, and it is empty because nothing established it.
    ///
    /// This word exists because both of the obvious answers are wrong. Refusing the stop (the first
    /// implementation answered 502) reports a FAILURE for an operation that SUCCEEDED - the session really
    /// was stopped - and a tool confident in one direction and vague in the other is the exact complaint
    /// this mission exists to fix. But calling it <see cref="Stopped"/> asserts a fact nobody established.
    /// </summary>
    public const string StoppedNotDescribed = "stoppedNotDescribed";
}

/// <summary>
/// The Director's answer to the <c>kill</c> verb: what the machine that owns the session actually found
/// and actually did. The Director is the only place the row and the process can be compared, because it
/// is the one machine that holds both.
///
/// <see cref="Killed"/> and <see cref="Removed"/> are the ORIGINAL two fields, kept with their original
/// values so nothing reading the old answer changes meaning. They are best-effort and they do NOT
/// distinguish "there was a process and I ended it" from "there was nothing running" - that is exactly
/// why the honest fields beside them exist. Read <see cref="ProcessEnded"/> and <see cref="RowRemoved"/>
/// for the truth; read the older pair only for compatibility.
/// </summary>
public sealed class DirectorStopResult
{
    /// <summary>Unchanged: the kill sequence ran (best-effort, and true even when nothing was running).</summary>
    public bool Killed { get; set; }

    /// <summary>Unchanged: the removal ran.</summary>
    public bool Removed { get; set; }

    /// <summary>The agent process id found BEFORE the stop, or null when the session held none.</summary>
    public int? ProcessId { get; set; }

    /// <summary>A live process WAS found and this stop ended it.</summary>
    public bool ProcessEnded { get; set; }

    /// <summary>A row WAS present on this Director and this stop removed it.</summary>
    public bool RowRemoved { get; set; }

    /// <summary>The worktree or repository the session held, or null when it held none.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>
    /// Whether that worktree has uncommitted changes. NULL MEANS IT COULD NOT BE DETERMINED, and null
    /// must never be reported as "clean" - see <c>SessionGitStatusMonitor</c> for the same rule and why
    /// it is written down: reporting zero would tell every reader downstream that a tree is clean, which
    /// is the one thing a failed probe does not know.
    /// </summary>
    public bool? WorktreeHadUncommittedChanges { get; set; }

    /// <summary>
    /// What this stop could establish, in one word about the machine's own words in
    /// <see cref="NotDescribedReason"/> - or null when it established everything it reports.
    ///
    /// The Director sets this ONLY alongside <see cref="SessionStopVerdict.StoppedNotDescribed"/>, and it is
    /// what lets the Gateway's fold tell an unread liveness check apart from a Director too old to describe
    /// anything at all. It is one line, written for an operator, and it is folded into the headline verbatim.
    /// </summary>
    public string? NotDescribedReason { get; set; }

    /// <summary><see cref="SessionStopVerdict.Stopped"/>, <see cref="SessionStopVerdict.AlreadyStopped"/>, or
    /// <see cref="SessionStopVerdict.StoppedNotDescribed"/> when this machine could not read whether a
    /// process was alive - which is a KNOWN word from a current Director, not the silence of an old one.
    /// The Director never returns <see cref="SessionStopVerdict.NotOnFleet"/> - that verdict belongs to the
    /// Gateway, which is the only party that can see the whole account.</summary>
    public string Verdict { get; set; } = "";
}

/// <summary>Body of <c>POST /sessions/{sid}/stop</c>. The reason is required (Ruling 4).</summary>
public sealed class SessionStopRequest
{
    /// <summary>Why this session is being stopped, in the caller's own words. Required and recorded.</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// The Gateway's answer to a stop: the same facts the Director reported, plus the FINISHED WORDS an
/// operator reads. The sentences are folded ONCE, here, and every surface renders them verbatim - the
/// command line, the Cockpit, the Director window and the phone. No client composes a sentence of its
/// own (Ruling 5, and the house rule that the Gateway owns all ruling).
/// </summary>
public sealed class SessionStopResponse
{
    /// <summary>One of the four <see cref="SessionStopVerdict"/> words.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>The one line an operator reads. Always present.</summary>
    public string Headline { get; set; } = "";

    /// <summary>Zero or more further lines, in the order they are to be shown.</summary>
    public List<string> Details { get; set; } = new();

    /// <summary>The session identifier the stop was asked for, exactly as the caller gave it.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>
    /// The short form of that identifier, as it appears in the headline.
    ///
    /// A GUID shortens to its first eight characters, which is how this fleet writes a session
    /// identifier everywhere else. ANYTHING ELSE IS PASSED THROUGH WHOLE. The not-on-this-fleet case
    /// is reached precisely when the caller typed something no session matched, and that is often a
    /// NAME rather than an identifier - truncating it to eight characters would print "nothing in
    /// this account carries the id Stop a s", which reads as a corrupted answer rather than an
    /// honest one.
    /// </summary>
    public string ShortId { get; set; } = "";

    /// <summary>The agent process id found before the stop, or null.</summary>
    public int? ProcessId { get; set; }

    /// <summary>A live process was found and this stop ended it.</summary>
    public bool ProcessEnded { get; set; }

    /// <summary>A row was present and this stop removed it.</summary>
    public bool RowRemoved { get; set; }

    /// <summary>The worktree or repository the session held, or null.</summary>
    public string? WorktreePath { get; set; }

    /// <summary>Whether that worktree had uncommitted changes; null means it could not be determined.</summary>
    public bool? WorktreeHadUncommittedChanges { get; set; }

    /// <summary>The reason the caller gave, as it was recorded. Null on the door that carries none.</summary>
    public string? Reason { get; set; }

    /// <summary>Who asked for the stop, as it was recorded in the audit trail.</summary>
    public string? StoppedBy { get; set; }

    /// <summary>Compatibility with the original <c>DELETE /sessions/{sid}</c> answer. See
    /// <see cref="DirectorStopResult.Killed"/>: best-effort, and not the honest fields.</summary>
    public bool Killed { get; set; }

    /// <summary>Compatibility. See <see cref="DirectorStopResult.Removed"/>.</summary>
    public bool Removed { get; set; }
}
