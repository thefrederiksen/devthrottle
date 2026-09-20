using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workspaces;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// WHO MAY PASS A SESSION'S DEV REPORTS TO ANOTHER SESSION, AND THE ONLY ANSWER IS: THE RESTORE OF A RECORDED SEAT
/// (the Smart Director Restart mission, section 5.3 item 13).
///
/// A dev report belongs to the session that published it, keyed on that session and the file path. A restored
/// session is a NEW session, so without this it republishes the same file as a new report at a new link, and the
/// link the owner was reading freezes. The restore asks for the pass once per seat it brought back.
///
/// THE JOIN IS THE RECORD'S, NEVER THE CALLER'S. The request names a seat. The old session id is that seat's
/// captured session id, which a capture observed and no write can change (a captured workspace's seat set is
/// fixed). The new session id is the seat's restored session id, which only a restore mark carrying the start
/// token of the Director that started the seat can write, or the spawn door's own record of that same token. There
/// is no field in which anybody can name either session, so there is nothing to forge.
///
/// EVERY CONDITION IS A PRESENCE. The pass happens only when the workspace is captured, the asking Director holds a
/// live restore lease, the seat exists, and the seat names the session it came back as. Anything else refuses with
/// the reason. A seat that was not brought back has no restored session id, so its reports stay where they are,
/// frozen, as before.
/// </summary>
internal static class DevReportInheritance
{
    /// <summary>
    /// Pass the seat's reports to the session it was restored as.
    /// </summary>
    /// <exception cref="WorkspaceValidationException">The request is malformed, or names no seat of this workspace.</exception>
    /// <exception cref="WorkspaceConflictException">The workspace is not captured, the Director does not hold a live
    /// restore lease, or the seat has not come back.</exception>
    public static WorkspaceDevReportPassResult Pass(
        WorkspaceDocument doc, WorkspaceDevReportPassRequest request, DevReportStore reports, TenantId tenant, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reports);
        FileLog.Write($"[DevReportInheritance] Pass: workspace={doc.Id}, director={request.DirectorId}, seat={request.SeatSessionId}");

        if (string.IsNullOrWhiteSpace(request.DirectorId))
            throw new WorkspaceValidationException("directorId is required on a dev report pass.");
        if (string.IsNullOrWhiteSpace(request.SeatSessionId))
            throw new WorkspaceValidationException("seatSessionId is required on a dev report pass.");

        if (!string.Equals(doc.Origin, WorkspaceOrigins.Captured, StringComparison.Ordinal))
            throw new WorkspaceConflictException(
                $"workspace \"{doc.Id}\" is {doc.Origin}, not captured. Dev reports pass only along a seat the Gateway " +
                "captured from a running Director, because only there is the old session a fact the Gateway observed.");

        var at = nowUtc.ToUniversalTime();
        var lease = doc.RestoreLease;
        if (lease is null || !lease.IsLiveAt(at)
            || !string.Equals(lease.DirectorId, request.DirectorId, StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceConflictException(
                $"Director '{request.DirectorId}' does not hold the restore lease on workspace \"{doc.Id}\" " +
                (lease is null ? "(nobody does)" : $"('{lease.DirectorId}' {(lease.IsLiveAt(at) ? "does" : "did, and it has expired")})") +
                ", so it may not pass a seat's dev reports. Only a running restore does that.");

        var seat = doc.Seats.FirstOrDefault(s => string.Equals(s.SessionId, request.SeatSessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkspaceValidationException($"workspace \"{doc.Id}\" has no seat '{request.SeatSessionId}'.");
        if (string.IsNullOrWhiteSpace(seat.RestoredSessionId))
            throw new WorkspaceConflictException(
                $"seat '{seat.SessionId}' (\"{seat.Name}\") has not come back, so there is no session for its dev reports " +
                "to pass to. They stay with the session that published them.");

        // Reports are stored under the session id in its one written form, whatever form the seat carries it in.
        var from = DevReportDelivery.NormalizeSessionId(seat.SessionId!.Trim());
        var to = DevReportDelivery.NormalizeSessionId(seat.RestoredSessionId.Trim());
        if (string.Equals(from, to, StringComparison.Ordinal))
            throw new WorkspaceConflictException(
                $"seat '{seat.SessionId}' is recorded as restored as itself, so there is nothing to pass.");

        var (passed, kept) = reports.PassToSession(tenant, from, to);
        FileLog.Write($"[DevReportInheritance] Pass: workspace={doc.Id}, seat={seat.SessionId} -> {to}: passed={passed}, kept={kept.Count}");
        return new WorkspaceDevReportPassResult
        {
            FromSessionId = from,
            ToSessionId = to,
            Passed = passed,
            KeptBecauseTheNewSessionAlreadyHasTheKey = kept.ToList(),
        };
    }
}
