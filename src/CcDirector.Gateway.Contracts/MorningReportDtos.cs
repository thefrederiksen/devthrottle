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
public abstract class MorningAttentionItemDto
{
    /// <summary>The item's discriminator: "waiting-session", "stale-worktrees", "unmerged-branches".</summary>
    public abstract string Type { get; }
}

/// <summary>Type strings for <see cref="MorningAttentionItemDto"/>, so producer and tests share one spelling.</summary>
public static class MorningAttentionTypes
{
    public const string WaitingSession = "waiting-session";
    public const string StaleWorktrees = "stale-worktrees";
    public const string UnmergedBranches = "unmerged-branches";
}

/// <summary>A session whose last recorded state is waiting on the human, and how long it has been there.</summary>
public sealed class WaitingSessionAttentionDto : MorningAttentionItemDto
{
    public override string Type => MorningAttentionTypes.WaitingSession;

    /// <summary>The session's friendly name when the Gateway can see it live, otherwise its session id -
    /// never blank, so the email always has something to name the row with.</summary>
    public string Session { get; set; } = "";

    /// <summary>The session's repository path, when a live record supplies one. Absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repo { get; set; }

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
}

/// <summary>One unmerged branch: its name, how old its tip is, and how many commits it carries.</summary>
public sealed class UnmergedBranchDto
{
    public string Name { get; set; } = "";
    public double AgeDays { get; set; }
    public int Commits { get; set; }
}
