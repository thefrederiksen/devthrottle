using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The safety net for a team bill's seat count (Teams v1, the team bill without Stripe). A membership change records the
/// team's paid-seat count on its bill right after the change commits (<see cref="TeamRegistry"/>); a Gateway that stops
/// between the two leaves the bill one change behind. This pass puts it right: for every team it asks
/// <see cref="TeamBillStore.RecordSeats"/> to record the Gateway's own paid-member count (Owner, Manager, Developer -
/// <see cref="Tenancy.TeamSeatRoles"/>) on the team's active bill where the two differ.
///
/// NOTHING LEAVES THE GATEWAY. The seat count used to be sent to the website so it could tell the payment provider; the
/// owner's ruling of 7 Oct 2026 removed that path. The count is the Gateway's own, recorded on the Gateway's own bill.
/// </summary>
public sealed class TeamSeatConvergence
{
    /// <summary>How often the pass runs on a hosted Gateway with Teams released.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly GatewayDatabase _db;
    private readonly TeamBillStore _bills;
    private int _running;

    public TeamSeatConvergence(GatewayDatabase db, TeamBillStore bills)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
    }

    /// <summary>
    /// One pass over every team. Returns how many bills had their seat count corrected. A pass that is still running when
    /// the next is due is not overlapped: the second returns at once having changed nothing.
    /// </summary>
    public int RunOnce()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            FileLog.Write("[TeamSeatConvergence] RunOnce: the previous pass is still running - skipped");
            return 0;
        }

        try
        {
            List<string> teamIds;
            using (var ctx = _db.CreateUnscopedContext())
                teamIds = ctx.Teams.AsNoTracking().Select(t => t.Id).ToList();

            var recorded = 0;
            var inStep = 0;
            var noBill = 0;
            var changedElsewhere = 0;
            foreach (var teamId in teamIds)
            {
                switch (_bills.RecordSeats(teamId))
                {
                    case SeatRecordOutcome.Recorded: recorded++; break;
                    case SeatRecordOutcome.InStep: inStep++; break;
                    case SeatRecordOutcome.NoBill: noBill++; break;
                    case SeatRecordOutcome.ChangedElsewhere: changedElsewhere++; break;
                    default: throw new InvalidOperationException("A seat record outcome this pass does not know.");
                }
            }
            FileLog.Write($"[TeamSeatConvergence] RunOnce: {teamIds.Count} team(s) checked, {recorded} seat count(s) corrected, {inStep} already in step, {noBill} with no active bill, {changedElsewhere} changed first by another writer (recorded next pass)");
            return recorded;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>The timer's entry point: a pass whose failure is logged and left to the next pass, never thrown into
    /// the timer thread.</summary>
    public void RunSafe()
    {
        try
        {
            RunOnce();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamSeatConvergence] RunSafe FAILED ({ex.GetType().Name}): {ex.Message} - the next pass retries");
        }
    }
}
