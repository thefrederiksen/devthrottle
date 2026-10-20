using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE CHANNEL ROW, FOLDED ONCE (Mail Desk mission, phase 1 contract, section 5; repository rule 7: the Gateway owns all
/// ruling). Every value a client shows for a channel - its mode in words, whether its mailbox is signed in, how its
/// last check went, and how many emails it decided today - is decided here, and <c>GET /status</c> and
/// <c>GET /channels</c> both read this one fold, so the two can never say different things about one channel.
///
/// WHERE EACH VALUE COMES FROM:
/// - <c>lastCheck</c>: the account's Mail Desk start's trigger (<see cref="MailDeskStart.Is"/>) - its last check time,
///   outcome and reason. No such trigger means the mail-desk factory is not published; a trigger with no check yet
///   means nothing is known.
/// - <c>signedIn</c>: the channel's OWN kept checks (<see cref="MailDeskChannelCheckStore"/>, phase 3 contract,
///   section 6.2), because one mailbox failing no longer fails the run: <c>failing</c> with the check's own sentence
///   while the channel's newest kept check is a failure, <c>yes</c> once a check has read it, <c>unknown</c> before
///   any check has. A whole run failing says nothing about one mailbox, so it leaves <c>signedIn</c> as it was.
/// - <c>today</c>: the channel's decisions since local midnight in the account's own time zone (the setting behind
///   <c>/gateway/time-zone</c>), every decision word listed with zeros.
/// - The account's day: every decision of the account since local midnight, over EVERY channel, removed ones
///   included - not the sum of the rows folded. Removing a channel keeps its decisions (mission section 5.8), so a
///   channel removed at noon still counts in that day's total, and <c>GET /status</c> agrees with
///   <c>GET /decisions</c> about how many emails were decided today (review of pull request 4231, finding F1).
///
/// The check's own sentence is quoted - it says which machine and the command that fixes it - so a refined sentence
/// reaches the row with no change here.
/// </summary>
public sealed class MailDeskChannelFold
{
    /// <summary>A channel's mode in words, keyed by mode.</summary>
    public static readonly IReadOnlyDictionary<string, string> ModeLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MailDeskWords.Modes.Observe] = "Observe - decides and records, changes nothing",
        [MailDeskWords.Modes.Live] = "Live - decides and acts: files each email and starts the seat it routes to",
    };

    /// <summary>The states <c>signedIn</c> can be in.</summary>
    public static class SignedInStates
    {
        public const string Unknown = "unknown";
        public const string Yes = "yes";
        public const string Failing = "failing";
    }

    /// <summary>The last check's label when the account has no Mail Desk start at all.</summary>
    public const string NotPublishedLabel = "Not checked yet - the mail-desk factory is not published";

    /// <summary>The last check's label when the start exists and has never been checked.</summary>
    public const string NeverCheckedLabel = "Not checked yet - the first check has not run";

    /// <summary>The sign-in label while no check has run.</summary>
    public const string SignedInUnknownLabel = "Not known yet - no check has read the mailbox";

    /// <summary>The sign-in label when the last check read the mailbox.</summary>
    public const string SignedInYesLabel = "Signed in - the last check read the mailbox";

    /// <summary>The sign-in label while the mailbox's newest check failed, before the check's own sentence.</summary>
    public const string SignedInFailingPrefix = "Failing - the last check could not read the mailbox: ";

    private readonly Func<TenantId, IReadOnlyList<TriggerEntity>> _triggersOf;
    private readonly Func<TenantId, string> _timeZoneOf;
    private readonly MailDeskDecisionStore _decisions;
    private readonly MailDeskChannelCheckStore _checks;

    /// <param name="triggersOf">The account's triggers; the Mail Desk start's is found among them.</param>
    /// <param name="timeZoneOf">The account's time zone, an IANA id (<c>TenantSettingsResolver.TimeZone</c>).</param>
    /// <param name="decisions">Where today's counts are read.</param>
    /// <param name="checks">Each mailbox's kept checks, which say whether it is signed in.</param>
    public MailDeskChannelFold(Func<TenantId, IReadOnlyList<TriggerEntity>> triggersOf, Func<TenantId, string> timeZoneOf,
        MailDeskDecisionStore decisions, MailDeskChannelCheckStore checks)
    {
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _triggersOf = triggersOf ?? throw new ArgumentNullException(nameof(triggersOf));
        _timeZoneOf = timeZoneOf ?? throw new ArgumentNullException(nameof(timeZoneOf));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
    }

    /// <summary>
    /// Fold <paramref name="channels"/> into the rows a client shows, and the account's day: its local date, its time
    /// zone and the counts by decision of every channel of the account, whether or not it is among those folded.
    /// </summary>
    public MailDeskFolded Fold(TenantId tenant, IReadOnlyList<MailDeskChannelEntity> channels, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var day = Today(_timeZoneOf(tenant), nowUtc);
        var counts = _decisions.CountsByChannel(tenant, day.FromUtc, day.ToUtc);
        var lastCheck = LastCheckOf(_triggersOf(tenant), day.Zone);
        var checks = _checks.ByChannel(tenant);

        var rows = channels.Select(c => Row(c, lastCheck,
            SignedInOf(checks.TryGetValue(c.Id, out var kept) ? kept : Array.Empty<MailDeskChannelCheckEntity>()),
            counts.TryGetValue(c.Id, out var count) ? count : MailDeskDecisionCounts.Zero)).ToList();
        // The day counts every channel the store has for the account, removed ones too, not only the rows folded.
        var total = MailDeskDecisionCounts.Sum(counts.Values);
        return new MailDeskFolded(rows, new MailDeskDay(day.Date, day.ZoneId, total.Total, total.ByDecision));
    }

    // ---------- the day ----------

    /// <summary>The account's day as instants: from local midnight to the next local midnight, in UTC.</summary>
    internal sealed record LocalDay(string Date, string ZoneId, TimeZoneInfo Zone, DateTime FromUtc, DateTime ToUtc);

    /// <summary>
    /// Today in <paramref name="zoneId"/> as <paramref name="nowUtc"/> sees it. A zone whose clocks jump forward at
    /// midnight has no midnight that day; the day then starts at its first real minute.
    /// </summary>
    internal static LocalDay Today(string zoneId, DateTime nowUtc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var utc = DateTime.SpecifyKind(nowUtc.ToUniversalTime(), DateTimeKind.Utc);
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(utc, zone).Date;
        return new LocalDay(localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zoneId, zone,
            StartOf(localDate, zone), StartOf(localDate.AddDays(1), zone));
    }

    private static DateTime StartOf(DateTime localDate, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    // ---------- signing in: the channel's own kept checks ----------

    /// <summary>Whether a mailbox is signed in, from its kept checks oldest first: its newest says.</summary>
    internal static MailDeskSignedIn SignedInOf(IReadOnlyList<MailDeskChannelCheckEntity> checksOldestFirst)
    {
        if (checksOldestFirst.Count == 0)
            return new MailDeskSignedIn(SignedInStates.Unknown, SignedInUnknownLabel);
        var newest = checksOldestFirst[^1];
        return newest.Ok
            ? new MailDeskSignedIn(SignedInStates.Yes, SignedInYesLabel)
            : new MailDeskSignedIn(SignedInStates.Failing, SignedInFailingPrefix + newest.Sentence);
    }

    // ---------- the last check of the Mail Desk start ----------

    private static MailDeskLastCheck LastCheckOf(IReadOnlyList<TriggerEntity> triggers, TimeZoneInfo zone)
    {
        var starts = triggers.Where(MailDeskStart.Is).ToList();
        if (starts.Count == 0)
            return new MailDeskLastCheck(null, null, NotPublishedLabel);

        // One account has one Mail Desk start; should a second exist, the one checked most recently says how it went.
        var start = starts.OrderByDescending(t => t.LastCheckUtc ?? DateTime.MinValue).First();
        if (start.LastCheckUtc is not { } at)
            return new MailDeskLastCheck(null, null, NeverCheckedLabel);

        var atUtc = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(start.LastReason) ? "the check gave no reason" : start.LastReason.Trim();
        var label = $"Checked {local}: {OutcomeInWords(start.LastOutcome, reason)}";
        return new MailDeskLastCheck(atUtc, start.LastOutcome, label);
    }

    private static string OutcomeInWords(string? outcome, string reason) => outcome switch
    {
        TriggerRunOutcome.NothingToDo => "no new mail",
        TriggerRunOutcome.Started => "new mail, and the seat was started",
        TriggerRunOutcome.Paused => "the start is paused, so no mail was screened",
        // The Mail Desk start starts no seat; its one skipped-running is the screening's (review of 4243, finding 3).
        TriggerRunOutcome.SkippedRunning when reason == Factory.Triggers.TriggerService.ScreeningStillRunning
            => "the previous screening is still running, so this check was not screened",
        TriggerRunOutcome.SkippedRunning => "new mail, and the seat it started last is still running",
        TriggerRunOutcome.Failed => $"failed - {reason}",
        TriggerRunOutcome.Screened => "the mail it read was screened",
        _ => $"'{outcome}'",
    };

    // ---------- the row ----------

    private static MailDeskChannelRow Row(MailDeskChannelEntity c, MailDeskLastCheck lastCheck, MailDeskSignedIn signedIn,
        MailDeskDecisionCounts today) => new(
        Id: c.Id,
        Kind: c.Kind,
        Provider: c.Provider,
        Address: c.Address,
        Machines: MailDeskChannelStore.Machines(c),
        Mode: c.Mode,
        ModeLabel: ModeLabels.TryGetValue(c.Mode, out var label)
            ? label
            : throw new InvalidOperationException($"Mail Desk channel {c.Id} holds the mode '{c.Mode}', which is not a mode."),
        Factory: c.Factory,
        Seat: c.Seat,
        SignedIn: signedIn,
        LastCheck: lastCheck,
        Today: new MailDeskChannelToday(today.Total, today.ByDecision),
        CreatedUtc: DateTime.SpecifyKind(c.CreatedUtc, DateTimeKind.Utc),
        CreatedBy: c.CreatedBy,
        ChangedUtc: DateTime.SpecifyKind(c.ChangedUtc, DateTimeKind.Utc),
        ChangedBy: c.ChangedBy,
        RemovedUtc: c.RemovedUtc is { } removed ? DateTime.SpecifyKind(removed, DateTimeKind.Utc) : null)
    {
        ProviderLabel = ProviderLabelOf(c.Id, c.Provider),
        MachinesLabel = MachinesLabelOf(c.Id, MailDeskChannelStore.Machines(c)),
        ModeTone = ModeToneOf(c.Id, c.Mode),
    };

    // ---------- the words and tones the Mail Desk page shows (phase 7 contract, section 2) ----------
    //
    // A client turns a tone into a colour and shows every label verbatim; it never works one out from the raw words.
    // The tones of signedIn, lastCheck and today are read off their own records, so whoever decides a state - the
    // trigger today, a mailbox's own checks after phase 3 - the tone follows it with no change here.

    /// <summary>The only values a client turns into a colour.</summary>
    public static class Tones
    {
        public const string Good = "good";
        public const string Bad = "bad";
        public const string Neutral = "neutral";
        /// <summary>Stands out on purpose: live mode acts on the mailbox.</summary>
        public const string Attention = "attention";
    }

    /// <summary>What each decision word reads as on the page, keyed by decision.</summary>
    public static readonly IReadOnlyDictionary<string, string> DecisionLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MailDeskWords.Decisions.Route] = "Route to a seat",
        [MailDeskWords.Decisions.Archive] = "Archive",
        [MailDeskWords.Decisions.Junk] = "Junk",
        [MailDeskWords.Decisions.KeepForOwner] = "Keep for you",
        [MailDeskWords.Decisions.MailDeskAgent] = "Hand to the Mail Desk agent",
        [MailDeskWords.Decisions.Failed] = "Could not be screened",
    };

    /// <summary>The heading of the Mail Desk page.</summary>
    public const string PageTitle = "Mail Desk";

    /// <summary>The sentence under the heading of the Mail Desk page.</summary>
    public const string PageSummary =
        "The mailboxes the Mail Desk screens. A check reads each one every few minutes. A mailbox in observe mode only "
        + "records what was decided and changes nothing in it; a mailbox in live mode files each email and starts the seat "
        + "it is routed to.";

    /// <summary>What the Mail Desk page shows when the account has no mailbox on it.</summary>
    public const string PageEmptyLabel =
        "No mailbox is on the Mail Desk yet. Add one with: cc-devthrottle maildesk channel add <address> --provider outlook "
        + "--machine <machine> --why \"<why>\"";

    /// <summary>The provider's name as a reader sees it. A provider with no label is refused, never shown raw.</summary>
    internal static string ProviderLabelOf(Guid channel, string provider)
        => MailDeskWords.Providers.Labels.TryGetValue(provider, out var label)
            ? label
            : throw new InvalidOperationException($"Mail Desk channel {channel} holds the provider '{provider}', which has no label.");

    /// <summary>
    /// The machines that read the mailbox, in the order of preference stored: one is its name; two read
    /// "A, then B if A is not answering"; three or more read "A, then B, then C - each only when the one before it is
    /// not answering".
    /// </summary>
    internal static string MachinesLabelOf(Guid channel, IReadOnlyList<string> machines) => machines.Count switch
    {
        0 => throw new InvalidOperationException($"Mail Desk channel {channel} names no machine to read it."),
        1 => machines[0],
        2 => $"{machines[0]}, then {machines[1]} if {machines[0]} is not answering",
        _ => string.Join(", then ", machines) + " - each only when the one before it is not answering",
    };

    /// <summary>The mode's tone: observe is neutral, live asks for attention.</summary>
    internal static string ModeToneOf(Guid channel, string mode) => mode switch
    {
        MailDeskWords.Modes.Observe => Tones.Neutral,
        MailDeskWords.Modes.Live => Tones.Attention,
        _ => throw new InvalidOperationException($"Mail Desk channel {channel} holds the mode '{mode}', which is not a mode."),
    };

    /// <summary>The sign-in state's tone: yes is good, failing is bad, unknown is neutral.</summary>
    internal static string SignedInToneOf(string state) => state switch
    {
        SignedInStates.Yes => Tones.Good,
        SignedInStates.Failing => Tones.Bad,
        SignedInStates.Unknown => Tones.Neutral,
        _ => throw new InvalidOperationException($"'{state}' is not a sign-in state."),
    };

    /// <summary>The last check's tone: no check yet is neutral, a failed check is bad, any other outcome is good.</summary>
    internal static string LastCheckToneOf(DateTime? utc, string? outcome)
        => utc is null ? Tones.Neutral : outcome == TriggerRunOutcome.Failed ? Tones.Bad : Tones.Good;

    /// <summary>"No emails decided today", "1 email decided today", or "N emails decided today".</summary>
    internal static string TodayLabelOf(int total) => total switch
    {
        0 => "No emails decided today",
        1 => "1 email decided today",
        _ => $"{total.ToString(CultureInfo.InvariantCulture)} emails decided today",
    };

    /// <summary>Every decision word in the order <see cref="MailDeskWords.Decisions"/> lists them, zeros included, with its label.</summary>
    internal static IReadOnlyList<MailDeskDecisionCount> CountsOf(IReadOnlyDictionary<string, int> byDecision)
        => MailDeskWords.Decisions.All.Select(d => new MailDeskDecisionCount(d, DecisionLabels[d],
            byDecision.TryGetValue(d, out var count)
                ? count
                : throw new InvalidOperationException($"Today's counts hold no count for the decision '{d}'."))).ToList();
}

/// <summary>The fold's answer: the rows, and the account's day across them.</summary>
public sealed record MailDeskFolded(IReadOnlyList<MailDeskChannelRow> Channels, MailDeskDay Today);

/// <summary>The account's day: its local date, its time zone, and the counts by decision of every channel of the account,
/// removed ones included.</summary>
public sealed record MailDeskDay(string Date, string TimeZone, int Total, IReadOnlyDictionary<string, int> ByDecision);

/// <summary>One channel as a client shows it (contract section 5). Every value is decided by <see cref="MailDeskChannelFold"/>.</summary>
public sealed record MailDeskChannelRow(
    Guid Id,
    string Kind,
    string Provider,
    string Address,
    IReadOnlyList<string> Machines,
    string Mode,
    string ModeLabel,
    string Factory,
    string Seat,
    MailDeskSignedIn SignedIn,
    MailDeskLastCheck LastCheck,
    MailDeskChannelToday Today,
    DateTime CreatedUtc,
    string CreatedBy,
    DateTime ChangedUtc,
    string ChangedBy,
    DateTime? RemovedUtc)
{
    /// <summary>The provider's name as a reader sees it: <c>Outlook</c>.</summary>
    public required string ProviderLabel { get; init; }

    /// <summary>The machines that read the mailbox, in order of preference, as one sentence.</summary>
    public required string MachinesLabel { get; init; }

    /// <summary><c>neutral</c> in observe mode, <c>attention</c> in live mode.</summary>
    public required string ModeTone { get; init; }
}

/// <summary>Whether the channel's mailbox is signed in: <c>unknown</c>, <c>yes</c> or <c>failing</c>, and the words.</summary>
public sealed record MailDeskSignedIn(string State, string Label)
{
    /// <summary><c>good</c>, <c>bad</c> or <c>neutral</c> (<see cref="MailDeskChannelFold.SignedInToneOf"/>).</summary>
    public string Tone => MailDeskChannelFold.SignedInToneOf(State);
}

/// <summary>The last check of the Mail Desk start: when, its outcome, and the words. Both null before any check.</summary>
public sealed record MailDeskLastCheck(DateTime? Utc, string? Outcome, string Label)
{
    /// <summary><c>good</c>, <c>bad</c> or <c>neutral</c> (<see cref="MailDeskChannelFold.LastCheckToneOf"/>).</summary>
    public string Tone => MailDeskChannelFold.LastCheckToneOf(Utc, Outcome);
}

/// <summary>The channel's decisions today, every decision word listed with zeros.</summary>
public sealed record MailDeskChannelToday(int Total, IReadOnlyDictionary<string, int> ByDecision)
{
    /// <summary>How many emails were decided today, as one sentence.</summary>
    public string Label => MailDeskChannelFold.TodayLabelOf(Total);

    /// <summary>Every decision word in order, zeros included, each with its label.</summary>
    public IReadOnlyList<MailDeskDecisionCount> Counts => MailDeskChannelFold.CountsOf(ByDecision);
}

/// <summary>One decision word's count today, with the words a reader sees for it.</summary>
public sealed record MailDeskDecisionCount(string Decision, string Label, int Count);
