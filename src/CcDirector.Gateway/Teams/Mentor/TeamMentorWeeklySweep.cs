using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams.Mentor;

/// <summary>
/// THE MENTOR'S WEEKLY RUN (devthrottle_internal#2305): for each TEAM's tenant, once the team's last week has closed in
/// the team's own time zone (the tenant's time zone setting) and <see cref="SettleDelay"/> has passed - so a Director's
/// last pushes of the week have landed - write that week through <see cref="TeamMentorWriter"/>. A personal tenant is
/// never visited: a person who never joins a team sees no change anywhere.
///
/// IDEMPOTENT: the stored run row per team-week is the "already ran" marker, so a restart never writes a week twice.
/// Each tick looks at the last closed week AND the one before it: a week whose model could not be reached is left
/// unmarked by the writer and is tried again here, until the following week has closed and the writer records it for
/// good.
/// OFF unless <see cref="TeamMentorSwitch"/> says on (both <c>CC_GATEWAY_TEAMS=1</c> and <c>CC_GATEWAY_TEAM_MENTOR=1</c>):
/// switched off, a tick does nothing at all.
///
/// Runs on <see cref="TenantScopedSweep"/>, so the per-tenant fan-out and the per-tenant failure isolation are inherited.
/// </summary>
public sealed class TeamMentorWeeklySweep : TenantScopedSweep
{
    /// <summary>How long after a week closes before it is written.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromHours(1);

    private readonly bool _enabled;
    private readonly ITenantContext _tenantContext;
    private readonly TeamRegistry _teams;
    private readonly TeamMentorStore _store;
    private readonly TeamMentorWriter _writer;
    private readonly Func<TenantId, string> _timeZoneOf;
    private readonly Func<DateTime> _now;

    /// <param name="enabled">The switch's answer (<see cref="TeamMentorSwitch.IsOn"/>). False: every tick does nothing.</param>
    /// <param name="timeZoneOf">The tenant's time zone id; production passes <see cref="TenantSettingsResolver.TimeZone"/>.</param>
    public TeamMentorWeeklySweep(bool enabled, HostedTenantBoundary boundary, TenantRegistry tenants, ITenantContext tenantContext,
        TeamRegistry teams, TeamMentorStore store, TeamMentorWriter writer, Func<TenantId, string> timeZoneOf, Func<DateTime>? now = null)
        : base(boundary, tenants)
    {
        _enabled = enabled;
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _timeZoneOf = timeZoneOf ?? throw new ArgumentNullException(nameof(timeZoneOf));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>One tick: for each team, the week before its last closed week and then the last closed week, each one
    /// that is due and not yet marked run is written.</summary>
    public Task SweepAsync(CancellationToken ct = default)
    {
        if (!_enabled)
            return Task.CompletedTask;

        return ForEachTenantAsync(async () =>
        {
            var tenant = _tenantContext.Current;
            if (!_teams.IsTeam(tenant))
                return;

            var zone = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneOf(tenant));
            var nowUtc = _now();
            var last = MentorWeek.LastClosed(nowUtc, zone);
            foreach (var week in new[] { last.Previous, last })
            {
                if (!IsDue(week, zone, nowUtc) || _store.HasRun(tenant, week))
                    continue;

                FileLog.Write($"[TeamMentorWeeklySweep] week {week} due for team {tenant.ToLogString()} zone={zone.Id}");
                await _writer.WriteWeekAsync(tenant, week, zone, ct).ConfigureAwait(false);
            }
        }, ct);
    }

    /// <summary>Whether <paramref name="week"/> has closed in <paramref name="zone"/> and settled. Pure.</summary>
    public static bool IsDue(MentorWeek week, TimeZoneInfo zone, DateTime nowUtc)
    {
        var (_, closedUtc) = week.UtcBounds(zone);
        return nowUtc >= closedUtc + SettleDelay;
    }
}
