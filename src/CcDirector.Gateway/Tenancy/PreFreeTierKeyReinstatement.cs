using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Gives back the device keys the Gateway cancelled under the rule that a trial ending cut an account off.
///
/// WHY THIS EXISTS. Until the free tier (#2664, merged 2026-09-03 03:54 UTC) an account whose trial ended with
/// no paid plan read NotEntitled, and <see cref="HostedAccessLeaseService"/> tombstoned every device key it held
/// (<see cref="TenantAccessRevokeReasons.EntitlementLost"/>). The owner withdrew that rule: the end of a trial is
/// a downgrade to free, which keeps the hosted gateway. But a tombstone is durable, and the Director does not
/// re-enrol on its own when its key is refused - so the accounts cut off before the change stayed cut off by a
/// rule that no longer exists. One of them used DevThrottle every day and was invisible to us for three weeks.
///
/// The owner's ruling, 2026-09-22: reinstate every key cancelled with that reason for an account that is now on
/// free or better. This runs once at hosted start-up and is idempotent.
///
/// WHAT KEEPS IT NARROW:
///   * only <see cref="TenantAccessRevokeReasons.EntitlementLost"/> - a key revoked for any other reason is
///     never touched;
///   * only revocations before <see cref="OldRuleEndedUtc"/> - anything the Gateway revokes from here on, for
///     whatever reason, stays revoked;
///   * only a tenant whose account the entitlement read says may hold hosted capacity TODAY - asked through the
///     same <see cref="EntitlementRegistry"/> and <see cref="EntitlementScopes"/> the request path uses. A FAILED
///     read is not a yes: that tenant is left alone and considered again at the next start.
/// </summary>
public static class PreFreeTierKeyReinstatement
{
    /// <summary>
    /// The end of the old rule. #2664 merged at 03:54 UTC on 3 September and the last revocation under the old
    /// rule was at 00:48 UTC that day; midnight on the 4th covers the deploy that followed the merge.
    /// </summary>
    public static readonly DateTime OldRuleEndedUtc = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);

    public static int Run(
        Pairing.DeviceRegistry devices,
        TenantRegistry tenants,
        EntitlementRegistry entitlements,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(entitlements);

        var reinstated = devices.ReinstateRevokedBefore(
            TenantAccessRevokeReasons.EntitlementLost,
            OldRuleEndedUtc,
            tenant =>
            {
                var subject = tenants.SubjectForTenant(tenant);
                if (string.IsNullOrWhiteSpace(subject))
                    return false;
                var decision = entitlements.Evaluate(subject, nowUtc);
                return decision.Outcome == EntitlementOutcome.Entitled
                       && EntitlementScopes.GrantsHostedGateway(decision.Tier);
            });

        FileLog.Write($"[PreFreeTierKeyReinstatement] reinstated {reinstated} device credential(s) cancelled before the free tier");
        return reinstated;
    }
}
