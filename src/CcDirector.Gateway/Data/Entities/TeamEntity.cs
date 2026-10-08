namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One team (Teams, first version - devthrottle_internal#2300): several people developing together. A team
/// IS a tenant: <see cref="Id"/> is the tenant id every tenant-scoped row the team owns carries in its
/// <c>tenant_id</c> column, exactly as a personal tenant's id is. The team does not live in the
/// <c>tenants</c> table, on purpose - that table is the one-account-to-one-personal-tenant mapping keyed by
/// a unique account subject, and a team has no single account behind it. Keeping teams out of it means a
/// person who never creates a team sees no change at all: their personal tenant, its id and every read of
/// that table are untouched.
///
/// GLOBAL, like <see cref="TenantEntity"/>: no <c>tenant_id</c> column and no query filter, because this is
/// the table that answers which tenants a person may act in - it is read before any tenant is chosen.
/// </summary>
public sealed class TeamEntity
{
    /// <summary>The team's id, which is also its tenant id. A GUID string generated in code when the team is
    /// created, never a database default.</summary>
    public string Id { get; set; } = "";

    /// <summary>The team's display name, as the team switcher and the members page show it.</summary>
    public string Name { get; set; } = "";

    /// <summary>When the team was created (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When the Owner deleted the team (UTC), or null for a team that is live. A deleted team is GONE to every read -
    /// the context's query filter leaves it out of every query of this table - but NOTHING IS ERASED: its skills,
    /// workflows, requests, reports and Mentor rows stay in the database and the team can be restored by hand (clear this
    /// column and put the Owner's membership row back - <see cref="DeletedByAccountSubject"/> names them). Erasing a
    /// deleted team's data is a later, separate decision.
    /// </summary>
    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>The account subject of the Owner who deleted the team, kept so a hand restore can give the team its Owner
    /// back. Personally identifying: never shown and never logged. Null for a live team.</summary>
    public string? DeletedByAccountSubject { get; set; }
}
