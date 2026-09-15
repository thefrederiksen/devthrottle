namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One judgement the Wingman made, kept whole so it can be inspected afterwards (the Wingman inspector,
/// devthrottle_internal#2029): what the judge was SHOWN, exactly what it was ASKED, what it ANSWERED word
/// for word, how long it took, and the verdict record the product kept.
///
/// WHY THIS IS A SECOND TABLE AND NOT MORE COLUMNS ON <see cref="TurnVerdictEntity"/>. The verdict table is
/// the product's live state, and it is deleted for a session every time that session starts working again
/// (<c>TurnVerdictStore.Invalidate</c>): a verdict is a statement about a screen, and once the screen is gone
/// the statement must stop colouring the row. That is right for the row and fatal for a record of what the
/// Wingman did - a session's history would never hold more than its latest stop. So the trace lives beside
/// the verdicts, is only ever APPENDED, and nothing but the retention purge removes a row.
///
/// WHY IT IS A COPY AND NOT A JOIN TO THE TURN LOG. The turn log is switched on per account by an
/// administrator, lives in daily files pulled down for grading, and makes its own screen read - which is
/// not the screen the judge was given. The only honest record of what the judge saw is the package it was
/// given, so that is what is kept.
///
/// THE BULKY PARTS (<see cref="PackageJson"/>, <see cref="Prompt"/>, <see cref="RawReply"/>) are a copy of
/// somebody's terminal and conversation. They are held for the same seven days as the verdicts, and only
/// written for an account whose judge switch is on.
/// </summary>
public sealed class TurnVerdictTraceEntity : TenantScopedEntity
{
    /// <summary>This trace's own identity, minted in code. The tenant and this are the key, because a
    /// session can be judged twice inside one clock tick and neither may overwrite the other.</summary>
    public string TraceId { get; set; } = "";

    /// <summary>The session the judgement was about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Director the session ran on, as the seat knew it. Empty when it was not known.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>When this trace was written (UTC). The history orders on it and the purge cuts on it.</summary>
    public DateTime RecordedAtUtc { get; set; }

    /// <summary>When the detector observed the stop being judged (UTC) - the same join key the verdict row
    /// and the turn log carry.</summary>
    public DateTime TurnEndObservedAtUtc { get; set; }

    /// <summary>What asked for the judgement: "turn-end", "voice", "sweep", "on-demand", or "clock" for an
    /// expiry of the carrying-on clock.</summary>
    public string Trigger { get; set; } = "";

    /// <summary>How it ended: "judged", "refused", "did-not-answer", "rate-limited", "unavailable",
    /// "reused", "expired", "skipped" or "cancelled".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Why a stop was skipped or cancelled (a closed activity cause word), or the exception type an
    /// "unavailable" judgement met. Null otherwise.</summary>
    public string? Cause { get; set; }

    /// <summary>The verdict record this judgement stored. Null for "skipped" and "cancelled", which store none.</summary>
    public string? VerdictId { get; set; }

    /// <summary>The verdict this one replaced, when it replaced one - the carrying-on verdict an expiry
    /// overwrote. Null otherwise.</summary>
    public string? ReplacedVerdictId { get; set; }

    /// <summary>How long the judge took to answer, in seconds. Null when the judge was not asked.</summary>
    public double? ReplySeconds { get; set; }

    /// <summary>Whether the account's verdict colours were on at that moment - whether this verdict could
    /// reach a screen at all, or was a shadow record.</summary>
    public bool ColourEnabled { get; set; }

    /// <summary>The package the judge was given - screen rows, reply or failure text, recent turns and the
    /// session facts - as serialized <c>TurnVerdictPackage</c> JSON. Null when no package was built.</summary>
    public string? PackageJson { get; set; }

    /// <summary>True when a package was built but was over the store's ceiling and was not kept.</summary>
    public bool PackageOmitted { get; set; }

    /// <summary>The exact prompt text sent to the judge, cut at the store's ceiling. Null when the judge was not
    /// asked.</summary>
    public string? Prompt { get; set; }

    /// <summary>True when <see cref="Prompt"/> was longer than the ceiling and was cut.</summary>
    public bool PromptTruncated { get; set; }

    /// <summary>The judge's answer exactly as received, cut at the store's ceiling. Null when no answer
    /// arrived.</summary>
    public string? RawReply { get; set; }

    /// <summary>True when <see cref="RawReply"/> was longer than the ceiling and was cut.</summary>
    public bool RawReplyTruncated { get; set; }

    /// <summary>The verdict record the judgement stored, as serialized <c>TurnVerdictDto</c> JSON - kept
    /// here too, because the verdict table's copy is deleted when the session works again. Null for "skipped" and
    /// "cancelled".</summary>
    public string? VerdictJson { get; set; }
}
