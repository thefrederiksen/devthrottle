using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The morning report for ONE account and ONE calendar day (issue #2119, slice 2 of #2096) - the JSON the
/// website's 7:00 cron reads, renders into the approved email design, and sends. The needs-your-attention
/// list, and the exact window the report covers.
///
/// NO YESTERDAY-STATS, BY OWNER RULING (2026-09-20, issue #3124): "telling me how much shit I did yesterday
/// is not going to help me today." The daily report answers WHAT NEEDS YOU TODAY. The scoreboard -
/// sessions run, work accepted, hosted-AI spend - is weekly-report material, and no weekly personal-stats
/// surface exists yet (mission item in #3124).
///
/// THE HONESTY RULE IS STRUCTURAL, NOT EDITORIAL. Anything the Gateway has no data for is ABSENT from the
/// JSON - never zero-filled, never estimated. A missing section means "this Gateway holds no data for
/// this account", which is a different statement from "nothing happened", and the email must be able to
/// tell them apart.
///
/// The field names here are the CONTRACT the website sender is coded against (owner-relayed, 24 July 2026).
/// They are camelCase on the wire (minimal-API web defaults). Renaming one breaks the email.
/// </summary>
public sealed class MorningReportDto
{
    /// <summary>The account this report is about, echoed back exactly as the caller asked for it, so the
    /// sender can prove the report it is about to email belongs to the recipient it is emailing.</summary>
    public string Account { get; set; } = "";

    /// <summary>The exact coordinates of the report - the resolved UTC range, plus the calendar day and
    /// zone it came from. Every claim in the report carries its coordinates.</summary>
    public MorningReportWindowDto Window { get; set; } = new();

    /// <summary>
    /// The needs-your-attention list, one typed item per row the email renders. ALWAYS PRESENT, possibly
    /// empty: an empty list is real knowledge ("nothing is waiting on you"), unlike an absent stat.
    /// Items are polymorphic and discriminated by their <c>type</c> string; the sender tolerates a type it
    /// does not know (it logs and does not render it), so a new item type never breaks a sent email.
    /// </summary>
    public List<MorningAttentionItemDto> Attention { get; set; } = new();

    /// <summary>
    /// Whether this account DID ANYTHING in the reported window - a turn submitted, or a session that
    /// became active (#3124). The owner's rule, 20 September 2026: "If you didn't work in DevThrottle
    /// yesterday, I don't think we should send a daily email. It's just annoying."
    ///
    /// THIS IS A GATE, NOT A STAT, AND THE DIFFERENCE IS THE WHOLE REASON IT IS A BOOLEAN. The owner
    /// ruled on the same day that the daily report is a briefing and not a scoreboard - "telling me how
    /// much shit I did yesterday is not going to help me today" - and the stats key was taken off this
    /// wire because of it. A count would be that key coming back under a new name. A yes/no cannot be
    /// rendered as an achievement; it can only answer "is there any reason to write to this person at
    /// all". The sender reads it to decide whether to send, and never prints it.
    /// </summary>
    public bool WorkedInWindow { get; set; }

    /// <summary>How many sessions the ledger still has open as waiting, but the Gateway can no longer
    /// see (#3124). They are NOT listed: a row that cannot be named, opened, or vouched for is not
    /// something to put in front of somebody at 7am, and listing them is what produced 93 rows about
    /// sessions that no longer existed. The owner's ruling, 20 September 2026: "Do not list it. Count
    /// it in one line at most." Absent when none, like every other section here.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LostContactCount { get; set; }

    /// <summary>
    /// An optional single-sentence observation about the day. The Gateway EMITS NOTHING HERE TODAY - it
    /// reports measurements and invents no prose. The member exists because the contract reserves the key;
    /// it is absent from the JSON while null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Observation { get; set; }
}

/// <summary>The resolved reporting window: the UTC range the calendar day covers in the caller's zone.</summary>
public sealed class MorningReportWindowDto
{
    /// <summary>Inclusive start of the reported day, in UTC.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>EXCLUSIVE end of the reported day, in UTC. A day is [start, end).</summary>
    public DateTime EndUtc { get; set; }

    /// <summary>The calendar day being reported on, as the caller supplied it (yyyy-MM-dd).</summary>
    public string Date { get; set; } = "";

    /// <summary>The IANA zone the calendar day was resolved in, as the caller supplied it.</summary>
    public string Tz { get; set; } = "";
}

/// <summary>
/// One row of the needs-your-attention list. The <c>type</c> string is the discriminator the email renderer
/// switches on; the derived shapes carry the fields that row needs plus its deep-link target.
///
/// Polymorphic serialization is declared WITHOUT a type-discriminator name, so System.Text.Json writes the
/// runtime type's properties and adds no synthetic <c>$type</c> key - this class's own
/// <see cref="Type"/> property is the discriminator, and it is the one the contract names.
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(WaitingSessionAttentionDto))]
[JsonDerivedType(typeof(StaleWorktreesAttentionDto))]
[JsonDerivedType(typeof(UnmergedBranchesAttentionDto))]
[JsonDerivedType(typeof(LongRunningSessionsAttentionDto))]
[JsonDerivedType(typeof(UsageLimitStopsAttentionDto))]
[JsonDerivedType(typeof(OutdatedDirectorsAttentionDto))]
public abstract class MorningAttentionItemDto
{
    /// <summary>The item's discriminator: "waiting-session", "stale-worktrees", "unmerged-branches",
    /// "outdated-directors".</summary>
    public abstract string Type { get; }
}

/// <summary>Type strings for <see cref="MorningAttentionItemDto"/>, so producer and tests share one spelling.</summary>
public static class MorningAttentionTypes
{
    public const string WaitingSession = "waiting-session";
    public const string StaleWorktrees = "stale-worktrees";
    public const string UnmergedBranches = "unmerged-branches";
    public const string LongRunningSessions = "long-running-sessions";
    public const string UsageLimitStops = "usage-limit-stops";
    public const string OutdatedDirectors = "outdated-directors";
}

/// <summary>
/// Directors this account is running that are behind the newest published release (#3124). ONE item per
/// account, listing every such Director - three machines behind is one thing to do, not three rows.
/// Present only when the newest release is KNOWN and at least one Director heard from in the last day is
/// provably older than it; a version that cannot be read is never called behind.
/// </summary>
public sealed class OutdatedDirectorsAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.OutdatedDirectors;

    /// <summary>The newest published release the Directors were compared against, e.g. "2.8.1".</summary>
    public string Newest { get; set; } = "";

    public List<OutdatedDirectorDto> Directors { get; set; } = new();
}

/// <summary>One Director that is behind: the machine it runs on and the version it reported.</summary>
public sealed class OutdatedDirectorDto
{
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>
/// Sessions that stopped because the agent's plan ran out of usage, and have not worked since (#3124).
/// ONE item per account. Read from the supervisor's recovery log, which records the fault the moment the
/// turn ends on it - so this is a fact that was observed, never an inference from a session going quiet.
/// A session that died overnight for any OTHER reason is deliberately not here (owner ruling, 19 September
/// 2026: a crash cannot be told from a clean finish, and an email that cries wolf stops being read).
/// </summary>
public sealed class UsageLimitStopsAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.UsageLimitStops;

    public List<UsageLimitStopDto> Sessions { get; set; } = new();
}

/// <summary>One stopped session: its friendly name when the Gateway can see it live, otherwise its id -
/// never blank - and when the limit was hit.</summary>
public sealed class UsageLimitStopDto
{
    public string Session { get; set; } = "";
    public DateTime StoppedUtc { get; set; }
}

/// <summary>
/// Sessions that have been working, without a stop, for unusually long BY THIS ACCOUNT'S OWN MEASURE
/// (#3124). ONE item per account. The rule is the owner's (20 September 2026): still working after three
/// hours AND at least ten times the account's usual working stretch - both, so a long build does not fire
/// it and somebody whose sessions routinely run for hours is not nagged. With too little history to know
/// the usual, the row is absent: it never guesses.
/// </summary>
public sealed class LongRunningSessionsAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.LongRunningSessions;

    /// <summary>The account's usual working stretch, in minutes: the median of its finished stretches over
    /// the lookback. Sent so the email can say what "unusually" was measured against.</summary>
    public double UsualMinutes { get; set; }

    public List<LongRunningSessionDto> Sessions { get; set; } = new();
}

/// <summary>One session still working: its friendly name and how long the current stretch has run.</summary>
public sealed class LongRunningSessionDto
{
    public string Session { get; set; } = "";
    public double RunningHours { get; set; }
}

/// <summary>A session whose last recorded state is waiting on the human, and how long it has been there.</summary>
public sealed class WaitingSessionAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.WaitingSession;

    /// <summary>The session's friendly name. ALWAYS A NAME NOW, never an id (#3124): a row is only
    /// produced for a session the Gateway can see live, and a live session has a name. It was
    /// previously allowed to fall back to the session id, and it did so for every row - 93 of 93 on
    /// 20 September 2026 - which put identifiers nobody can search for in front of a reader.</summary>
    public string Session { get; set; } = "";

    /// <summary>The session's own id, so the reader can be sent straight to it. The email builds the
    /// Cockpit address from this; it is NOT for display.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The repository this session is working in, as a short name rather than a full path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repo { get; set; }

    /// <summary>The machine it is running on, so two seats with similar names can be told apart.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Machine { get; set; }

    /// <summary>The session number the owner sees everywhere else in the product.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Number { get; set; }

    /// <summary>The session DRIVING this one, when something is. Absent means nothing is driving it,
    /// which is the owner's definition of a session that is his own problem (#3124, 20 September
    /// 2026: "only sessions nothing else is driving"). It is also what lets the reader's side work
    /// out how much is stuck BEHIND one answer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ControllerSessionId { get; set; }

    /// <summary>When the session entered its current waiting state, from the durable event ledger.</summary>
    public DateTime WaitingSinceUtc { get; set; }

    /// <summary>How long it has been waiting, in hours, at the moment the report was built.</summary>
    public double AgeHours { get; set; }
}

/// <summary>Stale worktrees in one repository. Only worktrees whose branch is MERGED and whose tip is older
/// than the staleness bar are ever marked <see cref="SafeToRemove"/>.</summary>
public sealed class StaleWorktreesAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.StaleWorktrees;

    public string Repo { get; set; } = "";
    public int Count { get; set; }

    /// <summary>The worktrees' directory BASE NAMES - never full paths.</summary>
    public List<string> Worktrees { get; set; } = new();

    public double OldestAgeDays { get; set; }

    /// <summary>True only when every worktree in this item is old AND its branch is merged into the
    /// default branch. An unmerged stale worktree is reported as its own item with this false.</summary>
    public bool SafeToRemove { get; set; }
}

/// <summary>Branches in one repository that are unmerged to the default branch, oldest first.</summary>
public sealed class UnmergedBranchesAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.UnmergedBranches;

    public string Repo { get; set; } = "";
    public List<UnmergedBranchDto> Branches { get; set; } = new();

    /// <summary>How many of this repository's branches could NOT be placed either way: whether they are
    /// merged was not determined, or they are unmerged with no tip date to age them by. They are counted
    /// here rather than named in <see cref="Branches"/> - the report does not know they are unfinished
    /// work, only that it could not rule it out - and rather than dropped, which made the email's count
    /// read as the whole truth when it was the part that could be worked out (#3124). Absent when zero.
    /// An item may carry this with an EMPTY <see cref="Branches"/>: nothing proven, some unknown.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Undetermined { get; set; }
}

/// <summary>One unmerged branch: its name, how old its tip is, and how many commits it carries.</summary>
public sealed class UnmergedBranchDto
{
    public string Name { get; set; } = "";
    public double AgeDays { get; set; }
    public int Commits { get; set; }
}
