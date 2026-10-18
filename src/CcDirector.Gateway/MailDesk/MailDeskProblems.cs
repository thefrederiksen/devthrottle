using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Running;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE MAIL DESK'S PROBLEMS, THE PROBLEMS LIST'S THIRD SOURCE (Mail Desk mission, phase 3 contract, sections 6.2 and 7).
/// Pure: every fact arrives in <see cref="MailDeskProblemFacts"/>. <see cref="CronRunResultFold.Problems"/> adds what
/// this answers to the scheduled runs and the definition triggers' checks, under the channel's factory (the mail-desk
/// factory), so the Open problems card and the 08:00 email show them with no change of their own.
///
/// TWO KINDS:
/// - <c>mailbox-failed</c>: one mailbox's failed checks in a row are ONE problem, named by the first failed check and
///   counted in <see cref="CronRunProblemDto.Occurrences"/>, with the sentence "&lt;address&gt; could not be checked on
///   &lt;machine&gt;: &lt;the check's sentence&gt;" - the newest failure's machine and sentence, because the newest names
///   the fix that is needed now. Cleared by that mailbox's next good check; resolved by hand, it stays resolved, and a
///   failure after the resolve starts a new problem - exactly the rule of a definition trigger's failed checks.
/// - <c>spend-limit-reached</c>: one per account, open while the spend limit has stopped a model call and the limit is
///   still at or under what was spent. It is not resolved by hand: raising the limit is what clears it.
/// </summary>
public static class MailDeskProblems
{
    /// <summary>The job name the spend problem is listed under.</summary>
    public const string SpendJobName = "Mail Desk spend limit";

    /// <summary>Every Mail Desk problem of the facts as of <paramref name="asOfUtc"/>, open, cleared and resolved.</summary>
    public static IReadOnlyList<CronRunProblemDto> All(MailDeskProblemFacts facts, TimeZoneInfo zone, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(zone);
        var found = new List<CronRunProblemDto>();
        foreach (var (channel, checks) in facts.Channels)
            found.AddRange(MailboxProblems(channel, checks, zone, asOfUtc));
        if (SpendProblem(facts.Spend, zone, asOfUtc) is { } spend)
            found.Add(spend);
        return found;
    }

    /// <summary>One mailbox's problems: its failed checks in a row, each run of them one problem.</summary>
    /// <param name="channel">The mailbox.</param>
    /// <param name="checksOldestFirst">Its kept checks (<see cref="MailDeskChannelCheckStore.ByChannel"/>).</param>
    /// <param name="zone">The account's time zone, for the shift and the time in words.</param>
    /// <param name="asOfUtc">Only checks by this instant are read, and a resolve after it has not happened yet.</param>
    public static IReadOnlyList<CronRunProblemDto> MailboxProblems(MailDeskChannelEntity channel,
        IReadOnlyList<MailDeskChannelCheckEntity> checksOldestFirst, TimeZoneInfo zone, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(checksOldestFirst);
        ArgumentNullException.ThrowIfNull(zone);
        var found = new List<CronRunProblemDto>();
        List<MailDeskChannelCheckEntity>? failures = null;
        foreach (var check in checksOldestFirst.Where(c => AsUtc(c.CheckedUtc) <= asOfUtc))
        {
            if (check.ChannelId != channel.Id)
                throw new ArgumentException($"Mail Desk check {check.Id} is of channel {check.ChannelId}, not of {channel.Id}.", nameof(checksOldestFirst));
            if (!check.Ok)
            {
                var head = failures?[0];
                if (head is not null && head.ResolvedUtc is { } resolved && AsUtc(resolved) <= asOfUtc && AsUtc(check.CheckedUtc) > AsUtc(resolved))
                {
                    found.Add(ProblemOf(channel, failures!, clearedBy: null, zone, asOfUtc));
                    failures = null;
                }
                (failures ??= new List<MailDeskChannelCheckEntity>()).Add(check);
            }
            else if (failures is not null)
            {
                found.Add(ProblemOf(channel, failures, clearedBy: check, zone, asOfUtc));
                failures = null;
            }
        }
        if (failures is not null)
            found.Add(ProblemOf(channel, failures, clearedBy: null, zone, asOfUtc));
        return found;
    }

    private static CronRunProblemDto ProblemOf(MailDeskChannelEntity channel, List<MailDeskChannelCheckEntity> failures,
        MailDeskChannelCheckEntity? clearedBy, TimeZoneInfo zone, DateTime asOfUtc)
    {
        var head = failures[0];
        var newest = failures[^1];
        var checkedUtc = AsUtc(head.CheckedUtc);
        var shift = WorkShifts.ShiftOf(checkedUtc, zone);
        var whenLocal = TimeZoneInfo.ConvertTimeFromUtc(checkedUtc, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        var reason = newest.Sentence!;
        var sentence = $"{channel.Address} could not be checked on {newest.Machine}: {reason}";
        if (failures.Count > 1)
            sentence += $" ({failures.Count.ToString(CultureInfo.InvariantCulture)} checks in a row failed)";
        var dto = new CronRunProblemDto
        {
            RunId = head.Id.ToString("D"),
            JobId = channel.Id.ToString("D"),
            JobName = channel.Address,
            Factory = channel.Factory,
            Seat = channel.Seat,
            ScheduledUtc = checkedUtc,
            StartedUtc = null,
            Shift = shift.Name,
            ShiftText = shift.Text,
            Kind = CronRunProblems.MailboxFailed,
            KindText = CronRunProblems.Words(CronRunProblems.MailboxFailed),
            Reason = reason,
            Sentence = sentence,
            WhenText = "Checked " + whenLocal,
            RaisedAtUtc = checkedUtc,
            Occurrences = failures.Count,
            SessionId = null,
            State = CronRunProblemStates.Open,
        };
        if (head.ResolvedUtc is { } resolved && AsUtc(resolved) <= asOfUtc)
        {
            dto.State = CronRunProblemStates.Resolved;
            dto.ClosedUtc = AsUtc(resolved);
            dto.ResolvedBy = head.ResolvedBy;
            dto.ResolvedReason = head.ResolvedReason;
            dto.StateText = $"resolved by {head.ResolvedBy}: {head.ResolvedReason}";
        }
        else if (clearedBy is not null)
        {
            dto.State = CronRunProblemStates.Cleared;
            dto.ClosedUtc = AsUtc(clearedBy.CheckedUtc);
            dto.ClearedByRunId = clearedBy.Id.ToString("D");
            dto.StateText = "cleared - the next check of this mailbox read it";
        }
        return dto;
    }

    // ---------- the spend ----------

    /// <summary>
    /// The account's spend problem, or null when the limit is not stopping the Mail Desk. Open from the first call the
    /// limit stopped while the limit is at or under what was spent; raising it above the spend clears it, and a
    /// cleared spend problem is not listed (the facts do not say when the limit was raised).
    /// </summary>
    public static CronRunProblemDto? SpendProblem(MailDeskSpend spend, TimeZoneInfo zone, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(zone);
        if (spend.StoppedUtc is not { } stopped || AsUtc(stopped) > asOfUtc || !spend.LimitReached) return null;
        var stoppedUtc = AsUtc(stopped);
        var shift = WorkShifts.ShiftOf(stoppedUtc, zone);
        var whenLocal = TimeZoneInfo.ConvertTimeFromUtc(stoppedUtc, zone).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        return new CronRunProblemDto
        {
            RunId = SpendProblemId(stoppedUtc).ToString("D"),
            JobId = SpendProblemId(stoppedUtc).ToString("D"),
            JobName = SpendJobName,
            Factory = MailDeskWords.DefaultFactory,
            Seat = MailDeskWords.DefaultSeat,
            ScheduledUtc = stoppedUtc,
            StartedUtc = null,
            Shift = shift.Name,
            ShiftText = shift.Text,
            Kind = CronRunProblems.SpendLimitReached,
            KindText = CronRunProblems.Words(CronRunProblems.SpendLimitReached),
            Reason = spend.StoppedSentence,
            Sentence = spend.StoppedSentence,
            WhenText = "Stopped " + whenLocal,
            RaisedAtUtc = stoppedUtc,
            Occurrences = 1,
            SessionId = null,
            State = CronRunProblemStates.Open,
        };
    }

    /// <summary>
    /// The id the spend problem is listed under: the same for one stop, so <c>run resolve</c> can name it and be told
    /// how it clears, and a new id when the limit stops calls again after it was raised.
    /// </summary>
    public static Guid SpendProblemId(DateTime stoppedUtc)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("mail-desk-spend-limit|" + AsUtc(stoppedUtc).Ticks.ToString(CultureInfo.InvariantCulture)));
        return new Guid(bytes);
    }

    /// <summary>The sentence a spend problem refusing a resolve answers with.</summary>
    public const string SpendNotResolvableSentence =
        "The Mail Desk spend problem is not resolved by hand: it clears when the limit is raised above what was spent. Raise it with maildesk set spend-limit <dollars> --why \"<reason>\".";

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>
/// THE ACCOUNT'S MAIL DESK SPEND (phase 3 contract, section 7), the input the spend problem and the 08:00 email's spend
/// line are folded from. Money is whole millionths of a US dollar, as on the decision rows.
/// </summary>
/// <param name="SpentMicros">Every model call the Mail Desk has made for the account, ever: the sum of the cost on its
/// decision rows (<see cref="MailDeskDecisionStore.SpentMicros"/>).</param>
/// <param name="LimitMicros">The account's spend limit (<c>mail_desk_spend_limit_usd</c>; no row is the default of
/// <see cref="DefaultLimitMicros"/>).</param>
/// <param name="StoppedUtc">When the limit FIRST stopped a model call since the limit was last set, or null when it has
/// stopped none under the current limit. Supplied by the screening, which is the only place a call is stopped.</param>
public sealed record MailDeskSpend(long SpentMicros, long LimitMicros, DateTime? StoppedUtc)
{
    /// <summary>The spend limit when the account has set none: 5.00 US dollars (phase 3 contract, section 1).</summary>
    public const long DefaultLimitMicros = 5_000_000;

    /// <summary>True when what was spent is at or over the limit, so no further call is made.</summary>
    public bool LimitReached => SpentMicros >= LimitMicros;

    /// <summary>True when the limit has stopped a call and still stands at or under the spend.</summary>
    public bool Stopped => StoppedUtc is not null && LimitReached;

    /// <summary>The spend in words, for the 08:00 email: "Spent $0.0123 of the $5.00 limit."</summary>
    public string Line => $"Spent {Dollars(SpentMicros, 4)} of the {Dollars(LimitMicros, 2)} limit.";

    /// <summary>The sentence of the spend problem (phase 3 contract, section 7).</summary>
    public string StoppedSentence =>
        $"Mail Desk stopped asking the model: the spend limit of {Dollars(LimitMicros, 2)} is reached ({Dollars(SpentMicros, 4)} spent). " +
        "Raise it with maildesk set spend-limit <dollars> --why \"<reason>\".";

    /// <summary>Millionths of a dollar as dollars with <paramref name="places"/> decimals: "$5.00", "$5.0003".</summary>
    public static string Dollars(long micros, int places)
        => "$" + (micros / 1_000_000m).ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}

/// <summary>What the Mail Desk problems are folded from: each active mailbox with its kept checks, and the spend.</summary>
public sealed record MailDeskProblemFacts(
    IReadOnlyList<(MailDeskChannelEntity Channel, IReadOnlyList<MailDeskChannelCheckEntity> Checks)> Channels,
    MailDeskSpend Spend)
{
    /// <summary>No Mail Desk: the switch is off, or the account has never used it.</summary>
    public static readonly MailDeskProblemFacts None = new(
        Array.Empty<(MailDeskChannelEntity, IReadOnlyList<MailDeskChannelCheckEntity>)>(),
        new MailDeskSpend(0, MailDeskSpend.DefaultLimitMicros, null));
}

/// <summary>
/// WHERE THE MAIL DESK PROBLEMS ARE READ FROM, for <see cref="CronRunResultService"/>: the switch, the channels, their
/// kept checks and the spend. With the switch off the account has no Mail Desk problems at all. A removed channel is
/// checked by no machine, so it can never clear; its problems leave the list with it.
/// </summary>
public sealed class MailDeskProblemSource
{
    private readonly Func<TenantId, bool> _isOn;
    private readonly MailDeskChannelStore _channels;
    private readonly Func<TenantId, MailDeskSpend> _spendOf;

    /// <param name="isOn">The account's Mail Desk switch (<see cref="MailDeskSwitch.IsOn"/>).</param>
    /// <param name="channels">The account's channels.</param>
    /// <param name="checks">The mailboxes' kept checks; also where a mailbox problem is resolved.</param>
    /// <param name="spendOf">The account's spend (<see cref="MailDeskSpend"/>).</param>
    public MailDeskProblemSource(Func<TenantId, bool> isOn, MailDeskChannelStore channels, MailDeskChannelCheckStore checks,
        Func<TenantId, MailDeskSpend> spendOf)
    {
        _isOn = isOn ?? throw new ArgumentNullException(nameof(isOn));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        Checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _spendOf = spendOf ?? throw new ArgumentNullException(nameof(spendOf));
    }

    /// <summary>The mailboxes' kept checks.</summary>
    public MailDeskChannelCheckStore Checks { get; }

    /// <summary>The account's facts, or <see cref="MailDeskProblemFacts.None"/> with the switch off.</summary>
    public MailDeskProblemFacts Facts(TenantId tenant)
    {
        if (!_isOn(tenant)) return MailDeskProblemFacts.None;
        var checks = Checks.ByChannel(tenant);
        var channels = _channels.List(tenant)
            .Select(c => (c, checks.TryGetValue(c.Id, out var rows) ? rows : (IReadOnlyList<MailDeskChannelCheckEntity>)Array.Empty<MailDeskChannelCheckEntity>()))
            .ToList();
        return new MailDeskProblemFacts(channels, _spendOf(tenant));
    }
}
