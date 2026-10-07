using System.Reflection;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using NetArchTest.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Architecture;

/// <summary>
/// The G8 STRUCTURAL tenant-isolation gate (increment 1). These are build-time architecture tests: they turn
/// the Gateway's tenant isolation from "we fixed the leaks we found" into "a cross-tenant leak cannot be
/// WRITTEN," by asserting the isolation is enforced BY CONSTRUCTION rather than by each handler remembering to
/// enter a scope. They must PASS on current main (they freeze the good shape that already exists); a red rule
/// means a real isolation gap, never a rule to weaken. Each rule ships with its own revert-proof description
/// (design section 7) - remove the property it protects and the rule goes red.
///
/// Design: docs/reviews/production-readiness/mission/design-g8-structural-tenant-gate.md (sections 5-7).
///
///  - DT-TEN-1: only <see cref="GatewayDatabase"/> may construct or obtain a <see cref="GatewayDbContext"/>,
///    so every tenant-scoped read/write is forced through the tenant boundary (CreateContext, fail-loud) or
///    the null-tenant unscoped door (fail-closed). This is the single most important rule.
///  - DT-TEN-2: every mapped entity is either a <see cref="TenantScopedEntity"/> carrying the ActiveTenant
///    global query filter, or is named on the GlobalUnscopedTables allowlist. No silent third category.
///  - System-capability allowlist: the reserved <see cref="TenantId.System"/> scope is entered ONLY at the
///    sanctioned composition-root site, and every deliberate cross-tenant capability names itself on
///    <see cref="SystemCapabilityAllowlist"/>.
///  - DT-TEN-3 (workers carry a tenant via TenantScopedSweep) is INCREMENT 2 - see the skipped placeholder
///    at the bottom of this file.
/// </summary>
public sealed class TenantGateArchitectureTests
{
    private static readonly Assembly GatewayAssembly = typeof(GatewayDatabase).Assembly;

    /// <summary>
    /// The types allowed to CONSTRUCT a <see cref="GatewayDbContext"/>. <see cref="GatewayDatabase"/> is the
    /// runtime chokepoint (it obtains contexts through the pooled factory and is the sanctioned owner); the
    /// design-time factory news one for the <c>dotnet ef</c> tooling ONLY - never at runtime - and the context
    /// it builds is unscoped scaffolding that serves no tenant data.
    /// </summary>
    private static readonly string[] ContextConstructionAllowed =
    {
        nameof(GatewayDatabase),
        nameof(GatewayDbContextDesignTimeFactory),
    };

    /// <summary>
    /// The deliberately UN-scoped global tables: no <c>tenant_id</c> column and no query filter, because a row
    /// is resolved BEFORE any tenant is known (the account-subject census, the by-hash device-key lookup) or
    /// is owned by another system entirely (the read-only entitlements table). This is the "sealed as a named
    /// capability" record for global tables (design 5.4): adding a table here is the one-line, review-visible
    /// act DT-TEN-2 forces for any non-scoped table.
    /// </summary>
    private static readonly HashSet<string> GlobalUnscopedTables = new(StringComparer.Ordinal)
    {
        nameof(TenantEntity),             // the account-subject -> tenant mapping (the tenant census itself)
        nameof(EntitlementEntity),        // paid entitlements owned/written by the payment side; Gateway only READs
        // A TEAM's bill (#2299), owned and written by the payment side exactly like the row above; the Gateway
        // only READs it. Keyed by the team id - which IS a tenant id - so it carries no separate tenant_id
        // column and no query filter: the key already names the one tenant the row is about.
        nameof(TeamEntitlementEntity),
        // A TEAM's bill as the Gateway owns it (Teams v1, the team bill without Stripe) and its billing history. Keyed by
        // the team id, which IS a tenant id, and read by the access lease and the renewal pass before or across any
        // request's tenant - so, like the teams table, scoping them would be circular.
        nameof(TeamBillEntity),
        nameof(TeamBillChargeEntity),
        nameof(AccountTrialEntity),       // free-trial ledger, keyed by account subject and read pre-tenant (#2117)
        // Teams and their members (devthrottle_internal#2300). A team IS a tenant, and these two tables are what say
        // which tenants a person may act in - read by the verified account subject BEFORE a tenant is chosen, so
        // scoping them to a tenant would be circular, exactly as for the tenants table. A team's member list is
        // served only to a member of that team, and a team is created only for the verified caller's own subject.
        nameof(TeamEntity),
        nameof(TeamMemberEntity),
        // Invitations to a team (devthrottle_internal#2301): opened by the person invited, from their own personal
        // tenant, before they are a member of the team's - so, like team_members, scoping them would be circular.
        nameof(TeamInvitationEntity),
        // The administrator trial-extension audit trail - the ledger OF account_trials directly above, and
        // global for the same reason it is: keyed by the account subject, an identity that exists before any
        // tenant does, so scoping it would be circular in exactly the same way. It is written only in the same
        // transaction as the trial row it describes, only for the one subject the caller named, and only by a
        // service-token-authorized administrator surface - never by a tenant, so there is no tenant whose rows
        // another tenant could reach through it.
        nameof(TrialExtensionEntity),
        nameof(DeviceCredentialEntity),   // per-device key records; a presented key is resolved by hash pre-tenant
        nameof(DeviceImportMarkerEntity), // one-time devices.json import idempotency markers (global, like above)
        // Per-SESSION Gateway credentials (Remove-the-network-port phase 1b). Global for exactly the same
        // reason as device_credentials directly above: a presented session key is resolved by its HASH before
        // any tenant is known, and the tenant is READ OFF the matched row. Scoping the table by tenant would
        // make the resolution circular - it would need the answer in order to ask the question. The row's
        // TenantId is a plain data column (whose account this session belongs to), and it is what the auth
        // gate then enters as the caller's scope for the rest of the request.
        nameof(SessionKeyEntity),
        // The turn-log capture switch (the turn-end research plan, area A). An OPERATOR setting, not tenant
        // data: an administrator switches capture on for an account that is not their own, so a tenant-scoped
        // row could not be written by the person entitled to write it, and the recorder reads it at the
        // turn-end boundary to decide whether to record at all - before it is inside any account's partition.
        // It holds an account identifier, a machine identifier, a boolean, and who decided and why; it is
        // written only by the service-token administrator surface, never by a tenant.
        nameof(TurnLogSwitchEntity),
    };

    // ----------------------------------------------------------------------------------------------------
    // DT-TEN-1: the data chokepoint is sole.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-TEN-1 (construction): a <see cref="GatewayDbContext"/> is CONSTRUCTED (newobj) only inside the
    /// sanctioned types. A metadata scan (Mono.Cecil) over the compiled Gateway assembly is used rather than a
    /// plain dependency check, because a store legitimately REFERENCES the context type (it is the return of
    /// CreateContext) - what must be forbidden is a foreign type MINTING its own.
    ///
    /// Revert-proof: add a store that does <c>new GatewayDbContext(...)</c> anywhere but the sanctioned types
    /// and this test goes red.
    /// </summary>
    [Fact]
    public void DT_TEN_1_GatewayDbContext_is_constructed_only_by_the_sanctioned_types()
    {
        var contextFullName = typeof(GatewayDbContext).FullName!;
        var offenders = new List<string>();

        using var module = ModuleDefinition.ReadModule(GatewayAssembly.Location);
        foreach (var type in AllTypes(module))
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.OpCode.Code != Mono.Cecil.Cil.Code.Newobj) continue;
                    if (instr.Operand is not MethodReference ctor) continue;
                    if (ctor.DeclaringType.FullName != contextFullName) continue;

                    var owner = TopLevel(type).Name;
                    if (!ContextConstructionAllowed.Contains(owner))
                        offenders.Add($"{type.FullName}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "DT-TEN-1: GatewayDbContext may be constructed ONLY by " + string.Join(" / ", ContextConstructionAllowed) +
            ". A foreign type minting its own context skips the tenant boundary. Offending constructors: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// DT-TEN-1 (obtain): only <see cref="GatewayDatabase"/> may DEPEND ON the pooled
    /// <c>IDbContextFactory&lt;GatewayDbContext&gt;</c>. Holding the factory is the other way to obtain a
    /// context; if nothing else can hold it, nothing else can hand out a scope-less context. Checked by
    /// reflection over every declared member surface (fields, properties, method/ctor parameters, returns).
    ///
    /// Revert-proof: inject or field the factory into any other type and this test goes red.
    /// </summary>
    [Fact]
    public void DT_TEN_1_only_GatewayDatabase_depends_on_the_context_factory()
    {
        var offenders = new List<string>();
        foreach (var type in SafeGetTypes(GatewayAssembly))
        {
            if (TopLevelName(type) == nameof(GatewayDatabase)) continue; // the sanctioned owner (and its lambdas)
            if (TypeReferencesContextFactory(type))
                offenders.Add(type.FullName ?? type.Name);
        }

        Assert.True(offenders.Count == 0,
            "DT-TEN-1: only GatewayDatabase may depend on IDbContextFactory<GatewayDbContext>. Any other holder " +
            "can obtain a scope-less context and bypass the tenant boundary. Offending types: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// DT-TEN-1 (belt-and-suspenders, NetArchTest): no Gateway type other than <see cref="GatewayDatabase"/>
    /// takes a type-graph dependency on <c>IDbContextFactory</c>. This is the type-graph mirror of the precise
    /// reflection check above; the sole IDbContextFactory in the assembly is the GatewayDbContext one inside
    /// GatewayDatabase, so this passes today and reddens on any new dependent.
    /// </summary>
    [Fact]
    public void DT_TEN_1_no_foreign_type_takes_a_dependency_on_the_context_factory()
    {
        var result = Types.InAssembly(GatewayAssembly)
            .That().DoNotHaveNameStartingWith(nameof(GatewayDatabase))
            .ShouldNot().HaveDependencyOn("Microsoft.EntityFrameworkCore.IDbContextFactory")
            .GetResult();

        var failing = result.FailingTypeNames is null ? "" : string.Join(", ", result.FailingTypeNames);
        Assert.True(result.IsSuccessful,
            "DT-TEN-1: only GatewayDatabase may depend on IDbContextFactory<GatewayDbContext>. Offending types: " + failing);
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-TEN-2: model completeness - no un-scoped scoped data.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-TEN-2: every mapped entity is either a <see cref="TenantScopedEntity"/> that carries the tenant_id
    /// property AND the ActiveTenant global query filter, or is named on <see cref="GlobalUnscopedTables"/>.
    /// A new store whose entity forgets to derive from <see cref="TenantScopedEntity"/> - the silent third
    /// category - fails the build. Owned (JSON sub-document) entity types are skipped: they inherit the owner's
    /// scope and have no independent data-access surface.
    ///
    /// Revert-proof: drop an <c>ApplyTenantScope&lt;T&gt;</c> line (or remove a query filter) for a scoped
    /// entity, or map a new entity that is neither scoped nor allowlisted, and this test goes red.
    /// </summary>
    [Fact]
    public void DT_TEN_2_every_mapped_entity_is_tenant_scoped_or_named_on_the_global_allowlist()
    {
        using var ctx = BuildModelContext();
        var problems = new List<string>();

        foreach (var entityType in ctx.Model.GetEntityTypes())
        {
            if (entityType.IsOwned()) continue; // owned JSON sub-docs inherit the owner's tenant scope

            var clr = entityType.ClrType;

            if (typeof(TenantScopedEntity).IsAssignableFrom(clr))
            {
                if (entityType.FindProperty(nameof(TenantScopedEntity.TenantId)) is null)
                    problems.Add($"{clr.Name}: a TenantScopedEntity with no mapped TenantId (tenant_id) property");

                var filter = entityType.GetQueryFilter();
                if (filter is null || !filter.ToString().Contains("ActiveTenant", StringComparison.Ordinal))
                    problems.Add($"{clr.Name}: a TenantScopedEntity with no ActiveTenant global query filter " +
                        "(reads would not be scoped to the active tenant)");
            }
            else if (!GlobalUnscopedTables.Contains(clr.Name))
            {
                problems.Add($"{clr.Name}: mapped but neither a TenantScopedEntity nor named on GlobalUnscopedTables. " +
                    "Either derive it from TenantScopedEntity (so it is tenant-filtered) or, if it is deliberately " +
                    "global, add its name to the GlobalUnscopedTables allowlist (a review-visible act).");
            }
        }

        Assert.True(problems.Count == 0,
            "DT-TEN-2: found tenant-scoped data that is not enforced by the global query filter, or a mapped table " +
            "in no known category: " + string.Join("; ", problems));
    }

    // ----------------------------------------------------------------------------------------------------
    // The System-capability allowlist: deliberate cross-tenant reads must NAME themselves.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The named cross-tenant allowlist carries the already-sealed fleet-wide Director listing, and that name
    /// points at a REAL seal: the <c>ListDirectors(SystemScope)</c> overload a handler (holding no token)
    /// cannot call. This binds the review-gated name to the mechanism it names.
    /// </summary>
    [Fact]
    public void System_capability_allowlist_names_the_sealed_fleet_director_list()
    {
        Assert.Contains(SystemCapabilityAllowlist.FleetDirectorList, SystemCapabilityAllowlist.Names);

        var sealedFleetList = typeof(DirectorRegistry).GetMethod(
            "ListDirectors", BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { typeof(SystemScope) }, modifiers: null);

        Assert.True(sealedFleetList is not null,
            "The 'fleet-director-list' capability names DirectorRegistry.ListDirectors(SystemScope); that sealed " +
            "fleet-wide overload must exist for the allowlist entry to mean anything.");
    }

    /// <summary>
    /// The shared workflow library read (devthrottle_internal issue 514) is a deliberate cross-tenant
    /// capability and must stay NAMED on the allowlist, bound to its real mechanism: the workflow
    /// stores resolve a workflow id's owning partition through a library-scoped context (the private
    /// owning-context resolver), never by entering the System scope. If either half disappears - the
    /// name or the mechanism - this test goes red and the reach is no longer review-visible.
    /// </summary>
    [Fact]
    public void System_capability_allowlist_names_the_shared_workflow_library_read()
    {
        Assert.Contains(SystemCapabilityAllowlist.SharedWorkflowLibraryRead, SystemCapabilityAllowlist.Names);

        var catalogResolver = typeof(Workflows.WorkflowStore).GetMethod(
            "OpenOwningContext", BindingFlags.NonPublic | BindingFlags.Instance);
        var runResolver = typeof(Workflows.WorkflowRunStore).GetMethod(
            "OpenOwningWorkflowContext", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.True(catalogResolver is not null,
            "The 'shared-workflow-library-read' capability names WorkflowStore's owning-partition " +
            "resolver (OpenOwningContext); that mechanism must exist for the allowlist entry to mean anything.");
        Assert.True(runResolver is not null,
            "The 'shared-workflow-library-read' capability names WorkflowRunStore's owning-partition " +
            "resolver (OpenOwningWorkflowContext); that mechanism must exist for the allowlist entry to mean anything.");
    }

    /// <summary>
    /// The reserved <see cref="TenantId.System"/> scope may be ENTERED only at the sanctioned composition-root
    /// startup-seeding site (GatewayHost). System scope is reached ONLY by code that explicitly enters it and
    /// is NEVER the answer to "no tenant resolved"; a new site that enters it is a new cross-tenant reach that
    /// must name itself on <see cref="SystemCapabilityAllowlist"/> and be sanctioned here. A source scan
    /// (mirroring <c>SystemScopeGuardTests</c>) keeps this cheap and provider-free.
    ///
    /// Revert-proof: enter TenantId.System from any file but GatewayHost.cs and this test goes red.
    /// </summary>
    [Fact]
    public void Reserved_System_scope_is_entered_only_at_the_sanctioned_composition_root_site()
    {
        var gatewaySrc = LocateGatewaySource();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(gatewaySrc, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "GatewayHost.cs") continue; // the sanctioned startup-seeding site

            var lineNo = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNo++;
                if (!line.Contains("TenantId.System", StringComparison.Ordinal)) continue;
                if (line.Contains(".Enter(", StringComparison.Ordinal) ||
                    line.Contains(".EnterScope(", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(gatewaySrc, file)}:{lineNo}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "System scope: the reserved TenantId.System scope may be ENTERED only at the sanctioned " +
            "composition-root startup-seeding site (GatewayHost.cs). A new cross-tenant/System reach must name " +
            "itself on SystemCapabilityAllowlist and be sanctioned here. Offending entries: " +
            string.Join(", ", offenders));
    }

    // ----------------------------------------------------------------------------------------------------
    // DT-TEN-3: workers carry a tenant. INCREMENT 2 - not implemented here.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// DT-TEN-3 (design section 5.5): no <c>System.Threading.Timer</c> field, <c>IHostedService</c>, or
    /// timer-callback method in the Gateway may reference a GatewayDatabase-backed store except through the
    /// worker seam <c>CcDirector.Gateway.Tenancy.TenantScopedSweep</c>. That seam does not exist yet - it is
    /// G8 INCREMENT 2 (design section 4.1). This rule cannot be written until the seam lands, so it is a
    /// documented placeholder rather than a silently-absent rule.
    ///
    /// TODO(g8-increment-2): implement this rule against TenantScopedSweep once the worker seam is built, and
    /// migrate the currently hosted-disabled sweeps onto it.
    /// </summary>
    [Fact(Skip = "DT-TEN-3 is G8 increment 2: it needs the TenantScopedSweep worker seam (design sections 4.1 / 5.5).")]
    public void DT_TEN_3_background_workers_touch_stores_only_through_TenantScopedSweep()
    {
        // Intentionally empty - see the summary. The rule is defined; its subject (TenantScopedSweep) is not
        // built in increment 1.
    }

    // ----------------------------------------------------------------------------------------------------
    // Helpers.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Build a GatewayDbContext purely to read its EF model. UseSqlite selects a provider so the model
    /// builds (IsNpgsql() is false, giving the provider-agnostic model); no connection is ever opened.</summary>
    private static GatewayDbContext BuildModelContext()
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new GatewayDbContext(options);
    }

    /// <summary>True when the type holds or exchanges an <c>IDbContextFactory&lt;GatewayDbContext&gt;</c> on any
    /// declared member surface (field, property, method/ctor parameter, or return).</summary>
    private static bool TypeReferencesContextFactory(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var field in type.GetFields(all))
            if (IsContextFactory(field.FieldType)) return true;

        foreach (var prop in type.GetProperties(all))
            if (IsContextFactory(prop.PropertyType)) return true;

        foreach (var ctor in type.GetConstructors(all))
            foreach (var p in ctor.GetParameters())
                if (IsContextFactory(p.ParameterType)) return true;

        foreach (var method in type.GetMethods(all))
        {
            if (IsContextFactory(method.ReturnType)) return true;
            foreach (var p in method.GetParameters())
                if (IsContextFactory(p.ParameterType)) return true;
        }

        return false;
    }

    private static bool IsContextFactory(Type t)
        => t.IsGenericType
           && t.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)
           && t.GetGenericArguments()[0] == typeof(GatewayDbContext);

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var t in module.Types)
            foreach (var nested in Flatten(t))
                yield return nested;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
            foreach (var inner in Flatten(nested))
                yield return inner;
    }

    private static TypeDefinition TopLevel(TypeDefinition type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }

    private static string TopLevelName(Type type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type.Name;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static string LocateGatewaySource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CcDirector.Gateway");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/CcDirector.Gateway from " + AppContext.BaseDirectory);
    }
}
