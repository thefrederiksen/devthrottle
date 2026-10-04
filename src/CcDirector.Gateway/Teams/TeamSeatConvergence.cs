using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The retry net for the seat sync (devthrottle_internal#2301, seam-team-billing.md section 4). The sync after a
/// membership change can fail - the website down, a timeout, a missing credential - and the change has already
/// committed, so nothing retries it in the moment. This pass does: for every team it counts the Gateway's own paid
/// members (Owner, Manager, Developer - <see cref="TeamSeatRoles"/>) and hands the count to
/// <see cref="TeamSeatSync.ConvergeAsync"/>, which compares it with the seats on the team's bill and calls the sync
/// again only where the two differ. A team with no bill (checkout not finished) or a cancelled one is skipped.
///
/// IT IS NOT A LOOP. One pass calls the sync at most once per team. The website answers by setting the bill's quantity
/// to the count it reads itself and heals team_entitlements.seats when only the row was behind, so on the next pass the
/// two counts match and nothing is called. A team that still differs on the next pass is called once more on that
/// pass - never again inside one.
/// </summary>
public sealed class TeamSeatConvergence
{
    /// <summary>How often the pass runs on a hosted Gateway with Teams released.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly GatewayDatabase _db;
    private readonly TeamSeatSync _seatSync;
    private int _running;

    public TeamSeatConvergence(GatewayDatabase db, TeamSeatSync seatSync)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _seatSync = seatSync ?? throw new ArgumentNullException(nameof(seatSync));
    }

    /// <summary>The paid-member count of every team, by team id. A team always has its Owner, so every team is here.</summary>
    public IReadOnlyDictionary<string, int> PaidMemberCounts()
    {
        using var ctx = _db.CreateUnscopedContext();
        var rows = ctx.TeamMembers.AsNoTracking().Select(m => new { m.TeamId, m.Role }).ToList();
        var teamIds = ctx.Teams.AsNoTracking().Select(t => t.Id).ToList();
        var counts = teamIds.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (TeamSeatRoles.IsPaidSeat(TeamRoles.ToStored(row.Role)) && counts.ContainsKey(row.TeamId))
                counts[row.TeamId]++;
        }
        return counts;
    }

    /// <summary>
    /// One pass over every team. Returns how many teams the sync was called for. A pass that is still running when the
    /// next is due is not overlapped: the second returns at once having called nothing.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            FileLog.Write("[TeamSeatConvergence] RunOnceAsync: the previous pass is still running - skipped");
            return 0;
        }

        try
        {
            var counts = PaidMemberCounts();
            var called = 0;
            var failed = 0;
            foreach (var (teamId, paid) in counts)
            {
                ct.ThrowIfCancellationRequested();
                var (verdict, call) = await _seatSync.ConvergeAsync(teamId, paid, ct).ConfigureAwait(false);
                if (verdict == SeatSyncVerdict.Unknown)
                    FileLog.Write("[TeamSeatConvergence] RunOnceAsync: a team's bill could not be read - left for the next pass");
                if (call is null) continue;
                called++;
                if (!call.Synced) failed++;
            }
            FileLog.Write($"[TeamSeatConvergence] RunOnceAsync: {counts.Count} team(s) checked, sync called for {called}, {failed} of those not done (retried next pass)");
            return called;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>The timer's entry point: a pass whose failure is logged and left to the next pass, never thrown into
    /// the timer thread.</summary>
    public async Task RunSafeAsync()
    {
        try
        {
            await RunOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamSeatConvergence] RunSafeAsync FAILED ({ex.GetType().Name}): {ex.Message} - the next pass retries");
        }
    }
}
