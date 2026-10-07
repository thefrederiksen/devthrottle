using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// The tenancy CI guard (Hosted Multi-Tenancy increment 1) - the check PostHog learned the hard way. It
/// reflects the actual EF model and fails the build if ANY tenant-scoped table is missing its tenant key or
/// its isolation filter. A future store that forgets to derive from <see cref="TenantScopedEntity"/>, or a
/// change that drops the <c>tenant_id</c> column or the global query filter, turns red here instead of
/// silently letting one tenant read another's rows.
///
/// The rule is exact and two-directional:
///  - EVERY entity derived from <see cref="TenantScopedEntity"/> MUST map a <c>tenant_id</c> column AND carry
///    a global query filter (deny-by-default isolation).
///  - The GLOBAL mapping table <see cref="TenantEntity"/> MUST NOT be scoped: it is the table the filter's
///    tenant values come from, so it carries no <c>tenant_id</c> column and no query filter.
///
/// And a THIRD rule, the other half of tenant scoping:
///  - EVERY tenant-scoped table's PRIMARY KEY must INCLUDE the tenant, unless its key is a value the Gateway
///    itself mints and guarantees globally unique - which is not a list of table names but a TYPE,
///    <see cref="GatewayMintedKeyEntity"/>, whose key only that base class can write.
///
/// That third rule exists because the column-and-filter checks above are structurally BLIND to the key. A
/// table can carry a perfect <c>tenant_id</c> column and a perfect query filter and STILL have a primary key
/// built only from caller-supplied input - and three did (<c>snoozes</c>, <c>session_spend</c>,
/// <c>push_subscriptions</c>). Every store upserts by reading THROUGH the filter and adding when it finds
/// nothing, so on such a key a second tenant presenting an identifier the first tenant already holds finds
/// nothing, inserts, and hits a PRIMARY KEY violation: a cross-tenant SQUAT plus an EXISTENCE ORACLE. A guard
/// that checked only the column and the filter was exactly how those three survived, so the key check lives
/// here beside them rather than in a separate file that could be forgotten.
/// </summary>
public sealed class TenantScopeGuardTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private IModel Model()
    {
        using var ctx = _harness.Open().CreateContext();
        return ctx.Model;
    }

    [Fact]
    public void EveryTenantScopedEntity_HasTenantIdColumnAndQueryFilter()
    {
        var model = Model();
        var scoped = model.GetEntityTypes()
            .Where(e => typeof(TenantScopedEntity).IsAssignableFrom(e.ClrType))
            .ToList();

        // Sanity: the scan actually found the scoped stores (guard against a reflection no-op passing green).
        Assert.NotEmpty(scoped);

        foreach (var entity in scoped)
        {
            var tenantColumn = entity.GetProperties()
                .FirstOrDefault(p => string.Equals(p.GetColumnName(), "tenant_id", StringComparison.Ordinal));
            Assert.True(tenantColumn is not null,
                $"{entity.ClrType.Name} derives from TenantScopedEntity but has no tenant_id column.");

            Assert.True(entity.GetQueryFilter() is not null,
                $"{entity.ClrType.Name} derives from TenantScopedEntity but has no global query filter " +
                "(a tenant could read another tenant's rows).");
        }
    }

    [Fact]
    public void TheTenantMappingTable_IsNotItselfTenantScoped()
    {
        var model = Model();
        var tenants = model.FindEntityType(typeof(TenantEntity));
        Assert.NotNull(tenants);

        // It is the global mapping table: no tenant_id column, no query filter.
        Assert.DoesNotContain(tenants!.GetProperties(),
            p => string.Equals(p.GetColumnName(), "tenant_id", StringComparison.Ordinal));
        Assert.Null(tenants.GetQueryFilter());
        Assert.False(typeof(TenantScopedEntity).IsAssignableFrom(tenants.ClrType));
    }

    /// <summary>
    /// The REVERSE invariant (deny-by-default for new tables). The forward test above only checks entities
    /// that ALREADY derive from <see cref="TenantScopedEntity"/> - so an author who maps a brand-new table
    /// and simply FORGETS to derive from the base would sail past it with no tenant_id and no filter, which is
    /// exactly the cross-tenant leak this guard exists to prevent. This test flips it: EVERY mapped table must
    /// be tenant-scoped, and the only tables allowed to be global are an explicit allowlist (today just the
    /// <c>tenants</c> mapping table). Adding a new global table is therefore a CONSCIOUS act - a line added to
    /// this allowlist with a reason - never a silent omission.
    ///
    /// Owned types are excluded: an owned type mapped to a JSON column (the cron/workflow "sub-doc -> JSON in
    /// a column" pattern) is not its own table - it rides inside its owner's row and its owner's tenant_id, so
    /// it neither needs nor has a tenant_id of its own.
    /// </summary>
    [Fact]
    public void EveryMappedTable_IsTenantScopedOrAnExplicitlyAllowlistedGlobalTable()
    {
        // The ONLY tables permitted to be global (un-scoped). Growing this set must be deliberate: a new
        // entry here is an author consciously declaring "this table is not tenant data".
        // Each entry here is a table that is deliberately NOT tenant-scoped, with the reason it cannot be:
        //
        //  - TenantEntity is the account-to-tenant mapping itself - the table the tenant filter's values COME
        //    FROM. It is read by account subject BEFORE any tenant is resolved, so scoping it would be circular.
        //
        //  - EntitlementEntity is the paid-entitlement record, read at enrollment for the same reason and at
        //    the same point: keyed by account subject, consulted BEFORE a tenant is minted, and specifically in
        //    order to decide whether a tenant may be minted at all. It is also READ-ONLY here - it is written
        //    by the payment side as the service role and this Gateway holds SELECT and nothing more - and it
        //    carries no tenant content: one subject, one subscription state, one period end.
        //
        //  - TeamEntitlementEntity is a team's bill (thefrederiksen/devthrottle_internal#2299), the team
        //    counterpart of EntitlementEntity and global for the same reasons. The website owns the table: it
        //    creates it, the payment side writes it as the service role, and this Gateway holds SELECT and
        //    nothing more. It carries no tenant content: one team id, one subscription state, a seat count, a
        //    period end. Every read is an equality on one team id the caller already holds. And it is read
        //    BEFORE or ACROSS tenant resolution - by the access lease deciding whether a tenant may be served
        //    at all, and by seat convergence, which runs outside any request's tenant - so scoping it to a
        //    tenant would be circular in the same way. It has no tenant_id column to scope by.
        //
        //  - TeamBillEntity and TeamBillChargeEntity are a team's bill as THIS Gateway owns it, and its billing history
        //    (Teams v1, the team bill without Stripe). They replace TeamEntitlementEntity as the source of a team's bill
        //    and are global for the same reasons as the teams table: keyed by the team id, which IS a tenant id, and read
        //    by the access lease and the renewal pass before or across any request's tenant, so scoping them would be
        //    circular. They are written only by TeamBillStore - by the team's Owner (asked of the role table first), on a
        //    membership change, and by the renewal pass - and served only to the team's Owner and Managers.
        //
        //  - DeviceCredentialEntity is the device registry (MTR-14) - an AUTH-RESOLUTION lookup, not tenant
        //    data. A presented key is resolved to its device by its SHA-256 hash BEFORE any tenant is known, and
        //    the tenant is then READ OFF the matched record (each row carries its own tenant binding as a
        //    column). Scoping the table to a tenant would make that resolution circular, exactly like the
        //    tenants mapping. A key still only ever resolves to its OWN bound tenant, so this is not a
        //    cross-tenant read.
        //
        //  - DeviceImportMarkerEntity is the one-time devices.json -> device_credentials import marker (MTR-14A).
        //    It guards a migration that spans every tenant's devices at once and runs before any tenant is
        //    resolved, so it is global for the same reason its table is.
        //
        //  - AccountTrialEntity is the free-trial ledger (issue #2117), the exact counterpart of
        //    EntitlementEntity: keyed by account subject, read at enrollment BEFORE a tenant is minted, and
        //    specifically in order to decide whether a tenant may be minted at all - so scoping it to a tenant
        //    would be circular in the same way. It carries no tenant content: one subject, one start, one end.
        //    Unlike EntitlementEntity this one IS written here, but only ever for the subject the caller has
        //    already been verified as, so a tenant cannot reach another tenant's row through it.
        //
        //  - TrialExtensionEntity is the administrator trial-extension ledger - the audit trail of
        //    AccountTrialEntity, and global for exactly the same reason it is: keyed by the account subject,
        //    which is an identity that exists before any tenant does, so scoping it would be circular in the
        //    same way. It is written only in the same transaction as the trial row it describes, only ever for
        //    the one subject the caller named, and only by an administrator surface that holds a service token
        //    - never by a tenant, so there is no tenant whose rows another tenant could reach through it.
        //
        //  - SessionKeyEntity is the per-SESSION credential registry (Remove-the-network-port phase 1b), and
        //    it is global for precisely the reason DeviceCredentialEntity is: an AUTH-RESOLUTION lookup, not
        //    tenant data. A presented session key is resolved to its session by its SHA-256 hash BEFORE any
        //    tenant is known, and the tenant is then READ OFF the matched row, which carries its own binding
        //    as a column. Scoping the table would make that resolution circular. The binding itself never
        //    comes from a client: it is written from the tenant the registering Director's tunnel bound to at
        //    Hello, which came from that Director's authenticated device key - so a session key only ever
        //    resolves to its OWN account, and both mutations (register, revoke) are scoped by that tenant.
        //
        //  - TurnLogSwitchEntity is the turn-log capture switch (the turn-end research plan, area A), and it
        //    is an OPERATOR setting rather than tenant data. An administrator switches capture on for an
        //    account that is not their own, so a row scoped to the account it names could not be written by
        //    the person entitled to write it; and the recorder reads it at a turn-end boundary to decide
        //    whether to record at all, which is before it is inside any account's partition - so scoping it
        //    would be circular in the same way the auth-resolution tables are. It carries no tenant content:
        //    an account identifier, a machine identifier, a boolean, and who decided. It is written only by
        //    the administrator surface, which holds a service token - never by a tenant - so there is no
        //    tenant whose rows another tenant could reach through it.
        //
        //  - TeamEntity and TeamMemberEntity are the teams and who belongs to each (devthrottle_internal#2300). A team
        //    IS a tenant - its id is the tenant id its rows carry - and these two tables answer which tenants a
        //    person may act in. They are read by the verified account subject BEFORE any tenant is chosen, so
        //    scoping them would be circular in the same way as TenantEntity. A team's member list is served only to
        //    a member of that team, and a team is created only for the verified caller's own subject, so no tenant
        //    reaches another tenant's rows through them.
        //
        //  - TeamInvitationEntity is an invitation to a team (devthrottle_internal#2301). It is opened by the person
        //    invited, from their own personal tenant, before they are a member of the team's tenant, so scoping it to
        //    the team would be circular in the same way. It is listed and changed only by the team's Owner and
        //    Managers, and opened only by whoever holds the secret token from the email.
        var allowedGlobalTables = new HashSet<Type>
        {
            typeof(TenantEntity),
            typeof(TeamEntity),
            typeof(TeamMemberEntity),
            typeof(TeamInvitationEntity),
            typeof(EntitlementEntity),
            typeof(TeamEntitlementEntity),
            typeof(TeamBillEntity),
            typeof(TeamBillChargeEntity),
            typeof(AccountTrialEntity),
            typeof(TrialExtensionEntity),
            typeof(DeviceCredentialEntity),
            typeof(DeviceImportMarkerEntity),
            typeof(SessionKeyEntity),
            typeof(TurnLogSwitchEntity),
        };

        var model = Model();
        var offenders = model.GetEntityTypes()
            // Owned types (JSON sub-documents) are part of their owner's table, not tables in their own right.
            .Where(e => !e.IsOwned())
            .Where(e => !typeof(TenantScopedEntity).IsAssignableFrom(e.ClrType))
            .Where(e => !allowedGlobalTables.Contains(e.ClrType))
            .Select(e => e.ClrType.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These mapped tables are neither tenant-scoped nor an allowlisted global table, so a tenant " +
            "could read another tenant's rows: " + string.Join(", ", offenders) +
            ". Derive the entity from TenantScopedEntity, or - only if it is genuinely global mapping data - " +
            "add it to allowedGlobalTables with a reason.");
    }

    /// <summary>
    /// The OTHER half of tenant scoping: the PRIMARY KEY. The two tests above check the tenant COLUMN and the
    /// query FILTER, and are structurally blind to the key - which is why <c>snoozes</c>, <c>session_spend</c>
    /// and <c>push_subscriptions</c> shipped with a primary key built from CALLER-SUPPLIED input while passing
    /// every guard here. Every store upserts by reading THROUGH the filter and adding when it finds nothing,
    /// so with such a key a second tenant presenting an identifier the first tenant already holds finds
    /// nothing (the filter hides the row), inserts, and hits a PRIMARY KEY violation: it cannot use that
    /// identifier at all (a cross-tenant SQUAT) and the failure tells it another tenant holds it (an EXISTENCE
    /// ORACLE).
    ///
    /// The rule: every tenant-scoped table's primary key MUST include <c>tenant_id</c>. The only exemption is
    /// a key the GATEWAY ITSELF mints as a fresh <see cref="Guid"/> - globally unique by construction and
    /// never a value a caller can present.
    ///
    /// THE EXEMPTION IS A TYPE, NOT A LIST. It used to be a written allowlist of table names, each with a
    /// sentence asserting "this store mints the key with Guid.NewGuid()", and the check beside it re-tested
    /// only the SHAPE of the key - that it was still a single <see cref="Guid"/>. That is true both before AND
    /// after the one change that matters: a store switching from minting the value to accepting one from the
    /// caller keeps the key a single Guid, so the guard stayed green in the exact case it was written to
    /// catch. A check that cannot fail for the dangerous change is worse than no check, because everyone
    /// downstream reads it as protection and stops looking.
    ///
    /// So the exemption now IS <see cref="GatewayMintedKeyEntity"/>, whose <c>Id</c> has a private setter and
    /// is minted by the base class itself. Assigning a caller-supplied value to it does not fail here - it
    /// does not COMPILE. What is left for this test is the two ways to escape that type while keeping a
    /// tenant-less key, and it closes both by construction:
    ///
    ///  - DROP the base class (and re-add a settable <c>Id</c>): the entity is then not a
    ///    <see cref="GatewayMintedKeyEntity"/>, so it needs <c>tenant_id</c> in its key and turns this red.
    ///  - KEEP the base class but key the table on something else - a re-declared <c>Id</c> that shadows the
    ///    base one, or any caller-supplied column: the mapped key property is then no longer DECLARED ON
    ///    <see cref="GatewayMintedKeyEntity"/>, which is what this test requires, so it turns red too.
    ///
    /// The third escape - widening the private setter so a caller value compiles again - is held by
    /// <see cref="TheGatewayMintedKey_CanOnlyBeWrittenByTheBaseClassThatMintsIt"/> below.
    /// </summary>
    [Fact]
    public void EveryTenantScopedTable_HasTenantIdInItsPrimaryKey_UnlessTheGatewayMintsTheKeyItself()
    {
        var model = Model();
        var scoped = model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .Where(e => typeof(TenantScopedEntity).IsAssignableFrom(e.ClrType))
            .ToList();

        // Sanity: the scan actually found the scoped stores (guard against a reflection no-op passing green).
        Assert.NotEmpty(scoped);

        // Sanity: the exempt population is not empty either. If a refactor ever unhooked every entity from
        // GatewayMintedKeyEntity, the exemption branch below would stop executing and this test would go
        // green while testing nothing about it.
        Assert.Contains(scoped, e => typeof(GatewayMintedKeyEntity).IsAssignableFrom(e.ClrType));

        var offenders = new List<string>();
        foreach (var entity in scoped)
        {
            var key = entity.FindPrimaryKey();
            Assert.True(key is not null, $"{entity.ClrType.Name} is tenant-scoped but has no primary key.");

            var keyHasTenant = key!.Properties.Any(
                p => string.Equals(p.GetColumnName(), "tenant_id", StringComparison.Ordinal));
            if (keyHasTenant)
                continue;

            // The ONLY exemption: the key is the Id that GatewayMintedKeyEntity itself declares and mints.
            // Note this asks where the key property is DECLARED, not merely what type it is - a re-declared
            // or shadowing Id on the derived entity is settable by callers again and is therefore NOT this.
            var keyIsTheMintedId = key.Properties.Count == 1
                                   && key.Properties[0].PropertyInfo?.DeclaringType
                                       == typeof(GatewayMintedKeyEntity);
            if (keyIsTheMintedId)
                continue;

            offenders.Add(entity.ClrType.Name + " (key: " +
                          string.Join(", ", key.Properties.Select(p => p.GetColumnName())) + ")");
        }

        Assert.True(offenders.Count == 0,
            "These tenant-scoped tables have a PRIMARY KEY that does not include tenant_id and is not the " +
            "Gateway-minted Id, so one tenant can squat a key value for every other tenant and learn that " +
            "another tenant holds it: " + string.Join("; ", offenders) +
            ". Make the key composite - HasKey(e => new { e.TenantId, e.<Key> }) - and add the migration on " +
            "BOTH providers; or, only if the key is genuinely the Gateway's to mint, derive the entity from " +
            "GatewayMintedKeyEntity and key the table on its Id.");
    }

    /// <summary>
    /// The mechanism behind the exemption above, asserted directly. <see cref="GatewayMintedKeyEntity"/> earns
    /// a primary key without <c>tenant_id</c> on exactly one ground: no caller can choose that key, because
    /// only the base class can write it. That is not a property of the key's TYPE (a caller-supplied Guid
    /// looks identical to a minted one) - it is a property of its ACCESSIBILITY, so accessibility is what this
    /// checks.
    ///
    /// This is the test that fails for the dangerous change. Widening the setter is the ONLY way to make
    /// <c>entity.Id = someCallerSuppliedGuid</c> compile again, so the moment someone does it to unblock
    /// themselves, this turns red and names the reason instead of letting the exemption quietly go false.
    /// </summary>
    [Fact]
    public void TheGatewayMintedKey_CanOnlyBeWrittenByTheBaseClassThatMintsIt()
    {
        var id = typeof(GatewayMintedKeyEntity).GetProperty(nameof(GatewayMintedKeyEntity.Id));
        Assert.True(id is not null, "GatewayMintedKeyEntity no longer declares an Id property.");

        var setter = id!.SetMethod;
        Assert.True(setter is not null,
            "GatewayMintedKeyEntity.Id has no setter at all. EF needs one (or the backing field) to " +
            "materialize a loaded row - restore a PRIVATE setter.");

        Assert.True(setter!.IsPrivate,
            "GatewayMintedKeyEntity.Id's setter is no longer private (it is now " +
            (setter.IsPublic ? "public" : setter.IsFamily ? "protected" : "internal or protected internal") +
            "). That setter being private is the WHOLE reason these tables are exempt from having tenant_id " +
            "in their primary key: it is what makes 'the Gateway mints this key' something the compiler " +
            "enforces rather than a sentence someone wrote. With a wider setter a caller-supplied value can " +
            "be assigned, and then one tenant can squat that key for every other tenant and learn from the " +
            "insert failure that another tenant holds it. Put the setter back to private; if the key really " +
            "must be caller-supplied, stop deriving from GatewayMintedKeyEntity and put tenant_id in the key.");

        // And nothing else on the type may hand the value out for writing either.
        Assert.DoesNotContain(typeof(GatewayMintedKeyEntity).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Static),
            f => !f.IsInitOnly);
    }
}
