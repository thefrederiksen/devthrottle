using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>The outcome of trying to move a request into the accepted state.</summary>
public enum RestartAcceptOutcome
{
    /// <summary>It was pending and unexpired, and it is now accepted. The caller owns dispatching the cycle.</summary>
    Accepted,

    /// <summary>No request with that id exists in this tenant.</summary>
    NotFound,

    /// <summary>It was pending once and the expiry passed before anybody accepted. Ask again.</summary>
    Expired,

    /// <summary>It is not pending: already accepted, declined, abandoned or completed. The record says which.</summary>
    NotPending,
}

/// <summary>
/// The pending restart requests, per tenant. Issue #2725 (restart epic, Phase 6).
///
/// IN MEMORY, AND THAT IS A DECISION RATHER THAN A SHORTCUT. A request lives thirty minutes. A Gateway
/// restart or a hosted slot swap in that window loses it, and the safe reading of a lost request is
/// exactly what happens: nothing - no approval exists, nothing was told to drain, and the session asks
/// again. Persisting it would buy a request that outlives the Gateway that scrutinised it, which is the
/// approval-that-outlives-its-request hazard the expiry exists to close. It also keeps this change off
/// the migration path, which Phase 3 already holds.
///
/// ONE PENDING REQUEST PER MACHINE. Two approvals racing one drain is the same hazard as two drains, so
/// a second request while one is pending is refused with the first one's id. Closed requests are kept for
/// a day so a screen can show the outcome, then swept.
///
/// EXPIRY IS APPLIED ON EVERY READ, not by a timer. A pending request read after its expiry is already
/// expired - the read stamps it so, before anything else looks at it - so there is no window in which a
/// stale approval is acceptable because a sweep has not run yet.
/// </summary>
public sealed class DirectorRestartRequestStore
{
    /// <summary>How long an unanswered request stays acceptable.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(30);

    /// <summary>How long a closed request is kept for reading.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an ACCEPTED request may run without a final report before it is closed as expired. The
    /// drain's own handover deadline is ninety minutes; a cycle that has not reported its end in three
    /// hours has lost the Director that owed the report, and a record that says "running" for ever is a
    /// lie the owner acts on.
    /// </summary>
    public static readonly TimeSpan RunningExpiry = TimeSpan.FromHours(3);

    private readonly object _gate = new();
    private readonly Dictionary<TenantId, Dictionary<string, DirectorRestartRequestDto>> _byTenant = new();

    /// <summary>The clock, replaceable so expiry is testable without waiting thirty minutes.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Add a request, unless one is already pending for that machine in that tenant.
    /// </summary>
    /// <param name="tenant">The calling tenant.</param>
    /// <param name="request">The record to add. Its <see cref="DirectorRestartRequestDto.Id"/>,
    /// timestamps and state are set here.</param>
    /// <param name="alreadyPending">The request that is already pending for that machine, when there is one.</param>
    /// <returns>True when added.</returns>
    public bool TryCreate(TenantId tenant, DirectorRestartRequestDto request, out DirectorRestartRequestDto? alreadyPending)
    {
        ArgumentNullException.ThrowIfNull(request);
        alreadyPending = null;
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            ApplyExpiryAndSweep(requests, now);

            // ONE OPEN REQUEST PER MACHINE, where open means pending OR accepted-and-running. A second
            // request while a cycle is running would be a second approval racing the drain - the hazard
            // the pending rule exists for, one state later.
            alreadyPending = requests.Values.FirstOrDefault(r =>
                (r.State == DirectorRestartRequestState.Pending || r.State == DirectorRestartRequestState.Accepted)
                && string.Equals(r.Machine, request.Machine, StringComparison.OrdinalIgnoreCase));
            if (alreadyPending is not null)
            {
                FileLog.Write($"[DirectorRestartRequestStore] TryCreate REFUSED tenant={tenant.Value} machine={request.Machine}: "
                              + $"request {alreadyPending.Id} is already pending");
                return false;
            }

            request.Id = Guid.NewGuid().ToString("N");
            request.RequestedAtUtc = now;
            request.ExpiresAtUtc = now + Expiry;
            request.State = DirectorRestartRequestState.Pending;
            request.StateReason = "";
            request.AcceptedAtUtc = null;
            request.ClosedAtUtc = null;
            Stamp(request, now);
            requests[request.Id] = request;
            FileLog.Write($"[DirectorRestartRequestStore] TryCreate tenant={tenant.Value} machine={request.Machine} "
                          + $"director={request.DirectorId} id={request.Id} expires={request.ExpiresAtUtc:o}");
            return true;
        }
    }

    /// <summary>One request, with its expiry applied, or null.</summary>
    public DirectorRestartRequestDto? Get(TenantId tenant, string id)
    {
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            ApplyExpiryAndSweep(requests, now);
            if (!requests.TryGetValue(id ?? "", out var found)) return null;
            Stamp(found, now);
            return Copy(found);
        }
    }

    /// <summary>Every request in the tenant, newest first, optionally only one machine's.</summary>
    public List<DirectorRestartRequestDto> List(TenantId tenant, string? machine = null)
    {
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            ApplyExpiryAndSweep(requests, now);
            return requests.Values
                .Where(r => machine is null || string.Equals(r.Machine, machine, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.RequestedAtUtc)
                .Select(r => { Stamp(r, now); return Copy(r); })
                .ToList();
        }
    }

    /// <summary>
    /// Move a request to accepted. Atomic: two accepts racing for one request see one
    /// <see cref="RestartAcceptOutcome.Accepted"/> and one <see cref="RestartAcceptOutcome.NotPending"/>.
    /// </summary>
    public RestartAcceptOutcome Accept(TenantId tenant, string id, out DirectorRestartRequestDto? request)
    {
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            ApplyExpiryAndSweep(requests, now);
            if (!requests.TryGetValue(id ?? "", out var found))
            {
                request = null;
                return RestartAcceptOutcome.NotFound;
            }

            // == AGAINST THE ONE STATE AN ACCEPT IS VALID FROM. A check written as "not declined and not
            // expired" would accept from any state added later, including one that means the cycle is
            // already running.
            if (found.State == DirectorRestartRequestState.Expired)
            {
                request = Copy(found);
                return RestartAcceptOutcome.Expired;
            }
            if (found.State != DirectorRestartRequestState.Pending)
            {
                request = Copy(found);
                return RestartAcceptOutcome.NotPending;
            }

            found.State = DirectorRestartRequestState.Accepted;
            found.AcceptedAtUtc = now;
            found.Progress = "accepted; telling the Director to begin";
            found.ProgressAtUtc = now;
            Stamp(found, now);
            FileLog.Write($"[DirectorRestartRequestStore] Accept tenant={tenant.Value} id={id} machine={found.Machine}");
            request = Copy(found);
            return RestartAcceptOutcome.Accepted;
        }
    }

    /// <summary>Close a pending request as declined. False when it was not pending.</summary>
    public bool Decline(TenantId tenant, string id, string reason, out DirectorRestartRequestDto? request)
    {
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            ApplyExpiryAndSweep(requests, now);
            if (!requests.TryGetValue(id ?? "", out var found) || found.State != DirectorRestartRequestState.Pending)
            {
                request = found is null ? null : Copy(found);
                return false;
            }
            Close(found, DirectorRestartRequestState.Declined, reason, now);
            FileLog.Write($"[DirectorRestartRequestStore] Decline tenant={tenant.Value} id={id}: {reason}");
            request = Copy(found);
            return true;
        }
    }

    /// <summary>
    /// Record what the Director reported. Progress keeps the request accepted; abandoned and completed
    /// close it. Any other state is refused here rather than written, because those are not the Director's
    /// to set. False when the request is not in the accepted state.
    /// </summary>
    public bool Report(TenantId tenant, string id, DirectorRestartProgressReport report, out DirectorRestartRequestDto? request)
    {
        ArgumentNullException.ThrowIfNull(report);
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            ApplyExpiryAndSweep(requests, now);
            if (!requests.TryGetValue(id ?? "", out var found) || found.State != DirectorRestartRequestState.Accepted)
            {
                request = found is null ? null : Copy(found);
                return false;
            }

            switch (report.State)
            {
                case DirectorRestartRequestState.Accepted:
                    found.Progress = report.Progress;
                    found.ProgressAtUtc = now;
                    break;
                case DirectorRestartRequestState.Abandoned:
                case DirectorRestartRequestState.Completed:
                    found.Progress = report.Progress;
                    found.ProgressAtUtc = now;
                    Close(found, report.State, report.Progress, now);
                    break;
                default:
                    throw new ArgumentException(
                        $"a Director may report Accepted, Abandoned or Completed, not {report.State}", nameof(report));
            }
            if (!string.IsNullOrWhiteSpace(report.WorkspaceId)) found.WorkspaceId = report.WorkspaceId;
            Stamp(found, now);
            FileLog.Write($"[DirectorRestartRequestStore] Report tenant={tenant.Value} id={id} state={report.State}: {report.Progress}");
            request = Copy(found);
            return true;
        }
    }

    /// <summary>Close an accepted request as abandoned from the Gateway's side - the dispatch to the
    /// Director failed, so nothing is running. False when it was not accepted.</summary>
    public bool Abandon(TenantId tenant, string id, string reason, out DirectorRestartRequestDto? request)
    {
        var now = Clock();
        lock (_gate)
        {
            var requests = For(tenant);
            if (!requests.TryGetValue(id ?? "", out var found) || found.State != DirectorRestartRequestState.Accepted)
            {
                request = found is null ? null : Copy(found);
                return false;
            }
            Close(found, DirectorRestartRequestState.Abandoned, reason, now);
            found.Progress = reason;
            found.ProgressAtUtc = now;
            FileLog.Write($"[DirectorRestartRequestStore] Abandon tenant={tenant.Value} id={id}: {reason}");
            request = Copy(found);
            return true;
        }
    }

    private Dictionary<string, DirectorRestartRequestDto> For(TenantId tenant)
    {
        if (!_byTenant.TryGetValue(tenant, out var requests))
        {
            requests = new Dictionary<string, DirectorRestartRequestDto>(StringComparer.Ordinal);
            _byTenant[tenant] = requests;
        }
        return requests;
    }

    /// <summary>Expire what is past its window, and forget what closed more than a day ago.</summary>
    private static void ApplyExpiryAndSweep(Dictionary<string, DirectorRestartRequestDto> requests, DateTime now)
    {
        foreach (var r in requests.Values)
        {
            // >= rather than >: at the exact instant of expiry the approval is no longer acceptable.
            if (r.State == DirectorRestartRequestState.Pending && now >= r.ExpiresAtUtc)
                Close(r, DirectorRestartRequestState.Expired,
                    $"nobody accepted within {Expiry.TotalMinutes:0} minutes of the request, so the approval "
                    + "that was being waited for can no longer arrive. Ask again if the restart is still wanted.",
                    now);
            else if (r.State == DirectorRestartRequestState.Accepted && r.AcceptedAtUtc is { } accepted
                     && now - accepted >= RunningExpiry)
                Close(r, DirectorRestartRequestState.Expired,
                    $"the Director reported nothing final within {RunningExpiry.TotalHours:0} hours of the accept. "
                    + "Its last word was: " + (string.IsNullOrWhiteSpace(r.Progress) ? "(none)" : r.Progress)
                    + ". Read the Director and launcher logs on that machine; do not assume the restart happened.",
                    now);
        }

        var stale = requests.Values
            .Where(r => r.ClosedAtUtc is { } closed && now - closed > Retention)
            .Select(r => r.Id)
            .ToList();
        foreach (var id in stale) requests.Remove(id);
    }

    private static void Close(DirectorRestartRequestDto r, DirectorRestartRequestState state, string reason, DateTime now)
    {
        r.State = state;
        r.StateReason = reason;
        r.ClosedAtUtc = now;
        Stamp(r, now);
    }

    /// <summary>The one place <see cref="DirectorRestartRequestDto.CanAccept"/> is computed.</summary>
    private static void Stamp(DirectorRestartRequestDto r, DateTime now)
        => r.CanAccept = r.State == DirectorRestartRequestState.Pending && now < r.ExpiresAtUtc;

    /// <summary>A copy, so a caller cannot edit the stored record around the lock.</summary>
    private static DirectorRestartRequestDto Copy(DirectorRestartRequestDto r) => new()
    {
        Id = r.Id,
        Machine = r.Machine,
        DirectorId = r.DirectorId,
        DirectorName = r.DirectorName,
        RequestedBySessionId = r.RequestedBySessionId,
        RequestedBySessionName = r.RequestedBySessionName,
        Reason = r.Reason,
        RequestedAtUtc = r.RequestedAtUtc,
        ExpiresAtUtc = r.ExpiresAtUtc,
        AcceptedAtUtc = r.AcceptedAtUtc,
        ClosedAtUtc = r.ClosedAtUtc,
        State = r.State,
        StateReason = r.StateReason,
        Progress = r.Progress,
        ProgressAtUtc = r.ProgressAtUtc,
        WorkspaceId = r.WorkspaceId,
        LiveSessionCount = r.LiveSessionCount,
        LiveSessionsSentence = r.LiveSessionsSentence,
        Capability = r.Capability,
        Title = r.Title,
        AskedBySentence = r.AskedBySentence,
        AcceptSentence = r.AcceptSentence,
        CanAccept = r.CanAccept,
    };
}
