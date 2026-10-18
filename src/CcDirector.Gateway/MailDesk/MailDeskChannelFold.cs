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
/// - <c>lastCheck</c> and <c>signedIn</c>: the account's Mail Desk start's trigger (<see cref="MailDeskStart.Is"/>) -
///   its last check time, outcome and reason. No such trigger means the mail-desk factory is not published; a trigger
///   with no check yet means nothing is known.
/// - <c>today</c>: the channel's decisions since local midnight in the account's own time zone (the setting behind
///   <c>/gateway/time-zone</c>), every decision word listed with zeros.
/// - The account's day: every decision of the account since local midnight, over EVERY channel, removed ones
///   included - not the sum of the rows folded. Removing a channel keeps its decisions (mission section 5.8), so a
///   channel removed at noon still counts in that day's total, and <c>GET /status</c> agrees with
///   <c>GET /decisions</c> about how many emails were decided today (review of pull request 4231, finding F1).
///
/// Phase 2 may refine what the check reports (a sign-in failure says which machine and the command that fixes it);
/// the check's own reason is quoted, so a refined reason reaches the row with no change here.
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

    private readonly Func<TenantId, IReadOnlyList<TriggerEntity>> _triggersOf;
    private readonly Func<TenantId, string> _timeZoneOf;
    private readonly MailDeskDecisionStore _decisions;

    /// <param name="triggersOf">The account's triggers; the Mail Desk start's is found among them.</param>
    /// <param name="timeZoneOf">The account's time zone, an IANA id (<c>TenantSettingsResolver.TimeZone</c>).</param>
    /// <param name="decisions">Where today's counts are read.</param>
    public MailDeskChannelFold(Func<TenantId, IReadOnlyList<TriggerEntity>> triggersOf, Func<TenantId, string> timeZoneOf,
        MailDeskDecisionStore decisions)
    {
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

        var rows = channels.Select(c => Row(c, lastCheck,
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

    // ---------- the last check, and what it says about signing in ----------

    private sealed record LastCheck(MailDeskLastCheck Row, MailDeskSignedIn SignedIn);

    private static LastCheck LastCheckOf(IReadOnlyList<TriggerEntity> triggers, TimeZoneInfo zone)
    {
        var starts = triggers.Where(MailDeskStart.Is).ToList();
        if (starts.Count == 0)
            return new LastCheck(new MailDeskLastCheck(null, null, NotPublishedLabel),
                new MailDeskSignedIn(SignedInStates.Unknown, SignedInUnknownLabel));

        // One account has one Mail Desk start; should a second exist, the one checked most recently says how it went.
        var start = starts.OrderByDescending(t => t.LastCheckUtc ?? DateTime.MinValue).First();
        if (start.LastCheckUtc is not { } at)
            return new LastCheck(new MailDeskLastCheck(null, null, NeverCheckedLabel),
                new MailDeskSignedIn(SignedInStates.Unknown, SignedInUnknownLabel));

        var atUtc = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var failed = start.LastOutcome == TriggerRunOutcome.Failed;
        var reason = string.IsNullOrWhiteSpace(start.LastReason) ? "the check gave no reason" : start.LastReason.Trim();
        var label = $"Checked {local}: {OutcomeInWords(start.LastOutcome, reason)}";
        var signedIn = failed
            ? new MailDeskSignedIn(SignedInStates.Failing, $"Failing - the last check failed: {reason}")
            : new MailDeskSignedIn(SignedInStates.Yes, SignedInYesLabel);
        return new LastCheck(new MailDeskLastCheck(atUtc, start.LastOutcome, label), signedIn);
    }

    private static string OutcomeInWords(string? outcome, string reason) => outcome switch
    {
        TriggerRunOutcome.NothingToDo => "no new mail",
        TriggerRunOutcome.Started => "new mail, and the seat was started",
        TriggerRunOutcome.Paused => "new mail, but the start is paused",
        TriggerRunOutcome.SkippedRunning => "new mail, and the seat it started last is still running",
        TriggerRunOutcome.Failed => $"failed - {reason}",
        _ => $"'{outcome}'",
    };

    // ---------- the row ----------

    private static MailDeskChannelRow Row(MailDeskChannelEntity c, LastCheck lastCheck, MailDeskDecisionCounts today) => new(
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
        SignedIn: lastCheck.SignedIn,
        LastCheck: lastCheck.Row,
        Today: new MailDeskChannelToday(today.Total, today.ByDecision),
        CreatedUtc: DateTime.SpecifyKind(c.CreatedUtc, DateTimeKind.Utc),
        CreatedBy: c.CreatedBy,
        ChangedUtc: DateTime.SpecifyKind(c.ChangedUtc, DateTimeKind.Utc),
        ChangedBy: c.ChangedBy,
        RemovedUtc: c.RemovedUtc is { } removed ? DateTime.SpecifyKind(removed, DateTimeKind.Utc) : null);
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
    DateTime? RemovedUtc);

/// <summary>Whether the channel's mailbox is signed in: <c>unknown</c>, <c>yes</c> or <c>failing</c>, and the words.</summary>
public sealed record MailDeskSignedIn(string State, string Label);

/// <summary>The last check of the Mail Desk start: when, its outcome, and the words. Both null before any check.</summary>
public sealed record MailDeskLastCheck(DateTime? Utc, string? Outcome, string Label);

/// <summary>The channel's decisions today, every decision word listed with zeros.</summary>
public sealed record MailDeskChannelToday(int Total, IReadOnlyDictionary<string, int> ByDecision);
