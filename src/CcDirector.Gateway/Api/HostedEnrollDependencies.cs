using CcDirector.Core.Account;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The five dependencies the ONE hosted mint - <see cref="HostedEnrollmentEndpoint.Enroll"/> - needs, bundled
/// so every hosted entry point that mints a tenant-scoped device key passes the identical set. Presence of this
/// object (non-null) is exactly what puts an entry point into hosted mode; a null bundle is the self-host case,
/// where the entry point's existing single-owner behaviour is byte-unchanged.
///
/// This bundle carries NO logic of its own. It deliberately does not wrap or re-implement any step of the mint:
/// the hosted entry points call <see cref="HostedEnrollmentEndpoint.Enroll"/> directly with these fields, so
/// there is one and only one place that validates an account token, gates on entitlement, mints a tenant, and
/// binds a device key. That single-path-by-construction is the whole security argument - tenant-scoping and
/// never-mint-on-a-bad-token are inherited from the one proven function, not re-derived per surface.
/// </summary>
/// <param name="Devices">The Gateway's local device registry that issues and binds the per-device key.</param>
/// <param name="Tenants">The tenant registry that mint-or-looks-up the account's tenant.</param>
/// <param name="AccountTokenValidator">The account access-token validator (signature, expiry, audience, issuer).</param>
/// <param name="Entitlements">The paid-entitlement gate that sits between subject verification and tenant mint.</param>
/// <param name="RemoteAccess">The remote access rule (Pricing Change mission, B2): whether an account may be handed a
/// Cockpit's or a phone's key. It rides in this bundle for the same reason the entitlement gate does - an entry point
/// that mints such a key must ask the identical rule the request path asks, never its own - and it is REQUIRED, so a
/// hosted entry point cannot be built without it.</param>
internal sealed record HostedEnrollDependencies(
    DeviceRegistry Devices,
    TenantRegistry Tenants,
    JwtAccessTokenValidator AccountTokenValidator,
    EntitlementRegistry Entitlements,
    RemoteAccessRule RemoteAccess);
