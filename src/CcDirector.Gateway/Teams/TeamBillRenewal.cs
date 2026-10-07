using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The renewal pass for team bills (Teams v1, the team bill without Stripe), on a timer like
/// <see cref="TeamSeatConvergence"/>. At the end of a bill's period an auto-renewing bill rolls to the next month and
/// writes its history line ("Charged: US$0.00"), and a bill whose auto-renew is off - switched off, or cancelled - ends.
/// The work is <see cref="TeamBillStore.RenewDue"/>; this class runs it, never overlapping itself, and logs the result.
/// </summary>
public sealed class TeamBillRenewal
{
    /// <summary>How often the pass runs on a hosted Gateway with Teams released. A period ends at most this long before
    /// the bill shows it.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly TeamBillStore _bills;
    private int _running;

    public TeamBillRenewal(TeamBillStore bills)
    {
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
    }

    /// <summary>One pass. Returns what it did; a pass that overlaps a running one does nothing and returns null.</summary>
    public TeamBillRenewalSummary? RunOnce()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            FileLog.Write("[TeamBillRenewal] RunOnce: the previous pass is still running - skipped");
            return null;
        }

        try
        {
            var summary = _bills.RenewDue();
            FileLog.Write($"[TeamBillRenewal] RunOnce: {summary.Due} bill(s) at the end of their period, {summary.Renewed} renewed ({summary.HistoryLines} history line(s), charged 0), {summary.Ended} ended, {summary.ChangedElsewhere} changed first by another writer and left alone");
            return summary;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>The timer's entry point: a failure is logged and left to the next pass, never thrown into the timer thread.</summary>
    public void RunSafe()
    {
        try
        {
            RunOnce();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamBillRenewal] RunSafe FAILED ({ex.GetType().Name}): {ex.Message} - the next pass retries");
        }
    }
}
