using System.Net.Http;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>What the seat-convergence check concluded for one team.</summary>
public enum SeatSyncVerdict
{
    /// <summary>The Gateway's paid-member count equals the seats the bill carries. Nothing to do.</summary>
    InSync,

    /// <summary>The two counts differ (or the bill recorded no count). Ask the website to recount.</summary>
    CallSync,

    /// <summary>The team has no bill to correct - no row yet (checkout not finished) or a canceled one.</summary>
    NoBill,

    /// <summary>The team row could not be read. Nothing is decided; the next pass retries.</summary>
    Unknown,
}

/// <summary>
/// Keeps a team's bill in step with its membership (devthrottle_internal #2299, seam section 4).
///
/// Two entry points, and only two:
///  - <see cref="SyncAfterMembershipChangeAsync"/> - called by the membership code (#2300) AFTER it commits an
///    accepted invitation, a removed member or a changed role. It names the team; the website recounts.
///  - <see cref="ConvergeAsync"/> - the retry net: given the Gateway's own paid-member count it compares with the
///    seats on the team's bill and calls sync again when they differ, so a failed call is retried, not lost.
///
/// Neither ever sends a number: the website reads the count from the Gateway's membership table itself.
/// </summary>
public sealed class TeamSeatSync
{
    private readonly EntitlementRegistry _entitlements;
    private readonly TeamSeatSyncClient _client;
    private readonly Func<string?> _serviceToken;

    /// <param name="entitlements">The one entitlement reader, used here only for the team row's seat count.</param>
    /// <param name="client">The website seat-sync client.</param>
    /// <param name="serviceToken">Resolves the Gateway service secret. Defaults to the same setting the
    /// owner-email client reads; injected so a test needs no process environment.</param>
    public TeamSeatSync(EntitlementRegistry entitlements, TeamSeatSyncClient client, Func<string?>? serviceToken = null)
    {
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _serviceToken = serviceToken ?? AccountNotifyByTenantClient.ResolveServiceToken;
    }

    /// <summary>
    /// The pure comparison: the Gateway's paid-member count against the seats the bill carries. Equal is
    /// <see cref="SeatSyncVerdict.InSync"/>; different - or a bill that recorded no count - is
    /// <see cref="SeatSyncVerdict.CallSync"/>.
    /// </summary>
    public static SeatSyncVerdict Compare(int gatewayPaidMembers, int? billedSeats)
    {
        if (gatewayPaidMembers < 0)
            throw new ArgumentOutOfRangeException(nameof(gatewayPaidMembers), "A member count cannot be negative");
        return billedSeats == gatewayPaidMembers ? SeatSyncVerdict.InSync : SeatSyncVerdict.CallSync;
    }

    /// <summary>
    /// THE METHOD THE MEMBERSHIP CODE CALLS after it commits a change that can move the paid-seat count:
    /// accepting an invitation (not sending one), removing a member, or changing a role. Call it after the
    /// commit, never inside the transaction - the website reads the committed membership.
    ///
    /// A REFUSED or UNREACHABLE website is returned, not thrown: the membership change has already committed, and
    /// failing the member's request now would report a change that happened as one that did not. Such a failure
    /// is logged loud and returned; <see cref="ConvergeAsync"/> retries it.
    ///
    /// WHAT CAN STILL THROW, exactly: an <see cref="ArgumentException"/> for a blank team id, and an
    /// <see cref="OperationCanceledException"/> when the caller's OWN <paramref name="ct"/> is cancelled. The call
    /// can take up to the client's 30-second timeout when the website is slow. So the caller must NOT pass the
    /// member's request token (a browser that disconnects would cancel the sync after the commit) and should not
    /// hold the member's response on it: pass <see cref="CancellationToken.None"/> or a host-lifetime token, and
    /// either await it after the response is decided or start it without awaiting - a lost call is what
    /// convergence exists to repair.
    /// </summary>
    /// <param name="teamId">The team id, which is the tenant id.</param>
    /// <param name="ct">A host-lifetime token, never the member's request token.</param>
    public async Task<TeamSeatSyncResult> SyncAfterMembershipChangeAsync(string teamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));

        FileLog.Write($"[TeamSeatSync] SyncAfterMembershipChangeAsync: team={TeamLog(teamId)} a membership change committed, asking the website to recount the team's seats");
        return await CallSyncAsync(teamId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The convergence check for one team: compare <paramref name="gatewayPaidMembers"/> (the Gateway's own count
    /// of the team's Owner, Manager and Developer members - see <see cref="TeamSeatRoles"/>) with the seats on the
    /// team's bill, and call sync when they differ. Returns the verdict and, when a call was made, its result.
    /// </summary>
    /// <param name="teamId">The team id, which is the tenant id.</param>
    /// <param name="gatewayPaidMembers">The Gateway's paid-member count for this team.</param>
    public async Task<(SeatSyncVerdict Verdict, TeamSeatSyncResult? Call)> ConvergeAsync(
        string teamId, int gatewayPaidMembers, CancellationToken ct = default)
    {
        var verdict = Decide(_entitlements.ReadTeamBilledSeats(teamId), gatewayPaidMembers);
        FileLog.Write($"[TeamSeatSync] ConvergeAsync: team={TeamLog(teamId)} verdict={verdict}");
        if (verdict != SeatSyncVerdict.CallSync)
            return (verdict, null);

        var result = await CallSyncAsync(teamId, ct).ConfigureAwait(false);
        return (verdict, result);
    }

    /// <summary>
    /// The convergence decision from what the team row says. A failed read decides nothing (Unknown); a team with
    /// no row, or a canceled bill, has no seat quantity to correct (NoBill); otherwise the counts are compared.
    /// </summary>
    public static SeatSyncVerdict Decide(TeamBilledSeats bill, int gatewayPaidMembers)
    {
        if (!bill.Known) return SeatSyncVerdict.Unknown;
        if (!bill.HasBill) return SeatSyncVerdict.NoBill;
        if (string.Equals(bill.Status, EntitlementRegistry.StatusCanceled, StringComparison.OrdinalIgnoreCase)) return SeatSyncVerdict.NoBill;
        return Compare(gatewayPaidMembers, bill.Seats);
    }

    private async Task<TeamSeatSyncResult> CallSyncAsync(string teamId, CancellationToken ct)
    {
        var token = _serviceToken();
        if (token is null)
        {
            // Fail closed and say so: calling unauthenticated would make every 401 ambiguous.
            FileLog.Write($"[TeamSeatSync] CallSyncAsync: team={TeamLog(teamId)} {AccountNotifyByTenantClient.ServiceTokenEnvVar} is not set on this Gateway - the seat sync was NOT called");
            return new TeamSeatSyncResult(false, 0,
                $"The Gateway service credential ({AccountNotifyByTenantClient.ServiceTokenEnvVar}) is not set, so the team's seats were not synced.",
                null);
        }

        try
        {
            var result = await _client.SyncSeatsAsync(token, teamId, ct).ConfigureAwait(false);
            FileLog.Write($"[TeamSeatSync] CallSyncAsync: team={TeamLog(teamId)} synced={result.Synced} status={result.StatusCode} code={result.ErrorCode ?? "<none>"}");
            return result;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            // The website could not be reached. The membership change has committed; the convergence pass
            // retries this team. Logged loud so a persistent outage is visible.
            FileLog.Write($"[TeamSeatSync] CallSyncAsync: team={TeamLog(teamId)} the website could not be reached ({ex.GetType().Name}) - the seat sync will be retried by convergence");
            return new TeamSeatSyncResult(false, 0, "The DevThrottle website could not be reached to sync the team's seats.", null);
        }
    }

    // The team id IS a tenant id, so it is logged in the same hashed form as every tenant in the Gateway: a failed
    // sync can be tied to its team without the raw id reaching the log.
    private static string TeamLog(string teamId) => new Core.Tenancy.TenantId(teamId.Trim()).ToLogString();
}
