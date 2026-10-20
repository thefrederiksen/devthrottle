using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Licenses;

/// <summary>
/// THE MINUTE RECORD OF WHAT IS USING THE LICENSE (the usage-by-session mandate). Once a minute, beside the license
/// check, for every DevThrottle account whose machines report their Claude accounts:
/// <list type="bullet">
/// <item>every session in a connected Director's roster is observed - its running token totals, which the Director
/// stamps at each turn end, and the Claude account its machine is on now (a session that is not a Claude Code session
/// is observed with no account: it does not use one);</item>
/// <item>every account's freshest usage reading is kept, so a weekly pace and a calibration can be measured from more
/// than the two hours the in-memory history holds;</item>
/// <item>once an hour, what is past its retention is deleted.</item>
/// </list>
/// Inert for an account whose machines have never reported (<see cref="LicenseStore.HasMachines"/>): nothing is read
/// and nothing written, exactly as the license check.
/// </summary>
public sealed class SessionUsageRecorder
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private readonly SessionUsageStore _usage;
    private readonly LicenseStore _licenses;
    private readonly LicenseReadModel _reads;
    private readonly Func<TenantId, IReadOnlyList<(string DirectorId, SessionDto Session)>> _liveSessions;
    private readonly Func<DateTime> _nowUtc;
    private readonly ConcurrentDictionary<TenantId, DateTime> _prunedAt = new();

    /// <param name="liveSessions">The sessions of every connected Director of a tenant.</param>
    public SessionUsageRecorder(SessionUsageStore usage, LicenseStore licenses, LicenseReadModel reads,
        Func<TenantId, IReadOnlyList<(string DirectorId, SessionDto Session)>> liveSessions, Func<DateTime> nowUtc)
    {
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _licenses = licenses ?? throw new ArgumentNullException(nameof(licenses));
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _liveSessions = liveSessions ?? throw new ArgumentNullException(nameof(liveSessions));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>Record every tenant, each inside its own scope; one tenant's failure never costs another its record.</summary>
    public async Task TickEveryTenantAsync(ITenantPass tenants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        await tenants.ForEachTenantAsync(() =>
        {
            if (tenants.Current is not { } tenant)
                return Task.CompletedTask; // no scope in effect: record nothing, never a tenant of our own choosing
            try
            {
                Record(tenant);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SessionUsageRecorder] TickEveryTenantAsync: the record FAILED for tenant={tenant.ToLogString()} ({ex.GetType().Name}): {ex.Message}");
            }
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>One minute's record for one tenant. Must be called inside the tenant's scope.</summary>
    public void Record(TenantId tenant)
    {
        if (!_licenses.HasMachines(tenant))
            return;
        var now = _nowUtc();
        var overview = _reads.Read(tenant, now);
        var observations = Observations(_liveSessions(tenant), overview);
        var samples = _usage.Observe(tenant, observations, now);
        var kept = _usage.RecordReadings(tenant, Readings(overview));
        FileLog.Write($"[SessionUsageRecorder] Record: tenant={tenant.ToLogString()}, sessions={observations.Count}, samples={samples}, readingsKept={kept}");

        if (!_prunedAt.TryGetValue(tenant, out var last) || now - last >= PruneEvery)
        {
            _usage.Prune(tenant, now);
            _prunedAt[tenant] = now;
        }
    }

    /// <summary>The observation of every live session: who it is, and the account its machine is on now.</summary>
    public static IReadOnlyList<SessionUsageObservation> Observations(
        IReadOnlyList<(string DirectorId, SessionDto Session)> live, LicenseOverview overview)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(overview);
        var accountByDirector = overview.Machines
            .Where(m => !string.IsNullOrWhiteSpace(m.DirectorId))
            .GroupBy(m => m.DirectorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.LastReportAtUtc).First().ActiveAccountEmail, StringComparer.OrdinalIgnoreCase);
        return live
            .Where(l => !string.IsNullOrEmpty(l.Session.SessionId))
            .GroupBy(l => l.Session.SessionId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(l =>
            {
                var s = l.Session;
                var agent = SessionSpendEmitter.ResolveAgentKind(s.Agent);
                var account = agent == "claude" && accountByDirector.TryGetValue(l.DirectorId, out var a) ? a : null;
                return new SessionUsageObservation(
                    s.SessionId, l.DirectorId, agent, s.Number, s.Name, s.MissionId, s.MissionName, s.ControllerSessionId,
                    s.MachineName, s.CurrentModel, s.AssessedState ?? s.ActivityState, s.CreatedAt, account,
                    s.ClaudeSessionId, s.TokenTotals);
            })
            .ToList();
    }

    /// <summary>Every account's freshest reading that carries a number.</summary>
    public static IReadOnlyList<LicenseUsageReadingRecord> Readings(LicenseOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return overview.Accounts
            .Where(a => a.Freshest is { ReadError: null } f && (f.SevenDayUtilization is not null || f.FiveHourUtilization is not null))
            .Select(a => new LicenseUsageReadingRecord(a.AccountEmail, a.Freshest!.JudgedReadAtUtc,
                a.Freshest.SevenDayUtilization, a.Freshest.SevenDayResetsAtUtc,
                a.Freshest.FiveHourUtilization, a.Freshest.FiveHourResetsAtUtc))
            .ToList();
    }
}
