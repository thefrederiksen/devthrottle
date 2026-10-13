using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>The facts one factory email is composed from: the account's open problems as of one instant, and each
/// factory's last day, read together from the one problems fold.</summary>
public delegate (CronRunProblemsDto Problems, IReadOnlyList<CronFactoryDayDto> Days) FactoryEmailFacts(
    TenantId tenant, DateTime dayStartUtc, DateTime dayEndUtc, DateTime asOfUtc);

/// <summary>
/// THE GATEWAY CHECKS AND SENDS THE FACTORY EMAIL, AS PLAIN CODE - NOT AN AGENT (the owner, 10 October 2026). For each
/// account whose factory email is switched on, at 08:00, 16:00 and midnight in the account's own time zone
/// (<see cref="FactoryEmailSlots"/>): compose the slot's email from the one problems fold, keep it, and send it through
/// the owner-email primitive (<see cref="IFactoryEmailSender"/>).
///
/// ONCE PER SLOT. The kept row per account and slot (<see cref="FactoryEmailStore"/>) is the record that a slot was
/// handled, so a restart, a deploy or a second tick never sends it again. The row is written BEFORE the send and
/// marked sent after it, so a crash in between leaves a slot that is tried again rather than one silently lost.
/// A failed send is tried again on later ticks, with the body kept from the first composition, until the slot's late
/// window closes. A team account is answered "not supported yet" once and not retried.
///
/// SHIPS SWITCHED OFF. <c>factory_email_enabled</c> is off unless an account turns it on; switched off, a tick does
/// nothing at all for that account - it composes nothing, keeps nothing and sends nothing.
/// </summary>
public sealed class FactoryEmailSweep : TenantScopedSweep
{
    private readonly ITenantContext _tenantContext;
    private readonly Func<TenantId, bool> _enabled;
    private readonly Func<TenantId, TimeZoneInfo> _zoneOf;
    private readonly FactoryEmailFacts _facts;
    private readonly Func<TenantId, IReadOnlyDictionary<string, FactoryEmailFactory>> _factories;
    private readonly Func<string?> _baseUrl;
    private readonly FactoryEmailStore _store;
    private readonly IFactoryEmailSender _sender;
    private readonly Func<DateTime> _now;

    /// <param name="enabled">Whether an account's factory email is on (the <c>factory_email_enabled</c> setting).</param>
    /// <param name="zoneOf">The account's display time zone.</param>
    /// <param name="facts">The open problems and the factories' last day (<c>CronRunResultService.ProblemsAndFactoryDays</c>).</param>
    /// <param name="factories">The account's registered factories, for titles and seat names.</param>
    /// <param name="baseUrl">This Gateway's public base address, or null when it has none.</param>
    public FactoryEmailSweep(HostedTenantBoundary boundary, TenantRegistry tenants, ITenantContext tenantContext,
        Func<TenantId, bool> enabled, Func<TenantId, TimeZoneInfo> zoneOf, FactoryEmailFacts facts,
        Func<TenantId, IReadOnlyDictionary<string, FactoryEmailFactory>> factories, Func<string?> baseUrl,
        FactoryEmailStore store, IFactoryEmailSender sender, Func<DateTime>? now = null)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
        _zoneOf = zoneOf ?? throw new ArgumentNullException(nameof(zoneOf));
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _factories = factories ?? throw new ArgumentNullException(nameof(factories));
        _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>One tick, over every account.</summary>
    public Task SweepAsync(CancellationToken ct = default) =>
        ForEachTenantAsync(() => SweepTenantAsync(_tenantContext.Current, ct), ct);

    /// <summary>
    /// One tick for one account, inside its scope. Returns what it did, in words, for the log and the tests.
    /// </summary>
    public async Task<string> SweepTenantAsync(TenantId tenant, CancellationToken ct = default)
    {
        if (!_enabled(tenant))
            return "off";

        var now = _now();
        var zone = _zoneOf(tenant);
        var slot = FactoryEmailSlots.DueAt(now, zone);
        if (slot is null)
            return "no slot due";

        var row = _store.Find(tenant, slot.Key);
        if (row is not null && row.State != FactoryEmailStates.Failed)
            return $"slot {slot.Key} already {row.State}";

        string subject, text, html;
        if (row is null)
        {
            FileLog.Write($"[FactoryEmailSweep] SweepTenantAsync: composing slot={slot.Key}, kind={slot.Kind}, tenant={tenant.ToLogString()}");
            // As of the slot's own cut-off, not of this tick: an email composed late in its window still describes
            // its slot - a problem raised after the cut-off waits for the next email, and one resolved after it is
            // still listed here, as it was open then.
            var (problems, days) = _facts(tenant, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
            var email = FactoryEmailComposer.Compose(new FactoryEmailInput(
                slot, zone, problems.Problems, days, _factories(tenant), _baseUrl()));
            if (!email.Send)
            {
                _store.Save(tenant, slot.Key, r => Fill(r, slot, now, FactoryEmailStates.NothingToSend, email.NotSentReason, email));
                return $"slot {slot.Key}: nothing to send ({email.NotSentReason})";
            }
            // Kept BEFORE the send: a crash between the two leaves a slot to retry, never one silently lost.
            _store.Save(tenant, slot.Key, r => Fill(r, slot, now, FactoryEmailStates.Failed, "composed, not sent yet", email));
            (subject, text, html) = (email.Subject, email.Text, email.Html);
        }
        else
        {
            FileLog.Write($"[FactoryEmailSweep] SweepTenantAsync: retrying slot={slot.Key}, attempts={row.Attempts}, tenant={tenant.ToLogString()}");
            (subject, text, html) = (row.Subject, row.Text, row.Html);
        }

        FactoryEmailSendResult result;
        try
        {
            result = await _sender.SendAsync(tenant, subject, text, html, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The owner-email service could not be reached. Recorded on the slot, so the failure is visible and the
            // send is tried again on the next tick.
            FileLog.Write($"[FactoryEmailSweep] SweepTenantAsync: send FAILED, service unreachable: {ex.Message}");
            result = FactoryEmailSendResult.Failed($"The owner-email service could not be reached: {ex.Message}");
        }
        _store.Save(tenant, slot.Key, r =>
        {
            r.Attempts++;
            if (result.Sent)
            {
                r.State = FactoryEmailStates.Sent;
                r.Detail = null;
                r.SentAtUtc = now;
                r.ProviderId = result.ProviderId;
            }
            else
            {
                r.State = result.Final ? FactoryEmailStates.NotSupported : FactoryEmailStates.Failed;
                r.Detail = Cap(result.Error ?? "the send failed and gave no reason", 1024);
            }
        });

        if (result.Sent && slot.Kind == FactoryEmailKinds.Morning)
            _store.Prune(tenant, now);
        return result.Sent ? $"slot {slot.Key}: sent" : $"slot {slot.Key}: not sent ({result.Error})";
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
