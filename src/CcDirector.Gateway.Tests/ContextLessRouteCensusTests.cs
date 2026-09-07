using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CcDirector.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE CONTEXT-LESS ROUTE CENSUS, CLOSED AND EXECUTABLE (tenant-boundary hardening, release
/// 2026-07-31, the brief's item 5).
///
/// A route that takes a path parameter but NO <see cref="HttpContext"/> cannot read the caller's
/// request, so it cannot resolve a tenant from it. The original census counted such routes, probed a
/// handful, and refused to generalise - correctly, because eight samples cannot stand for a family. The
/// gap it left was not a missing test but a missing INVENTORY: nobody could say what the whole set was,
/// so nobody could say the whole set was safe.
///
/// This file closes that by making the inventory a TEST rather than a paragraph. It reads the FINALISED
/// route table from a real <see cref="GatewayHost"/> - the actual endpoints, with route-group prefixes
/// applied, and each handler's real parameter list read by reflection - and asserts the context-less set
/// is EXACTLY the ruled list below. Every entry in that list carries a written verdict naming the
/// mechanism that keeps one tenant out of another's data, and the report for this phase carries the
/// evidence per row.
///
/// WHY THIS SHAPE, AND NOT A PROSE TABLE. A table in a document is true on the day it is written. This
/// fails the moment a new context-less route is mapped, so the next person cannot add one without
/// reaching a verdict about it - which is the property the census was supposed to buy and could not,
/// because it was prose. It also cannot be fooled by a source-parsing mistake: patterns come from the
/// route table, not from reading Map calls, so a route mounted under a group prefix is counted at its
/// real path (the earlier source-derived attempt read /vault/keys/{name} as /{name} and would have
/// mis-stated the census).
///
/// BOTH DEPLOYMENTS ARE COUNTED, because they map different route tables. On HOSTED, the
/// <c>HostedRouteDeny</c> families (the key vault, the developer exe slots) are not mapped at all - a
/// verb-less refusal catch-all claims their prefixes - so their routes cannot serve any tenant. That is
/// a verdict this test EXECUTES rather than asserts on faith: the hosted case must not contain them.
/// </summary>
public sealed class ContextLessRouteCensusTests
{
    private readonly ITestOutputHelper _out;

    public ContextLessRouteCensusTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The census of context-less routes on the HOSTED Gateway - the multi-tenant deployment, and so the
    /// only one where cross-tenant reach is possible at all. Each row's verdict:
    ///
    /// EF global tenant filter (the ambient request scope entered by the device-key middleware, plus
    /// GatewayDbContext's per-entity query filter; GatewayDatabase throws rather than defaulting when no
    /// scope exists). Executed cross-tenant for every family below - see the phase report's table for
    /// which test covers which row:
    ///   /cron/jobs/{id} (+ DELETE, /runs)          cron_jobs, cron_runs
    ///   /gateway/governance/session-spend/{id}     session_spend
    ///   /lists/{name} (+ /consumer, /items/...)    worklists, worklist_items
    ///   /gateway/workflows/{id} and its family     workflows, workflow_versions, workflow_files,
    ///                                              workflow_tenant_overrides
    ///   /gateway/workflow-runs/{id}                workflow_runs
    ///   /gateway/skills/{id} and its family        skills, skill_versions, skill_files,
    ///                                              skill_tenant_overrides
    ///   /gateway/rules/{id:guid} (+ /firings)      session_rules, session_rule_firings
    ///   /gateway/workspaces/{id} (GET + DELETE)   workspaces
    ///     - probed by CensusRouteTenancyProbeTests.Workspaces_ContextLess_KeepEachTenantsWorkspaceUnderTheSameSlugSeparate
    ///
    /// THE WORKSPACES FAMILY (issue #2722), ruled on here because it is added by the same change that
    /// adds the routes. Two context-less routes, GET and DELETE on /gateway/workspaces/{id}, and what
    /// confines them:
    ///
    ///   - WorkspaceEntity derives from TenantScopedEntity, so TenantScopeGuardTests - which reflects
    ///     the REAL EF model rather than reading a list somebody keeps - asserts it carries tenant_id
    ///     AND the deny-by-default global query filter.
    ///   - The primary key is COMPOSITE, (tenant_id, Id). The id is a slug the CALLER mints, exactly
    ///     like a skill or workflow id, so the same reasoning applies: two tenants may hold
    ///     "morning-fleet" without colliding, and a read of another account's id answers 404 - the
    ///     same answer as an id nobody has ever used, so there is no existence oracle.
    ///   - GatewayDatabase.CreateContext() sets ActiveTenant from the ambient scope the device-key
    ///     middleware entered, and THROWS on an invalid tenant rather than defaulting, so a workspace
    ///     query cannot run unscoped; writes additionally meet GatewayDbContext.SaveChanges, which
    ///     refuses a row whose TenantId is not the connection's.
    ///
    /// PUT and POST on this family are NOT in the census and that is not an omission: the PUT takes the
    /// HttpContext it reads the body from, and the POST takes no path parameter.
    ///
    /// THE RULES FAMILY, ruled on here because it shipped without one (issue #2679). The three routes take
    /// an id and no HttpContext, and nothing in the route confines them - the confinement is in the MODEL,
    /// which is why reading the endpoint alone cannot settle it:
    ///
    ///   - SessionRuleEntity and SessionRuleFiringEntity both derive from GatewayMintedKeyEntity, which
    ///     derives from TenantScopedEntity. TenantScopeGuardTests reflects the REAL EF model and asserts
    ///     every such entity carries tenant_id AND the deny-by-default global query filter, so these two
    ///     are covered by a check that enumerates rather than by a list somebody keeps.
    ///   - The primary key is a Guid the Gateway MINTS, with a private setter only that base class can
    ///     write, so there is no caller-supplied key to squat on and no existence oracle - the hazard the
    ///     guard's third rule exists for.
    ///   - GatewayDatabase.CreateContext() sets ActiveTenant from the ambient scope the device-key
    ///     middleware entered, and THROWS on an invalid tenant rather than defaulting, so a rules query
    ///     cannot run unscoped.
    ///   - Writes additionally meet GatewayDbContext.SaveChanges, which refuses a row whose TenantId is not
    ///     the connection's.
    ///
    /// EXECUTED, not argued: CensusRouteTenancyProbeTests hands the other account a real rule id and asks
    /// it to read, delete, and read the firing record. The firing probe seeds a firing through the store
    /// first, because no route creates one and two empty lists compared against each other would pass with
    /// the filter removed.
    ///
    /// POST /gateway/rules and POST /gateway/rules/{id:guid}/promote are NOT in this census and that is not
    /// an omission: the first takes no path parameter, and the second takes the HttpContext it needs to
    /// mint a promotion grant, so neither is context-less.
    ///
    /// Hosted deny (the legacy same-machine discovery plane - not a tenant surface at all; refused on
    /// hosted, gated on the process-level hosted flag and proven by
    /// <see cref="NullBoundaryHostedGateFailClosedTests"/>):
    ///   POST /directors/{id}/doorbell, DELETE /directors/{id}/registration
    /// </summary>
    private static readonly string[] HostedCensus =
    {
        "DELETE /cron/jobs/{id}",
        "DELETE /directors/{id}/registration",
        "DELETE /gateway/rules/{id:guid}",
        "DELETE /gateway/skills/{id}",
        "DELETE /gateway/workflows/{id}",
        "DELETE /gateway/workspaces/{id}",
        "DELETE /lists/{name}/consumer",
        "DELETE /lists/{name}/items/{source}/{id}",
        "GET /cron/jobs/{id}",
        "GET /cron/jobs/{id}/runs",
        "GET /gateway/governance/session-spend/{sessionId}",
        "GET /gateway/rules/{id:guid}",
        "GET /gateway/rules/{id:guid}/firings",
        "GET /gateway/skills/{id}",
        "GET /gateway/skills/{id}/body",
        "GET /gateway/skills/{id}/files/{**filePath}",
        "GET /gateway/skills/{id}/versions",
        "GET /gateway/skills/{id}/versions/{version:int}",
        "GET /gateway/workflow-runs/{id:guid}",
        "GET /gateway/workflows/{id}",
        "GET /gateway/workflows/{id}/files/{fileName}",
        "GET /gateway/workflows/{id}/instructions",
        "GET /gateway/workflows/{id}/versions",
        "GET /gateway/workflows/{id}/versions/{version:int}",
        "GET /gateway/workspaces/{id}",
        "GET /lists/{name}",
        "POST /directors/{id}/doorbell",
        "POST /gateway/skills/{id}/clone",
        "POST /gateway/skills/{id}/disable",
        "POST /gateway/skills/{id}/enable",
        "POST /gateway/skills/{id}/publish",
        "POST /gateway/workflows/{id}/clone",
        "POST /gateway/workflows/{id}/disable",
        "POST /gateway/workflows/{id}/enable",
        "POST /gateway/workflows/{id}/publish",
    };

    /// <summary>
    /// The two families that exist ONLY off hosted, because <c>HostedRouteDeny</c> takes them off the
    /// hosted route table entirely. Their verdict is therefore "not reachable on the multi-tenant
    /// deployment", and the hosted assertion proves it by their ABSENCE there:
    ///   GET/DELETE /vault/keys/{name}      - the key vault (family "key-vault")
    ///   DELETE /exes/slots/{n}, POST /exes/slots/{n}/build-start - the developer exe slots
    ///     (family "exes-slots"); additionally mapped only on Windows.
    /// On self-host both are single-owner surfaces behind the host-wide credential gate.
    /// </summary>
    private static readonly string[] SelfHostOnlyExtras =
    {
        "DELETE /exes/slots/{n}",
        "DELETE /vault/keys/{name}",
        "GET /vault/keys/{name}",
        "POST /exes/slots/{n}/build-start",
    };

    [Fact]
    public async Task The_hosted_context_less_route_set_is_exactly_the_ruled_census()
    {
        var actual = await ContextLessRoutes(hosted: true);
        foreach (var row in actual) _out.WriteLine(row);

        // If this fails, a context-less route was added, removed, or re-pathed on the HOSTED Gateway.
        // That is not automatically a defect - but it IS a verdict nobody has reached yet. Establish
        // which store the route reaches and what confines it to the caller's tenant, write that verdict
        // into the doc comment above, add a cross-tenant probe if the family has none, and only then
        // add the row here.
        Assert.Equal(HostedCensus, actual);
    }

    [Fact]
    public async Task The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families()
    {
        var actual = await ContextLessRoutes(hosted: false);
        foreach (var row in actual) _out.WriteLine(row);

        var expected = HostedCensus.Concat(SelfHostOnlyExtras).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);

        // The point of the pair: the key vault and the developer exe slots are context-less routes over
        // process-global stores with no tenant column at all, and they are reachable on self-host and
        // NOT on hosted. This asserts that against the OBSERVED hosted route table - re-read here rather
        // than compared against the ruled constant above, because comparing two constants would be an
        // assertion this file cannot fail. Each extra must be present in the self-host table (it is, by
        // the equality above) and ABSENT from the hosted one.
        var hostedActual = await ContextLessRoutes(hosted: true);
        foreach (var row in SelfHostOnlyExtras)
        {
            Assert.Contains(row, actual);
            Assert.DoesNotContain(row, hostedActual);
        }
    }

    /// <summary>
    /// The finalised route table of a real host, reduced to the context-less routes: a path parameter in
    /// the pattern, and no <see cref="HttpContext"/> in the handler's parameter list. The hosted refusal
    /// catch-alls are excluded by name - they carry no handler method at all and exist precisely to make
    /// a denied family unreachable, so counting them as census rows would invert their meaning.
    /// </summary>
    private static async Task<string[]> ContextLessRoutes(bool hosted)
    {
        var prior = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);
        var dir = Path.Combine(Path.GetTempPath(), "cc-census-routes-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "census-token",
            authEnabled: true,
            instancesDirectory: dir,
            workListsPath: Path.Combine(dir, "worklists", "worklists.json"));
        try
        {
            await gateway.StartAsync();

            return gateway.MappedEndpoints.OfType<RouteEndpoint>()
                .Select(e => new
                {
                    Pattern = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'),
                    Methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>(),
                    Handler = e.Metadata.GetMetadata<MethodInfo>(),
                })
                .Where(r => r.Pattern.Contains('{', StringComparison.Ordinal))
                .Where(r => !r.Pattern.Contains("hostedDeniedPath", StringComparison.Ordinal))
                .Where(r => r.Handler is not null
                            && !r.Handler.GetParameters().Any(p => p.ParameterType == typeof(HttpContext)))
                .SelectMany(r => r.Methods.DefaultIfEmpty("ANY"), (r, m) => $"{m} {r.Pattern}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            await gateway.StopAsync();
            Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", prior);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}
