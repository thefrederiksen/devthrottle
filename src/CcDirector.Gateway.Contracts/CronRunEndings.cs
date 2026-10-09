namespace CcDirector.Gateway.Contracts;

/// <summary>
/// How the session a scheduled run started came to an end - the answer to "does this schedule clean up after
/// itself?" (the owner, 2026-10-09). Every run carried a task status of "unknown" until this existed, so no screen
/// could tell a schedule whose sessions close themselves from one whose sessions wait for someone to close them.
///
/// Three of these are RECORDED on the run at the moment they happen, by the Gateway route that carried them, because
/// only that route knows who asked: <see cref="ClosedItself"/>, <see cref="StoppedByYou"/> and
/// <see cref="StoppedBySession"/>. The rest are FOLDED when the run is read, from the session's work-history row.
/// </summary>
public static class CronRunEndings
{
    /// <summary>The fire failed to start a session, so there is nothing to end.</summary>
    public const string NoSession = "no-session";

    /// <summary>
    /// The fire drained a work list. A drain starts many sessions and the run records none of them, so how they ended
    /// is not known here.
    /// </summary>
    public const string WorkList = "work-list";

    /// <summary>The session is still open.</summary>
    public const string StillOpen = "still-open";

    /// <summary>The session asked to be closed (<c>session done</c>), or its agent finished and it was dismissed.</summary>
    public const string ClosedItself = "closed-itself";

    /// <summary>A person stopped it or flagged it for deletion - the desktop, the Cockpit or the phone.</summary>
    public const string StoppedByYou = "stopped-by-you";

    /// <summary>Another session stopped it or flagged it for deletion.</summary>
    public const string StoppedBySession = "stopped-by-session";

    /// <summary>
    /// It was closed on its machine without any request through the Gateway, so who closed it is not known. Every run
    /// that ended before endings were recorded reads this way.
    /// </summary>
    public const string ClosedNotRecorded = "closed-not-recorded";

    /// <summary>Its Director shut down while it was open.</summary>
    public const string DirectorStopped = "director-stopped";

    /// <summary>Its Director went silent while it was open, so the Gateway concluded it was interrupted.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>The Gateway has no record of the session, for example because its history aged out.</summary>
    public const string Unknown = "unknown";

    /// <summary>True for the three endings a Gateway route writes onto the run when it carries the request.</summary>
    public static bool IsRecorded(string? ending) =>
        ending is ClosedItself or StoppedByYou or StoppedBySession;
}

/// <summary>
/// One schedule's recent run record, folded by the Gateway for the Schedule page: whether its sessions close
/// themselves, in words. The client shows <see cref="Text"/> verbatim and colours it by <see cref="Verdict"/>.
/// </summary>
public sealed class CronRunRecordSummaryDto
{
    /// <summary><c>ok</c> (every ended run closed itself), <c>bad</c> (a run was stopped by someone or is still open)
    /// or <c>none</c> (nothing recorded to judge by yet).</summary>
    public string Verdict { get; set; } = "none";

    /// <summary>The record in words, for example "closes itself - 8 of 8, about 6 min".</summary>
    public string Text { get; set; } = "";

    /// <summary>How many recent runs the summary covers.</summary>
    public int Runs { get; set; }
}
