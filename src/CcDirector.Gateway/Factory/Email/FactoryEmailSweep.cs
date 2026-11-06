using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Tenancy;
using YourInboxStore = CcDirector.Gateway.Inbox.YourInboxStore;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>The facts one factory email is composed from: the account's open problems as of one instant, and each
/// factory's last day, read together from the one problems fold.</summary>
public delegate (CronRunProblemsDto Problems, IReadOnlyList<CronFactoryDayDto> Days) FactoryEmailFacts(
    TenantId tenant, DateTime dayStartUtc, DateTime dayEndUtc, DateTime asOfUtc);

/// <summary>A factory's summary from since up to and including until: what came in and the decision on each - the same
/// summary the factory thread's Summary button shows (<c>FactoryInboxStore.Summary</c>).</summary>
public delegate FactoryInboxSummaryDto FactorySummaryOf(TenantId tenant, string factory, DateTime sinceUtc, DateTime untilUtc);

/// <summary>
/// THE GATEWAY WRITES THE 08:00 MORNING SUMMARY, AS PLAIN CODE - NOT AN AGENT (the owner, 10 October 2026), AND IT IS NO
/// LONGER AN EMAIL (the owner's decision, Factory Inbox mission, phase 5 "Email off": "Factories stop emailing me").
/// For each account whose morning summary is switched on, once a day at 08:00 in the account's own time zone
/// (<see cref="FactoryEmailSlots"/>): compose the account's morning report from the one problems fold and keep it, then
/// write ONE Your inbox message per factory with news - the factory's summary of the last 24 hours (the Summary button's), followed
/// by that factory's part of the report (<see cref="FactoryMorningMessage"/>). Nothing is emailed: this class holds no
/// mail sender of any kind, so it cannot send one.
///
/// ONCE PER SLOT, AND ONCE PER FACTORY. The kept row per account and slot (<see cref="FactoryEmailStore"/>) is the record
/// that a slot was handled, written BEFORE the messages and marked delivered after them, so a crash in between leaves a
/// slot that is tried again on the next tick rather than one silently lost. A factory's message is found again by its
/// slot (<see cref="YourInboxStore.MorningOf"/>), so a slot tried again writes only the messages still missing.
///
/// SHIPS SWITCHED OFF. <c>factory_email_enabled</c> is off unless an account turns it on; switched off, a tick does
/// nothing at all for that account - it composes nothing, keeps nothing and writes nothing.
/// </summary>
public sealed class FactoryEmailSweep : TenantScopedSweep
{
    /// <summary>What the kept row says once the morning summaries are written.</summary>
    public static string DeliveredDetail(int written) =>
        $"Written to Your inbox as {written} morning {(written == 1 ? "summary" : "summaries")}, one per factory. No email was sent.";

    private readonly ITenantContext _tenantContext;
    private readonly Func<TenantId, bool> _enabled;
    private readonly Func<TenantId, TimeZoneInfo> _zoneOf;
    private readonly FactoryEmailFacts _facts;
    private readonly Func<TenantId, IReadOnlyDictionary<string, FactoryEmailFactory>> _factories;
    private readonly Func<string?> _baseUrl;
    private readonly FactoryEmailStore _store;
    private readonly YourInboxStore _inbox;
    private readonly FactorySummaryOf _summaryOf;
    private readonly Func<TenantId, DateTime, DateTime, TimeZoneInfo, MailDesk.MailDeskEmailSection?> _mailDesk;
    private readonly Func<TenantId, FactoryEmailNeedsYou> _needsYou;
    private readonly Func<DateTime> _now;

    /// <param name="enabled">Whether an account's morning summary is on (the <c>factory_email_enabled</c> setting).</param>
    /// <param name="zoneOf">The account's display time zone.</param>
    /// <param name="facts">The open problems and the factories' last day (<c>CronRunResultService.ProblemsAndFactoryDays</c>).</param>
    /// <param name="factories">The account's registered factories, for titles and seat names.</param>
    /// <param name="baseUrl">This Gateway's public base address, or null when it has none.</param>
    /// <param name="store">The kept morning reports, one row per account and slot.</param>
    /// <param name="inbox">Your inbox, where each factory's morning summary is written.</param>
    /// <param name="summaryOf">Each factory's summary (<c>FactoryInboxStore.Summary</c>).</param>
    /// <param name="mailDesk">The 08:00 report's Mail Desk section for (account, window start, window end, time zone), or
    /// null when the account's Mail Desk is off or has no mailbox (<see cref="MailDesk.MailDeskEmailFold.Section"/>).
    /// REQUIRED, so a wiring line that forgot it fails to compile rather than leaving the section out.</param>
    /// <param name="needsYou">What waits on the owner now: the open asked and escalated items of the factory activity
    /// record (<see cref="FactoryAgentsFold.OpenWaiting"/>). REQUIRED, for the same reason.</param>
    public FactoryEmailSweep(HostedTenantBoundary boundary, TenantRegistry tenants, ITenantContext tenantContext,
        Func<TenantId, bool> enabled, Func<TenantId, TimeZoneInfo> zoneOf, FactoryEmailFacts facts,
        Func<TenantId, IReadOnlyDictionary<string, FactoryEmailFactory>> factories, Func<string?> baseUrl,
        FactoryEmailStore store, YourInboxStore inbox, FactorySummaryOf summaryOf,
        Func<TenantId, DateTime, DateTime, TimeZoneInfo, MailDesk.MailDeskEmailSection?> mailDesk,
        Func<TenantId, FactoryEmailNeedsYou> needsYou, Func<DateTime>? now = null)
        : base(boundary, tenants)
    {
        _needsYou = needsYou ?? throw new ArgumentNullException(nameof(needsYou));
        _mailDesk = mailDesk ?? throw new ArgumentNullException(nameof(mailDesk));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _zoneOf = zoneOf ?? throw new ArgumentNullException(nameof(zoneOf));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _summaryOf = summaryOf ?? throw new ArgumentNullException(nameof(summaryOf));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>One tick, over every account.</summary>
    public Task SweepAsync(CancellationToken ct = default) =>
        ForEachTenantAsync(() => SweepTenantAsync(_tenantContext.Current, ct), ct);

    /// <summary>
    /// One tick for one account, inside its scope. Returns what it did, in words, for the log and the tests.
    /// </summary>
    public Task<string> SweepTenantAsync(TenantId tenant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_enabled(tenant))
            return Task.FromResult("off");

        var now = _now();
        var zone = _zoneOf(tenant);
        var slot = FactoryEmailSlots.DueAt(now, zone);
        if (slot is null)
            return Task.FromResult("no slot due");

        var row = _store.Find(tenant, slot.Key);
        if (row is not null && row.State != FactoryEmailStates.Failed)
            return Task.FromResult($"slot {slot.Key} already {row.State}");

        FileLog.Write($"[FactoryEmailSweep] SweepTenantAsync: composing slot={slot.Key}, kind={slot.Kind}, retry={row is not null}, tenant={tenant.ToLogString()}");
        // As of the slot's own cut-off, not of this tick: a summary composed late in its window still describes its
        // slot - a problem raised after the cut-off waits for the next morning, and one resolved after it is still
        // listed here, as it was open then.
        var (problems, days) = _facts(tenant, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var input = new FactoryEmailInput(slot, zone, problems.Problems, days, _factories(tenant), _baseUrl(),
            _mailDesk(tenant, slot.DayStartUtc, slot.BoundaryUtc, zone), _needsYou(tenant));
        var report = FactoryEmailComposer.Compose(input);
        if (row is null)
        {
            if (!report.Send)
            {
                _store.Save(tenant, slot.Key, r => Fill(r, slot, now, FactoryEmailStates.NothingToSend, report.NotSentReason, report));
                return Task.FromResult($"slot {slot.Key}: nothing to send ({report.NotSentReason})");
            }
            // Kept BEFORE the messages: a crash between the two leaves a slot to try again, never one silently lost.
            _store.Save(tenant, slot.Key, r => Fill(r, slot, now, FactoryEmailStates.Failed, "composed, not written to Your inbox yet", report));
        }

        // One message per factory with news: a factory where nothing came in, nothing was decided, nothing ran, nothing is
        // open and nothing waits on the owner writes none, so a quiet factory does not add an unread message every day.
        var written = 0;
        var all = 0;
        foreach (var factory in FactoryMorningMessage.FactoriesOf(input))
        {
            if (_inbox.MorningOf(tenant, factory, slot.BoundaryUtc) is not null)
            {
                all++;
                continue;
            }
            var summary = _summaryOf(tenant, factory, slot.DayStartUtc, slot.BoundaryUtc);
            if (!FactoryMorningMessage.HasNews(input, factory, summary))
                continue;
            var message = FactoryMorningMessage.Compose(input, factory, summary);
            _inbox.Add(tenant, YourInboxStore.ForMorningSummary(tenant, factory, message.Subject, message.Body, slot.BoundaryUtc));
            written++;
            all++;
        }

        _store.Save(tenant, slot.Key, r =>
        {
            r.Attempts++;
            r.State = FactoryEmailStates.Sent;
            r.Detail = DeliveredDetail(all);
            r.SentAtUtc = now;
            r.ProviderId = null;
        });
        _store.Prune(tenant, now);
        FileLog.Write($"[FactoryEmailSweep] SweepTenantAsync: slot={slot.Key} wrote {written} of {all} morning summaries, tenant={tenant.ToLogString()}");
        return Task.FromResult($"slot {slot.Key}: wrote {written} morning {(written == 1 ? "summary" : "summaries")} to Your inbox");
    }

    private static void Fill(Data.Entities.FactoryEmailReportEntity r, FactoryEmailSlot slot, DateTime now, string state,
        string? detail, FactoryEmail email)
    {
        r.Kind = slot.Kind;
        r.BoundaryUtc = slot.BoundaryUtc;
        r.ComposedAtUtc = now;
        r.State = state;
        r.Detail = detail is null ? null : Cap(detail, 1024);
        r.Subject = email.Subject;
        r.Html = email.Html;
        r.Text = email.Text;
        r.ProblemCount = email.ProblemCount;
        r.FactoriesJson = System.Text.Json.JsonSerializer.Serialize(email.Factories);
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}
