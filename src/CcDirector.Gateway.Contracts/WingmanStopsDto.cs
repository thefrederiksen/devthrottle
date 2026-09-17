namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The answer to <c>GET /sessions/{sid}/wingman-stops</c>: every stop the Wingman judged for one session, newest first,
/// fully prepared for the Wingman tab (the Wingman inspector, phase 2).
///
/// EVERYTHING HERE IS A FINISHED STRING OR FLAG, folded once on the Gateway by <c>WingmanStopsFold</c>. The Cockpit
/// formats the UTC instants into local time and lays the rest out verbatim. It never decides what an outcome means,
/// which group a stop belongs to, what colour it wore, or whether a clock ran out - the product's rule that the client
/// is dumb and the Gateway rules.
/// </summary>
public sealed class WingmanStopsResponse
{
    /// <summary>The session the stops are about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The stops, newest first.</summary>
    public List<WingmanStopDto> Stops { get; set; } = new();

    /// <summary>The filter chips, in display order, each with how many of <see cref="Stops"/> it holds. "All" is first
    /// and counts every stop.</summary>
    public List<WingmanStopGroupDto> Groups { get; set; } = new();
}

/// <summary>One filter chip.</summary>
public sealed class WingmanStopGroupDto
{
    /// <summary>The group's key, which <see cref="WingmanStopDto.Group"/> carries: "all", "needs-you", "calm", "failed"
    /// or "not-asked".</summary>
    public string Key { get; set; } = "";

    /// <summary>The chip's words.</summary>
    public string Label { get; set; } = "";

    /// <summary>How many stops in the answer are in this group.</summary>
    public int Count { get; set; }
}

/// <summary>One stop the Wingman judged, or stood down from, as the tab shows it.</summary>
public sealed class WingmanStopDto
{
    /// <summary>The trace's own identity.</summary>
    public string TraceId { get; set; } = "";

    /// <summary>When the detector observed the stop (UTC).</summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>When the record was written (UTC).</summary>
    public DateTime RecordedAtUtc { get; set; }

    /// <summary>What asked for the judgement, as recorded: "turn-end", "voice", "sweep", "on-demand",
    /// "snooze-expiry" or "clock".</summary>
    public string Trigger { get; set; } = "";

    /// <summary>What asked for it, in plain English.</summary>
    public string TriggerText { get; set; } = "";

    /// <summary>How it ended, as recorded: "judged", "refused", "did-not-answer", "rate-limited", "unavailable",
    /// "reused", "expired", "skipped", "cancelled" or "joined".</summary>
    public string Outcome { get; set; } = "";

    /// <summary>How it ended, in plain English.</summary>
    public string OutcomeText { get; set; } = "";

    /// <summary>The filter group this stop is in besides "all": "needs-you", "calm", "failed" or "not-asked". Null only
    /// for an outcome word this Gateway does not know, which is then counted under "all" alone.</summary>
    public string? Group { get; set; }

    /// <summary>True when the row's colour and label were recorded with the stop.</summary>
    public bool RowRecorded { get; set; }

    /// <summary>The colour word the row wore with this stop on it, as it was then - not as the row looks now. Null when
    /// it was not recorded.</summary>
    public string? RowColour { get; set; }

    /// <summary>The canonical pixel for <see cref="RowColour"/>. Null when it was not recorded.</summary>
    public string? RowColourHex { get; set; }

    /// <summary>The label the row showed then, or "Not recorded".</summary>
    public string RowLabel { get; set; } = "";

    /// <summary>The verdict word, or null when there was no accepted verdict.</summary>
    public string? VerdictWord { get; set; }

    /// <summary>The verdict's confidence, or null when there was no accepted verdict.</summary>
    public string? Confidence { get; set; }

    /// <summary>The verdict word and confidence in words, or "No verdict".</summary>
    public string VerdictText { get; set; } = "";

    /// <summary>The one line across the top of the selected stop.</summary>
    public WingmanStopStripDto Strip { get; set; } = new();

    /// <summary>What the judge was shown.</summary>
    public WingmanStopSawDto Saw { get; set; } = new();

    /// <summary>What the judge was asked.</summary>
    public WingmanStopAskedDto Asked { get; set; } = new();

    /// <summary>What the judge answered.</summary>
    public WingmanStopAnsweredDto Answered { get; set; } = new();

    /// <summary>What the product did with the answer.</summary>
    public WingmanStopDidDto Did { get; set; } = new();
}

/// <summary>The summary strip: the whole stop in one line.</summary>
public sealed class WingmanStopStripDto
{
    /// <summary>The verdict's label, or the outcome in words when there was no verdict.</summary>
    public string Label { get; set; } = "";

    /// <summary>How long the judge took, in words: "Answered in 3.8 seconds", "No answer arrived" or "Not asked".</summary>
    public string ReplyText { get; set; } = "";

    /// <summary>How the stop ended, in words.</summary>
    public string OutcomeText { get; set; } = "";
}

/// <summary>Block one: what the judge was shown.</summary>
public sealed class WingmanStopSawDto
{
    /// <summary>True when the package the judge was given is kept and shown below.</summary>
    public bool Kept { get; set; }

    /// <summary>Why nothing is shown, when <see cref="Kept"/> is false. Null otherwise.</summary>
    public string? NotKeptText { get; set; }

    /// <summary>The terminal screen rows, top to bottom.</summary>
    public List<string> ScreenRows { get; set; } = new();

    /// <summary>The heading for <see cref="SourceText"/>: "Latest reply" or "Failure text". Null when there is none.</summary>
    public string? SourceHeading { get; set; }

    /// <summary>The agent's latest reply, or the failure text for a terminal failure. Null when there is none.</summary>
    public string? SourceText { get; set; }

    /// <summary>The recent turns of the conversation the judge was shown. Null when there were none.</summary>
    public string? RecentTurns { get; set; }

    /// <summary>The session facts the judge was given, as name and value pairs in display order.</summary>
    public List<WingmanStopFactDto> Facts { get; set; } = new();
}

/// <summary>One session fact.</summary>
public sealed class WingmanStopFactDto
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Block two: what the judge was asked.</summary>
public sealed class WingmanStopAskedDto
{
    /// <summary>The exact prompt, or null when the judge was not asked.</summary>
    public string? Prompt { get; set; }

    /// <summary>True when the stored prompt was cut at the size ceiling.</summary>
    public bool Cut { get; set; }

    /// <summary>What the cut means, in words, or null when nothing was cut.</summary>
    public string? CutText { get; set; }

    /// <summary>Why there is no prompt, when <see cref="Prompt"/> is null. Null otherwise.</summary>
    public string? NotAskedText { get; set; }
}

/// <summary>Block three: what the judge answered.</summary>
public sealed class WingmanStopAnsweredDto
{
    /// <summary>The reply exactly as received, or null when no answer arrived.</summary>
    public string? RawReply { get; set; }

    /// <summary>True when the stored reply was cut at the size ceiling.</summary>
    public bool Cut { get; set; }

    /// <summary>What the cut means, in words, or null when nothing was cut.</summary>
    public string? CutText { get; set; }

    /// <summary>How long the judge took, in seconds, or null when it was not asked.</summary>
    public double? ReplySeconds { get; set; }

    /// <summary>Why there is no reply, when <see cref="RawReply"/> is null. Null otherwise.</summary>
    public string? NoAnswerText { get; set; }
}

/// <summary>Block four: what the product did with the answer.</summary>
public sealed class WingmanStopDidDto
{
    /// <summary>What became of the answer, in words: "Accepted", "Refused", "No verdict formed", "Not asked", or for a
    /// verdict nobody was asked for this stop, that it was used again or written by the carrying-on clock.</summary>
    public string Decision { get; set; } = "";

    /// <summary>True only when a judge answered this stop and the answer was accepted.</summary>
    public bool Accepted { get; set; }

    /// <summary>The reason recorded for a refused or failed answer, verbatim - the check that failed, in the contract's
    /// own words. Null for an accepted answer and for a stop that asked nothing.</summary>
    public string? Reason { get; set; }

    /// <summary>The verdict's label, or null when there was no accepted verdict.</summary>
    public string? VerdictLabel { get; set; }

    /// <summary>The verdict's summary, or null when there was no accepted verdict.</summary>
    public string? VerdictSummary { get; set; }

    /// <summary>Why a skipped, cancelled, joined or unavailable stop ended that way, in words. Null otherwise.</summary>
    public string? CauseText { get; set; }

    /// <summary>What this stop replaced, in words, or null when it replaced nothing.</summary>
    public string? ReplacedText { get; set; }

    /// <summary>The stop in this answer whose verdict this one replaced, when it is in the answer. Null otherwise.</summary>
    public string? ReplacedTraceId { get; set; }

    /// <summary>The carrying-on clock, or null when this stop has none.</summary>
    public WingmanStopClockDto? Clock { get; set; }
}

/// <summary>The carrying-on clock of a "continues-alone" stop, or the expiry that ended one.</summary>
public sealed class WingmanStopClockDto
{
    /// <summary>When the clock was set to run out at the moment of judgement (UTC), or null when no deadline was recorded.</summary>
    public DateTime? SetToRunOutAtUtc { get; set; }

    /// <summary>When it actually ran out (UTC), or null when it did not run out in this answer.</summary>
    public DateTime? RanOutAtUtc { get; set; }

    /// <summary>The clock in words.</summary>
    public string Text { get; set; } = "";

    /// <summary>The expiry stop that ended this clock, when it is in this answer. Null otherwise.</summary>
    public string? RanOutTraceId { get; set; }
}
