namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The account-to-tenant mapping (Hosted Multi-Tenancy mission, increment 1): one row per tenant in the
/// <c>tenants</c> table. This is the GLOBAL mapping table - the one place that answers "which tenant does
/// this verified account belong to?" - so it deliberately does NOT derive from
/// <see cref="TenantScopedEntity"/>: it carries no <c>tenant_id</c> column and no global query filter,
/// because it is the table the filter's tenant values COME FROM. It is read by the account subject, before
/// any tenant is resolved, so scoping it to a tenant would be circular.
///
/// The mapping key is the STABLE Supabase subject (<see cref="AccountSubject"/>, the token's <c>sub</c>
/// claim), never the email - an email can change over an account's life, so it is stored only as display
/// metadata (<see cref="Email"/>) and is NEVER used to look a tenant up. The relationship is 1:1 now (one
/// account subject -> one tenant), structured so many subjects can map to one tenant later (enterprise):
/// the mint path looks up by subject first and reuses an existing tenant, minting a new
/// <see cref="Id"/> only for a subject that has none - so a second device of the same account resolves to
/// the SAME tenant.
///
/// On the single-tenant local install this table is unused: everything resolves to
/// <see cref="Core.Tenancy.TenantId.Local"/> ("local") without a lookup.
/// </summary>
public sealed class TenantEntity
{
    /// <summary>The tenant's own id - the value carried in every tenant-scoped row's <c>tenant_id</c> column.
    /// A GUID string generated in code on mint (never a database default). Primary key (ordinally compared).</summary>
    public string Id { get; set; } = "";

    /// <summary>The verified Supabase subject (<c>sub</c>) this tenant maps from - the STABLE mapping key.
    /// Unique across the table. Never logged (personally identifying).</summary>
    public string AccountSubject { get; set; } = "";

    /// <summary>The sign-in address: the token's top-level <c>email</c> claim, overwritten at every sign-in that
    /// carries one, so it follows the provider (devthrottle_internal#2816). Never the mapping key (an email can
    /// change). Kept, not cleared, when a token carries no claim; null when none was ever supplied. Never logged.</summary>
    public string? Email { get; set; }

    /// <summary>When this tenant was minted (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }
}
