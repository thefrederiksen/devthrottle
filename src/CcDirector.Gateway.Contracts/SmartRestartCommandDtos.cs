namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE COMMAND LINE DOOR onto the smart shutdown (mission "Smart Director Restart", section 5.3 item 12).
///
/// A Director is emptied and restarted from its own File menu, and that menu is a window. One broken window
/// must never again leave a Director impossible to empty, so the same engine is reachable from the command
/// line as well. These three verbs are that door, and they are HOST-level: they are about the whole Director,
/// not about one session, exactly like the restart cycle's two verbs beside them.
///
/// THE DOOR IS A DOOR, NOT A SECOND ENGINE. Every one of them is answered on the Director by the engine that
/// already exists - <c>ISmartShutdown</c> for the start and the progress, <c>IDirectorWayUp</c> for the
/// history. Nothing here decides what a state means, what a phase is called or how a count reads: every
/// sentence a person sees travels on these objects, computed once on the Director, and the command line
/// renders it verbatim (critical rule 7 in CLAUDE.md).
/// </summary>
public static class SmartRestartVerbs
{
    /// <summary>Start a smart shutdown with the restart purpose on this Director. Answered as soon as the run
    /// is TAKEN - the same shape as the restart cycle and the restore, and for the same reason: the run closes
    /// sessions for minutes, and a command that waited for it would time out long before it ended.</summary>
    public const string Start = "smart-restart/start";

    /// <summary>Where the run started on this Director stands, as one complete snapshot. A read.</summary>
    public const string Progress = "smart-restart/progress";

    /// <summary>The restart records this Director wrote, newest first - the newest twenty-five of them. A
    /// read. The cap is the way up's (<c>DirectorWayUp.MostRecentRecordsRead</c>), and where it bites the
    /// history's own sentence says how many older records are not being read, so a capped answer is never
    /// stated as a total.</summary>
    public const string History = "smart-restart/history";
}

/// <summary>What the command line asks for when it starts a smart restart.</summary>
public sealed class SmartRestartStartOrder
{
    /// <summary>How long the sessions are given, in whole minutes. It must be one of the times the engine
    /// allows (5, 10, 15, 30, 60); anything else is refused by name, never rounded to a neighbour.</summary>
    public int Minutes { get; set; }

    /// <summary>The owner's own words for why, written into the record. Optional.</summary>
    public string? Reason { get; set; }
}

/// <summary>The answer to a start that was taken.</summary>
public sealed class SmartRestartStartAccepted
{
    /// <summary>Always true. A start that was not taken is a failure carrying its reason, never this object
    /// with a false in it - the two are told apart by the command's outcome, not by reading a flag.</summary>
    public bool Taken { get; set; } = true;

    /// <summary>The Director the run is on.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The minutes the sessions were given.</summary>
    public int Minutes { get; set; }

    /// <summary>What was taken, in plain words, shown as it is.</summary>
    public string Detail { get; set; } = "";
}

/// <summary>
/// One complete reading of the run, as the progress screen sees it. Complete and immutable, like the
/// snapshot it is made from: a reader replaces what it shows, it never merges.
/// </summary>
public sealed class SmartRestartProgressDto
{
    /// <summary>True while the run is still going. False both before one has started and after one ended -
    /// <see cref="Started"/> is what tells those two apart.</summary>
    public bool Running { get; set; }

    /// <summary>True when this Director has a run to report at all. False means no smart shutdown has been
    /// started on it since it came up, and then <see cref="Detail"/> says exactly that. An absent run and a
    /// finished run are never collapsed into one silence.</summary>
    public bool Started { get; set; }

    /// <summary>The phase's own name (Starting, Asking, Collecting, ...), for a reader that colours by
    /// state. It never words by it.</summary>
    public string Phase { get; set; } = "";

    /// <summary>The phase in plain words, shown as it is.</summary>
    public string PhaseLabel { get; set; } = "";

    /// <summary>"4 of 9 shut down", shown as it is.</summary>
    public string CountLabel { get; set; } = "";

    /// <summary>How many sessions the run is dealing with.</summary>
    public int Total { get; set; }

    /// <summary>How many are verifiably gone.</summary>
    public int Gone { get; set; }

    /// <summary>When the run started.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Two thirds of the time allowed: when a session still mid-turn is interrupted.</summary>
    public DateTime InterruptAtUtc { get; set; }

    /// <summary>When every session still present is ended.</summary>
    public DateTime LimitUtc { get; set; }

    /// <summary>The record on the Gateway, once it exists.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>The most recent thing worth saying, in plain words, or null.</summary>
    public string? Note { get; set; }

    /// <summary>How it ended (Emptied, RestartAccepted, RestartRefused, Cancelled, Refused, Failed), or null
    /// while it is still running.</summary>
    public string? Outcome { get; set; }

    /// <summary>What happened, in plain words, shown as it is. On a finished run this is the result's own
    /// sentence; on a Director that has started nothing it says so.</summary>
    public string Detail { get; set; } = "";

    /// <summary>One row per session, leads first, each lead followed by the sessions under it.</summary>
    public List<SmartRestartSessionDto> Sessions { get; set; } = new();
}

/// <summary>What is happening to one session.</summary>
public sealed class SmartRestartSessionDto
{
    /// <summary>The session's id.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Its name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The mission it is on, or null.</summary>
    public string? Mission { get; set; }

    /// <summary>Its role, or null.</summary>
    public string? Role { get; set; }

    /// <summary>The lead it sits under, or null for a lead or a standalone.</summary>
    public string? OwnerSessionId { get; set; }

    /// <summary>The state's own name, for a reader that colours by state.</summary>
    public string State { get; set; } = "";

    /// <summary>The state in plain words, shown as it is.</summary>
    public string StateLabel { get; set; } = "";

    /// <summary>Why, when there is a why - the delivery refusal, for one. Null otherwise.</summary>
    public string? Detail { get; set; }
}

/// <summary>The restart records this Director wrote, newest first - the newest twenty-five of them, which
/// is as far back as the way up reads. <see cref="Message"/> says so when there are older ones.</summary>
public sealed class SmartRestartHistoryDto
{
    /// <summary>True when nothing could be read. Then <see cref="Entries"/> is empty and
    /// <see cref="Message"/> is the reason: an empty history and an unreadable one are never confused.</summary>
    public bool Refused { get; set; }

    /// <summary>What this is, in plain words, shown as it is.</summary>
    public string Message { get; set; } = "";

    /// <summary>The records, newest first.</summary>
    public List<SmartRestartHistoryEntryDto> Entries { get; set; } = new();
}

/// <summary>One record in the history.</summary>
public sealed class SmartRestartHistoryEntryDto
{
    /// <summary>The record on the Gateway.</summary>
    public string WorkspaceId { get; set; } = "";

    /// <summary>When it was, in universal time.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>When it was, in plain words.</summary>
    public string WhenLabel { get; set; } = "";

    /// <summary>What kind of shutdown wrote it, in plain words.</summary>
    public string KindLabel { get; set; } = "";

    /// <summary>The reason in plain words, or null when none was given - a reader shows nothing for a null
    /// rather than a line saying there was no reason.</summary>
    public string? ReasonLabel { get; set; }

    /// <summary>What became of it, in plain words.</summary>
    public string OutcomeLabel { get; set; } = "";

    /// <summary>What this record still holds, in plain words - how many seats are waiting to be brought
    /// back AND how many ended without a handover - or null when there is nothing left to act on. It is
    /// the offer's own sentence and is not named after either count, because it says both. Reading it is
    /// not bringing anything back: the command line does not restore, and this sentence is what tells the
    /// owner a record is still worth opening.</summary>
    public string? SeatsLabel { get; set; }

    /// <summary>Why this record has stopped being offered when the Director starts although it still holds
    /// something to act on: it is older than the Director offers a record for. Null when it is still offered.
    /// It is here because the restart history is where nothing is hidden - a record that simply stopped
    /// appearing, with no sentence anywhere saying why, is the same confusion in the other direction.</summary>
    public string? NotOfferedAtStartUpLabel { get; set; }

    /// <summary>What became of each seat.</summary>
    public List<SmartRestartHistorySeatDto> Seats { get; set; } = new();
}

/// <summary>What became of one seat, for the history.</summary>
public sealed class SmartRestartHistorySeatDto
{
    /// <summary>The captured session id.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The seat's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The mission it was on, or null.</summary>
    public string? Mission { get; set; }

    /// <summary>Its role, or null.</summary>
    public string? Role { get; set; }

    /// <summary>What became of it, in plain words.</summary>
    public string Outcome { get; set; } = "";
}
