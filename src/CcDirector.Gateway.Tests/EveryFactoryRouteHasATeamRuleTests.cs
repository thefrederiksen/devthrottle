using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Teams;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// FACTORIES INSIDE A TEAM, ON THE REAL ROUTE TABLE (Team implementation 2.1, devthrottle_internal#2792). Reads the
/// FINALISED route table of a real hosted <see cref="GatewayHost"/> (<see cref="GatewayHost.MappedEndpoints"/>) - the
/// same table <c>EveryFactoryRouteIsBehindTheGateTests</c> reads - and walks every factory route through the team gate
/// (<see cref="TeamEndpointGate"/>) for a team of four:
///
/// <list type="bullet">
/// <item>every factory route states the row "open, run and talk to the team's factories";</item>
/// <item>the Owner, working in the team, is served on every one;</item>
/// <item>a Manager, a Developer and a contributor (Collaborator) are refused on every one.</item>
/// </list>
///
/// A route is a factory route when its path names a factory, the factory inbox, a trigger or a scheduled run. A new one
/// with no rule turns this red, because the gate refuses an undeclared route in a team for the Owner too.
/// </summary>
public sealed class EveryFactoryRouteHasATeamRuleTests
{
    private const string Owner = "sub-walk-factory-owner";

    /// <summary>The factory routes with no team rule on purpose, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoTeamRuleOnPurpose = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The administrator's switch, called with the admin service token: never a member's key, never in a team.
        ["GET /gateway/admin/factory-agents"] = "the administrator route",
        ["POST /gateway/admin/factory-agents"] = "the administrator route",
        // A contributor's own customer pages (Team implementation 4.1): the factories the team's Owner SENT to them, by
        // name. Not the Owner's alone and never will be - they exist for the contributor. Their rule is the row "answer
        // questions, send requests and read reports sent to them", and the endpoint answers only through the one check,
        // ContributorFactoryAccess.Check, which refuses anyone who is not a contributor of the team and any factory not
        // sent to them by name. They read nothing the factory pages do: no floor, no seat, no session, no journal - only
        // the factory's title and the sentences its agents wrote for a contributor.
        ["GET /teams/{teamId}/contributor/factories"] = "a contributor's own customer pages, gated by ContributorFactoryAccess",
        ["GET /teams/{teamId}/contributor/factories/{factory}"] = "a contributor's own customer pages, gated by ContributorFactoryAccess",
        // "Watch your factory" for a contributor (version one, pieces 4 and 6): the watch view, and the person's own record
        // that they have seen the explainer card - both through the same check, ContributorFactoryAccess.
        ["GET /teams/{teamId}/contributor/factories/{factory}/watch"] = "a contributor's own customer pages, gated by ContributorFactoryAccess",
        ["POST /teams/{teamId}/contributor/factories/{factory}/watch/explainer-seen"] = "a contributor's own customer pages, gated by ContributorFactoryAccess",
    };

    private static bool IsFactoryRoute(string pattern) =>
        pattern.Contains("factor", StringComparison.OrdinalIgnoreCase)
        || pattern.Contains("/triggers", StringComparison.OrdinalIgnoreCase)
        || pattern.StartsWith("/cron/", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task EveryFactoryRoute_OnTheHostedGateway_IsTheOwnersInATeam_AndRefusedToEveryOtherRole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-factory-team-routes-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "factory-team-token",
            authEnabled: true,
            instancesDirectory: dir,
            workListsPath: Path.Combine(dir, "worklists", "worklists.json"),
            options: new GatewayHostOptions { Hosted = true });
        try
        {
            await gateway.StartAsync();
            var routes = gateway.MappedEndpoints.OfType<RouteEndpoint>()
                .Select(e => (Pattern: "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'),
                    Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>()))
                .Where(r => IsFactoryRoute(r.Pattern))
                .SelectMany(r => r.Methods.DefaultIfEmpty("ANY"), (r, m) => (Method: m, r.Pattern))
                .Where(r => !NoTeamRuleOnPurpose.ContainsKey($"{r.Method} {r.Pattern}"))
                .Distinct()
                .OrderBy(r => r.Pattern, StringComparer.Ordinal).ThenBy(r => r.Method, StringComparer.Ordinal)
                .ToList();

            // A walk that found nothing proves nothing: name routes of every family that must be there.
            var names = routes.Select(r => $"{r.Method} {r.Pattern}").ToHashSet(StringComparer.Ordinal);
            foreach (var expected in new[]
                     {
                         "GET /gateway/factories/{factory}/floor", "POST /gateway/factory-agents/factories/{factory}/seats/{seat}/talk",
                         "POST /gateway/factory-agents/factories/{factory}/pause", "POST /gateway/factories/{factory}/archive",
                         "POST /gateway/factory/{factory}/inbox", "GET /sessions/{sid}/factory-inbox", "GET /factory-memory/notes",
                         "GET /gateway/factories/{factory}/records/journal", "GET /gateway/factories/{factory}/definition",
                         "GET /gateway/factory-agents/reports", "GET /triggers", "GET /directors/{directorId}/triggers",
                         "POST /gateway/factories/new", "POST /factory-definitions/create",
                     })
                Assert.Contains(expected, names);

            // The team gate itself, over a throwaway database with a team of four (the host's own registry would need a
            // paid plan read to create one). It is the same gate class the host runs; only its team is made here.
            using var harness = new Data.GatewayDbTestHarness();
            var db = harness.Open();
            using var devices = new CcDirector.Gateway.Pairing.DeviceRegistry(db, harness.LegacyPath("devices.json"));
            var tenants = new CcDirector.Gateway.Tenancy.TenantRegistry(db);
            var teams = new TeamRegistry(db, tenants);
            var gate = new TeamEndpointGate(new TeamAccess(teams), teams, tenants,
                new CcDirector.Gateway.Tenancy.HostedTenantBoundary(new AsyncLocalTenantContext(), devices, hosted: false));
            var team = teams.CreateTeam(Owner, "Walk").Team!.TeamId;
            var teamTenant = new TenantId(team);
            var members = new Dictionary<TeamRole, string>
            {
                [TeamRole.Manager] = "sub-walk-factory-manager",
                [TeamRole.Developer] = "sub-walk-factory-developer",
                [TeamRole.Collaborator] = "sub-walk-factory-collaborator",
            };
            foreach (var (role, subject) in members)
                Assert.True(teams.AddMember(team, subject, role).IsDone);

            // A route under /teams/{teamId}/ names its team in the address (TeamFrom.RouteTeamId); give it this team, as
            // a request to it would. Every other factory route takes the team from the calling key's tenant.
            TeamGateVerdict Check(string method, string pattern, string subject) =>
                gate.Check(method, pattern, name => name == "teamId" ? team : null, teamTenant, () => subject, _ => TeamOwnership.Callers);

            var wrong = new List<string>();
            foreach (var (method, pattern) in routes)
            {
                if (TeamEndpointRules.Find(method, pattern)?.Action != TeamAction.OpenRunAndTalkToTeamFactories)
                    wrong.Add($"{method} {pattern}: states {TeamEndpointRules.Find(method, pattern)?.Action.ToString() ?? "no action"}");

                var owner = Check(method, pattern, Owner);
                if (owner.Outcome != TeamGateOutcome.Allowed)
                    wrong.Add($"{method} {pattern}: the Owner was {owner.Outcome} - {owner.Message}");

                foreach (var (role, subject) in members)
                {
                    var verdict = Check(method, pattern, subject);
                    if (verdict.Outcome != TeamGateOutcome.Refused)
                        wrong.Add($"{method} {pattern}: a {role} was {verdict.Outcome}");
                }
            }

            Assert.True(wrong.Count == 0, $"Of {routes.Count} factory routes walked:\n  " + string.Join("\n  ", wrong));
        }
        finally
        {
            await gateway.StopAsync();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }
}
