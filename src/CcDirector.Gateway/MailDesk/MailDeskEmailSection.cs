using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE 08:00 EMAIL'S MAIL DESK SECTION, FOLDED ON THE GATEWAY (Mail Desk mission, phase 3 contract, section 9). Every
/// word the section shows is decided here; <c>FactoryEmailComposer</c> only lays it out. It covers the same 24 hours
/// as the email.
///
/// THE SEVEN PARTS:
///   1. the counts by decision, every decision listed with zeros, per mailbox and in total;
///   2. every junked email - mailbox, sender, subject, layer and reason - never a sample, so a wrong junk is seen;
///   3. every email kept for the owner, by sender and subject;
///   4. every email that could not be screened (decided <c>failed</c>: the spend limit, or a failure still being
///      retried), and every email kept for the owner after three failed screenings, with the reason;
///   5. every recorded change to the Mail Desk in the window: what, who in words, and why;
///   6. the spend and the limit, and the spend sentence when the limit stopped the model;
///   7. a line saying nothing was moved, when every mailbox is in observe mode.
///
/// ABSENT - null - when the account's switch is off or it has no active channel, so the email reads exactly as before.
/// </summary>
public sealed class MailDeskEmailFold
{
    /// <summary>The section's heading.</summary>
    public const string Heading = "Mail Desk";

    /// <summary>Part 7: said when every active mailbox is in observe mode.</summary>
    public const string ObserveLine = "Every mailbox is in observe mode: the Mail Desk decided and recorded each email, and moved nothing.";

    /// <summary>Each decision in the words the section counts it under, in <see cref="MailDeskWords.Decisions"/>'s order.</summary>
    public static readonly IReadOnlyDictionary<string, string> DecisionLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MailDeskWords.Decisions.Route] = "Routed to a seat",
        [MailDeskWords.Decisions.Archive] = "Archived",
        [MailDeskWords.Decisions.Junk] = "Junked",
        [MailDeskWords.Decisions.KeepForOwner] = "Kept for you",
        [MailDeskWords.Decisions.MailDeskAgent] = "For the Mail Desk seat",
        [MailDeskWords.Decisions.Failed] = "Could not be screened",
    };

    private readonly Func<TenantId, bool> _isOn;
    private readonly MailDeskChannelStore _channels;
    private readonly MailDeskDecisionStore _decisions;
    private readonly MailDeskRulesStore _rules;
    private readonly Func<TenantId, MailDeskSpend> _spendOf;

    /// <param name="isOn">The account's Mail Desk switch.</param>
    /// <param name="channels">The account's channels.</param>
    /// <param name="decisions">The decisions of the window.</param>
    /// <param name="rules">Where the recorded changes are read.</param>
    /// <param name="spendOf">The account's spend - the same value the spend problem is folded from.</param>
    public MailDeskEmailFold(Func<TenantId, bool> isOn, MailDeskChannelStore channels, MailDeskDecisionStore decisions,
        MailDeskRulesStore rules, Func<TenantId, MailDeskSpend> spendOf)
    {
        _isOn = isOn ?? throw new ArgumentNullException(nameof(isOn));
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _spendOf = spendOf ?? throw new ArgumentNullException(nameof(spendOf));
    }

    /// <summary>
    /// The section for the window from <paramref name="fromUtc"/> to <paramref name="toUtc"/> (exclusive), or null when
    /// the switch is off or the account has no active channel.
    /// </summary>
    public MailDeskEmailSection? Section(TenantId tenant, DateTime fromUtc, DateTime toUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!_isOn(tenant)) return null;
        var every = _channels.List(tenant, includeRemoved: true);
        var active = every.Where(c => c.RemovedUtc is null).ToList();
        if (active.Count == 0) return null;

        var decided = _decisions.DecidedBetween(tenant, fromUtc, toUtc);
        var changes = _rules.ChangesBetween(tenant, fromUtc, toUtc);
        var section = Fold(every, decided, changes, _spendOf(tenant), zone);
        FileLog.Write($"[MailDeskEmailFold] Section: tenant={tenant.ToLogString()}, mailboxes={section.Mailboxes.Count}, decided={section.Total.Total}, junked={section.Junked.Count}, changes={section.Changes.Count}");
        return section;
    }

    /// <summary>
    /// The pure fold of the section from its facts. <paramref name="channels"/> holds every channel of the account,
    /// removed ones too: a removed mailbox is counted when it decided an email in the window, and its emails are named
    /// by its address. It must hold at least one active channel.
    /// </summary>
    public static MailDeskEmailSection Fold(IReadOnlyList<MailDeskChannelEntity> channels, IReadOnlyList<MailDeskDecisionEntity> decided,
        IReadOnlyList<MailDeskListChangeEntity> changes, MailDeskSpend spend, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(decided);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(zone);
        var active = channels.Where(c => c.RemovedUtc is null).ToList();
        if (active.Count == 0)
            throw new ArgumentException("The Mail Desk section is only folded for an account with an active channel.", nameof(channels));
        var byId = channels.ToDictionary(c => c.Id);
        string AddressOf(MailDeskDecisionEntity d) => byId.TryGetValue(d.ChannelId, out var c)
            ? c.Address
            : throw new InvalidOperationException($"Mail Desk decision {d.Id} names channel {d.ChannelId}, which this account does not have.");

        // 1. The counts: every active mailbox, and a removed one only when it decided something in the window.
        var decidedChannels = decided.Select(d => d.ChannelId).ToHashSet();
        var mailboxes = channels
            .Where(c => c.RemovedUtc is null || decidedChannels.Contains(c.Id))
            .OrderBy(c => c.CreatedUtc).ThenBy(c => c.Address, StringComparer.Ordinal)
            .Select(c => new MailDeskEmailMailbox(
                c.RemovedUtc is null ? c.Address : $"{c.Address} (removed)",
                Counts(MailDeskDecisionCounts.From(decided.Where(d => d.ChannelId == c.Id).Select(d => (d.Decision, 1))))))
            .ToList();
        var total = MailDeskDecisionCounts.From(decided.Select(d => (d.Decision, 1)));

        MailDeskEmailMessage Message(MailDeskDecisionEntity d) => new(AddressOf(d), d.SenderAddress, d.Subject, d.Layer, d.Reason);

        // 2 to 4. Every one, oldest first; an email kept after three failed screenings is listed with what could not
        // be screened, where its reason explains it, and not again among the emails kept for the owner.
        var junked = decided.Where(d => d.Decision == MailDeskWords.Decisions.Junk).Select(Message).ToList();
        var kept = decided.Where(d => d.Decision == MailDeskWords.Decisions.KeepForOwner && !MailDeskReasons.IsKeptAfterFailures(d.Reason))
            .Select(Message).ToList();
        var notScreened = decided.Where(d => d.Decision == MailDeskWords.Decisions.Failed
                || (d.Decision == MailDeskWords.Decisions.KeepForOwner && MailDeskReasons.IsKeptAfterFailures(d.Reason)))
            .Select(Message).ToList();

        // 5. Every change, oldest first.
        var changeRows = changes.OrderBy(c => c.AtUtc).Select(c => new MailDeskEmailChange(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(c.AtUtc, DateTimeKind.Utc), zone).ToString("HH:mm", CultureInfo.InvariantCulture),
            WhatInWords(c),
            FactoryPublisher.Words(c.WhoKind, c.WhoId, c.WhoName),
            c.Why)).ToList();

        // 6 and 7.
        var observe = active.All(c => c.Mode == MailDeskWords.Modes.Observe) ? ObserveLine : null;
        return new MailDeskEmailSection(
            Factory: active[0].Factory,
            Mailboxes: mailboxes,
            Total: Counts(total),
            Junked: junked,
            KeptForOwner: kept,
            NotScreened: notScreened,
            Changes: changeRows,
            SpendLine: spend.Line,
            SpendStoppedSentence: spend.Stopped ? spend.StoppedSentence : null,
            ObserveLine: observe);
    }

    private static MailDeskEmailCounts Counts(MailDeskDecisionCounts counts) => new(counts.Total,
        MailDeskWords.Decisions.All.Select(d => new MailDeskEmailCount(d, DecisionLabels[d], counts.ByDecision[d])).ToList());

    /// <summary>
    /// A recorded change in words: "Allow-list entry added: news@example.org". Every kind of change has its words, and
    /// a kind without them is an error rather than a change quietly described wrong.
    /// </summary>
    public static string WhatInWords(MailDeskListChangeEntity change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var thing = change.Kind switch
        {
            MailDeskWords.ChangeKinds.Rule => "Rule",
            MailDeskWords.ChangeKinds.Allow => "Allow-list entry",
            MailDeskWords.ChangeKinds.Block => "Block list entry",
            MailDeskWords.ChangeKinds.Route => "Route",
            MailDeskWords.ChangeKinds.Channel => "Mailbox",
            MailDeskWords.ChangeKinds.Mode => "Mode of",
            MailDeskWords.ChangeKinds.Setting => "Setting",
            _ => throw new InvalidOperationException(
                $"Mail Desk change {change.Id} is of the kind '{change.Kind}', which has no words. Add it to MailDeskEmailFold.WhatInWords."),
        };
        if (change.Kind == MailDeskWords.ChangeKinds.Mode)
            return $"Mode of {change.Subject} set: {ModeChange(change.Detail)}";
        if (change.Kind == MailDeskWords.ChangeKinds.Setting)
            return $"Setting {change.Subject} set: {SettingChange(change.Detail)}";
        var done = change.Action switch
        {
            MailDeskWords.ChangeActions.Add => "added",
            MailDeskWords.ChangeActions.Remove => "removed",
            MailDeskWords.ChangeActions.Set => "changed",
            _ => throw new InvalidOperationException(
                $"Mail Desk change {change.Id} did '{change.Action}', which has no words. Add it to MailDeskEmailFold.WhatInWords."),
        };
        return $"{thing} {done}: {change.Subject}";
    }

    /// <summary>A recorded setting change's numbers, as stored (phase 3, section 7): "0.90 to 0.85".</summary>
    private static string SettingChange(string detail)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(detail);
        var root = doc.RootElement;
        if (!root.TryGetProperty("from", out var from) || from.ValueKind != System.Text.Json.JsonValueKind.Number
            || !root.TryGetProperty("to", out var to) || to.ValueKind != System.Text.Json.JsonValueKind.Number)
            throw new InvalidOperationException($"A recorded Mail Desk setting change carries no numbers 'from' and 'to': {detail}");
        return $"{from.GetRawText()} to {to.GetRawText()}";
    }

    private static string ModeChange(string detail)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(detail);
        var root = doc.RootElement;
        var from = root.TryGetProperty("from", out var f) ? f.GetString() : null;
        var to = root.TryGetProperty("to", out var t) ? t.GetString() : null;
        if (from is null || to is null)
            throw new InvalidOperationException($"A recorded Mail Desk mode change carries no 'from' and 'to': {detail}");
        return $"{from} to {to}";
    }
}

/// <summary>
/// THE REASONS THE SCREENING WRITES THAT OTHER PARTS READ (phase 3 contract, section 1): the reason of an email kept
/// for the owner after its screening failed three times. Written and recognised here, once, so the 08:00 section can
/// list such an email with what could not be screened.
/// </summary>
public static class MailDeskReasons
{
    /// <summary>How many failed screenings make an email kept for the owner.</summary>
    public const int FailuresBeforeKept = 3;

    private const string KeptAfterFailuresHead = "screening failed 3 times (";
    private const string KeptAfterFailuresTail = "); kept for the owner";

    /// <summary>"screening failed 3 times (&lt;error kind&gt;); kept for the owner".</summary>
    public static string KeptAfterFailures(string errorKind)
    {
        if (string.IsNullOrWhiteSpace(errorKind))
            throw new ArgumentException("The reason names the kind of error the screening failed with.", nameof(errorKind));
        return KeptAfterFailuresHead + errorKind.Trim() + KeptAfterFailuresTail;
    }

    /// <summary>True when <paramref name="reason"/> is one <see cref="KeptAfterFailures"/> wrote.</summary>
    public static bool IsKeptAfterFailures(string? reason)
        => reason is not null
           && reason.StartsWith(KeptAfterFailuresHead, StringComparison.Ordinal)
           && reason.EndsWith(KeptAfterFailuresTail, StringComparison.Ordinal)
           && reason.Length > KeptAfterFailuresHead.Length + KeptAfterFailuresTail.Length;
}

/// <summary>The 08:00 email's Mail Desk section, every word decided by <see cref="MailDeskEmailFold"/>.</summary>
/// <param name="Factory">The factory the Mail Desk works in, so the email lists it among the factories it covers.</param>
/// <param name="Mailboxes">Part 1: each mailbox's counts.</param>
/// <param name="Total">Part 1: the counts of every mailbox together.</param>
/// <param name="Junked">Part 2: every junked email.</param>
/// <param name="KeptForOwner">Part 3: every email kept for the owner.</param>
/// <param name="NotScreened">Part 4: every email that could not be screened, with the reason.</param>
/// <param name="Changes">Part 5: every recorded change.</param>
/// <param name="SpendLine">Part 6: the spend and the limit.</param>
/// <param name="SpendStoppedSentence">Part 6: the spend problem's sentence, when the limit stopped the model.</param>
/// <param name="ObserveLine">Part 7: said when every mailbox is in observe mode, otherwise null.</param>
public sealed record MailDeskEmailSection(
    string Factory,
    IReadOnlyList<MailDeskEmailMailbox> Mailboxes,
    MailDeskEmailCounts Total,
    IReadOnlyList<MailDeskEmailMessage> Junked,
    IReadOnlyList<MailDeskEmailMessage> KeptForOwner,
    IReadOnlyList<MailDeskEmailMessage> NotScreened,
    IReadOnlyList<MailDeskEmailChange> Changes,
    string SpendLine,
    string? SpendStoppedSentence,
    string? ObserveLine);

/// <summary>One mailbox's counts, under its address.</summary>
public sealed record MailDeskEmailMailbox(string Address, MailDeskEmailCounts Counts);

/// <summary>Counts by decision: the total, and every decision in order with its words, zeros included.</summary>
public sealed record MailDeskEmailCounts(int Total, IReadOnlyList<MailDeskEmailCount> ByDecision);

/// <summary>One decision's count and its words.</summary>
public sealed record MailDeskEmailCount(string Decision, string Label, int Count);

/// <summary>One email as the section names it. The sender and subject are the sender's text, escaped when laid out.</summary>
public sealed record MailDeskEmailMessage(string Mailbox, string Sender, string Subject, string Layer, string Reason);

/// <summary>One recorded change: when (local time), what in words, who in words, and why.</summary>
public sealed record MailDeskEmailChange(string AtText, string What, string Who, string Why);
