using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// Where held dev report items are drained from (issue #2958, PLAN-phase-2.md rule 5): the Gateway's turn-end
/// boundary, the same one Session Rules hang off. It is a type of its own, as <c>RuleTurnEndLauncher</c> is, so
/// the host's turn-end handler holds one line and the feature holds its own isolation.
///
/// FIRE AND FORGET, AND IT NEVER THROWS. The turn-end handler must not wait on a tunnel send, and a delivery
/// fault must not break the voice refresh, the supervisor or the rules beside it.
///
/// THE RESTART CASE RIDES THE SAME CALL. After a Gateway restart the turn-end watcher sees each session for the
/// first time; one already waiting raises a catch-up turn end (<c>IsNewTurn=false</c>), and this drains whatever
/// the database still holds for it. There is no second startup sweep to keep in step.
/// </summary>
internal sealed class DevReportTurnEndLauncher
{
    private readonly DevReportDelivery _delivery;

    /// <exception cref="ArgumentNullException">The delivery service is null.</exception>
    public DevReportTurnEndLauncher(DevReportDelivery delivery)
    {
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
    }

    /// <summary>A session has just crossed into idle (or was first seen idle): settle what is held for it.</summary>
    public void OnTurnEnd(TenantId tenant, string sessionId, bool isNewTurn)
    {
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var delivered = await _delivery.SettleAsync(tenant, sessionId, CancellationToken.None).ConfigureAwait(false);
                    if (delivered > 0)
                        FileLog.Write($"[DevReportTurnEndLauncher] sid={sessionId} newTurn={isNewTurn} delivered {delivered} item(s)");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[DevReportTurnEndLauncher] sid={sessionId} settle FAILED: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportTurnEndLauncher] sid={sessionId} could not start: {ex.Message}");
        }
    }
}
