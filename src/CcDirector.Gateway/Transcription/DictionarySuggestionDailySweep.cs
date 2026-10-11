using Cronos;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The DAILY dictionary-suggestion scan (devthrottle issue #2115): for each tenant, run one suggestion scan
/// just after midnight in that tenant's OWN time zone (the per-account time zone setting on the Settings
/// page). The timer in <c>GatewayHost</c> ticks this sweep every few minutes; the sweep itself decides, per
/// tenant, whether that tenant's local 00:05 has passed since its last scan - so a tenant in Copenhagen and a
/// tenant in Sydney each get their scan at their own midnight from the same timer.
///
/// THE STORED SCAN ROW IS THE DURABLE MARKER: "has this tenant's daily scan run" is answered by the scan
/// store's <c>ScannedAtUtc</c>, so a Gateway restart never double-runs a tenant's day and never skips one. A
/// tenant with NO stored scan at all is due immediately - the first sweep after this feature ships (or after
/// a new tenant enrolls) seeds the stored result instead of leaving the badge dark until midnight. An
/// explicit "Scan now" also refreshes the marker, which simply means the daily run happens at most once per
/// local day across both triggers.
///
/// Runs through <see cref="TenantScopedSweep"/> (the G8 worker seam) so hosted fan-out, per-tenant scope
/// entry, and per-tenant failure isolation are inherited, not re-implemented.
/// </summary>
public sealed class DictionarySuggestionDailySweep : TenantScopedSweep
{
    /// <summary>Five past midnight, tenant-local: past the midnight boundary (never ON it, so day-rollover
    /// work like log rotation is done) but early enough that the result is waiting in the morning.</summary>
    internal const string DailyCron = "5 0 * * *";

    private static readonly CronExpression DailySchedule = CronExpression.Parse(DailyCron);

    private readonly ITenantContext _tenantContext;
    private readonly DictionarySuggestionService _suggestions;
    private readonly TenantSettingsResolver _settings;
    private readonly TenantAiGate _aiGate;
    private readonly Func<DateTime> _now;

    /// <param name="boundary">The hosted tenant boundary (the seam's scope mechanism). Required.</param>
    /// <param name="tenants">The tenant census the seam fans out over. Required.</param>
    /// <param name="tenantContext">The ambient tenant context; the per-tenant body reads the tenant the seam
    /// entered from here (the seam's sanctioned pattern). Required.</param>
    /// <param name="suggestions">The scan engine. Required.</param>
    /// <param name="settings">The per-tenant settings resolver the time zone is read from. Required.</param>
    /// <param name="aiGate">Whether a tenant's plan includes dictation (Pricing Change mission, B1). The scan screens
    /// candidates with a model, so a tenant without it is skipped. Required.</param>
    /// <param name="now">Clock, injected so tests are deterministic; <see cref="DateTime.UtcNow"/> when null.</param>
    public DictionarySuggestionDailySweep(
        HostedTenantBoundary boundary,
        TenantRegistry tenants,
        ITenantContext tenantContext,
        DictionarySuggestionService suggestions,
        TenantSettingsResolver settings,
        TenantAiGate aiGate,
        Func<DateTime>? now = null)
        : base(boundary, tenants)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _suggestions = suggestions ?? throw new ArgumentNullException(nameof(suggestions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _aiGate = aiGate ?? throw new ArgumentNullException(nameof(aiGate));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>One sweep tick: for each tenant, scan if that tenant's daily run is due, otherwise do nothing.
    /// Cheap when nothing is due (one stored-row read per tenant).</summary>
    public Task SweepAsync(CancellationToken ct = default)
        => ForEachTenantAsync(async () =>
        {
            var tenant = _tenantContext.Current;
            var stored = _suggestions.GetStored(tenant);
            var zone = ResolveZone(tenant);
            if (!IsDue(stored?.ScannedAtUtc, zone, _now().ToUniversalTime()))
                return;

            // THE PLAN (Pricing Change mission, B1): a tenant whose plan has no dictation - or whose plan could not be
            // read - is skipped and logged. Nothing is mined, no model is asked, and the stored marker is not moved, so
            // the scan runs on the first tick after the plan allows it.
            var plan = _aiGate.ForTenant(tenant, EntitlementScopes.Dictation);
            if (!plan.IsAllowed)
            {
                FileLog.Write($"[SuggestionDailySweep] daily scan SKIPPED: tenant={tenant.ToLogString()} - dictation is not on the plan ({plan.Verdict}); no model call");
                return;
            }

            FileLog.Write($"[SuggestionDailySweep] daily scan due: tenant={tenant.ToLogString()} zone={zone.Id}");
            await _suggestions.RunScanAsync(tenant, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Whether a tenant's daily scan is due: true when it never ran, or when the next tenant-local 00:05
    /// after its last run is now in the past. Pure and DST-correct (Cronos does the zone arithmetic), so the
    /// schedule is unit-testable without a host.
    /// </summary>
    internal static bool IsDue(DateTime? lastScanUtc, TimeZoneInfo zone, DateTime nowUtc)
    {
        if (lastScanUtc is null) return true;
        var next = DailySchedule.GetNextOccurrence(
            DateTime.SpecifyKind(lastScanUtc.Value, DateTimeKind.Utc), zone);
        return next is not null && next.Value <= nowUtc;
    }

    private TimeZoneInfo ResolveZone(TenantId tenant)
    {
        // The resolver only returns a validated id (the tenant's setting or the operator default), so this
        // resolves; an unknown id here means the machine's zone database changed and SHOULD fail this
        // tenant's body loudly (the seam isolates it and the next tick retries).
        var id = _settings.TimeZone(tenant);
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}
