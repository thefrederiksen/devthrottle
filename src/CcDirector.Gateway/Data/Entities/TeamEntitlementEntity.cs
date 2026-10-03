namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One TEAM's bill, as the payment side last recorded it - the row that decides whether the paid members of a
/// team tenant hold paid features (devthrottle_internal #2299).
///
/// THIS TABLE IS NOT OURS TO CREATE OR WRITE, exactly like <see cref="EntitlementEntity"/>. The website creates
/// it with its own migration and writes it ONLY from its payment webhook (through
/// <c>public.record_team_subscription(...)</c>); this Gateway holds SELECT and nothing more. It is therefore
/// mapped EXCLUDED FROM MIGRATIONS: we describe its shape so we can read it, and we never emit anything that
/// would create, alter or seed it.
///
/// Keyed by the TEAM ID, which IS the tenant id (<c>gateway.tenants."Id"</c>, the string the Gateway mints) -
/// never by the Owner's subject, an email or a payment-provider id. A person's own Pro stays on
/// <see cref="EntitlementEntity"/>, keyed by their subject; a team seat and a personal seat never share a row,
/// which is what keeps "a seat on one team is not a personal seat" true by construction.
///
/// Nothing on this row is ever logged: the team id is account-identifying, and the subscription reference
/// belongs to the payment provider.
/// </summary>
public sealed class TeamEntitlementEntity
{
    /// <summary>The team id - the tenant id of the team tenant. Primary key. Never logged.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>
    /// The subscription state as the payment side computed it: active, past_due, or canceled. Read as an opaque
    /// string and compared exactly; a value the Gateway does not recognise is NOT entitled.
    /// </summary>
    public string Status { get; set; } = "";

    /// <summary>
    /// The number of seats the payment provider is billing now. Read ONLY by the seat-convergence check, which
    /// compares it with the Gateway's own paid-member count; it never decides access. Nullable so a row the
    /// payment side wrote without a count reads as "differs" (call sync) rather than failing the access read.
    /// </summary>
    public int? Seats { get; set; }

    /// <summary>The end of the current paid period (UTC). For a team this is NOT a cut-off - see
    /// <see cref="Tenancy.EntitlementRegistry.EvaluateTeam"/>.</summary>
    public DateTime? CurrentPeriodEnd { get; set; }

    /// <summary>The payment provider's subscription reference. Read-only here, and never logged.</summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>
    /// Whether this is a LIVE subscription rather than a payment-provider TEST-mode one. On the production
    /// hosted Gateway it must be explicitly true; false and null are both refused, for the same reason as on
    /// <see cref="EntitlementEntity.Livemode"/>.
    /// </summary>
    public bool? Livemode { get; set; }

    /// <summary>When the payment side last wrote this row (UTC).</summary>
    public DateTime? UpdatedAt { get; set; }
}
