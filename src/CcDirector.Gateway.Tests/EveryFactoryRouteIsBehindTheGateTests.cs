using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// FACTORIES NEED PRO, ON THE REAL ROUTE TABLE (review of pull request #4337, finding 1). The mission promises that
/// every factory route refuses an account without factories. A test that maps the routes itself proves only its own
/// wiring, so this reads the FINALISED route table of a real hosted <see cref="GatewayHost"/>
/// (<see cref="GatewayHost.MappedEndpoints"/>) and asserts that every factory route carries the gate's marker - except
/// the few written below, each with why it is outside.
///
/// A route is a factory route when its path names a factory or a trigger. A new one mapped on the bare app instead of
/// a gate's group turns this red until it is gated or ruled on here.
/// </summary>
public sealed class EveryFactoryRouteIsBehindTheGateTests
{
    /// <summary>The factory routes mapped OUTSIDE the gate on purpose, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> OutsideOnPurpose = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // How the Cockpit learns the answer: it must answer when the area is off.
        ["GET /gateway/factory-agents/switch"] = "the switch question itself",
        // The administrator's own switch, behind the admin service token.
        ["GET /gateway/admin/factory-agents"] = "the administrator route",
        ["POST /gateway/admin/factory-agents"] = "the administrator route",
        // The owner's own clicks, mapped outside so a refusal can carry a sentence. Each asks the SAME decision itself
        // first (FactoryAgentsGate.AnswerFor) - proven in FactoryTalkEndpointTests and FactoryOwnerActionEndpointTests.
        ["POST /gateway/factories/{factory}/archive"] = "an owner action that asks the decision itself",
        ["POST /gateway/factories/{factory}/restore"] = "an owner action that asks the decision itself",
        ["POST /gateway/factories/{factory}/delete"] = "an owner action that asks the decision itself",
        ["POST /gateway/factories/{factory}/waiting/handled-older"] = "an owner action that asks the decision itself",
        ["POST /gateway/factory-agents/factories/{factory}/problems/{runId}/handle"] = "a talk that asks the decision itself",
        ["POST /gateway/factory-agents/factories/{factory}/seats/{seat}/talk"] = "a talk that asks the decision itself",
        ["POST /gateway/factory-agents/factory-manager/talk"] = "the Factory Manager's talk, which asks the decision itself",
    };

    private static async Task<IReadOnlyList<(string Route, bool Gated)>> FactoryRoutesAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-factory-gate-routes-" + Guid.NewGuid().ToString("N"));
        var gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "factory-gate-token",
            authEnabled: true,
            instancesDirectory: dir,
            workListsPath: Path.Combine(dir, "worklists", "worklists.json"),
            options: new GatewayHostOptions { Hosted = true });
        try
        {
            await gateway.StartAsync();
            return gateway.MappedEndpoints.OfType<RouteEndpoint>()
                .Select(e => (Pattern: "/" + (e.RoutePattern.RawText ?? "").TrimStart('/'),
                    Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>(),
                    Gated: e.Metadata.GetMetadata<FactoryAgentsGate.GatedMarker>() is not null))
                .Where(r => r.Pattern.Contains("factor", StringComparison.OrdinalIgnoreCase)
                            || r.Pattern.Contains("/triggers", StringComparison.OrdinalIgnoreCase))
                .SelectMany(r => r.Methods.DefaultIfEmpty("ANY"), (r, m) => ($"{m} {r.Pattern}", r.Gated))
                .Distinct()
                .OrderBy(r => r.Item1, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            await gateway.StopAsync();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task EveryFactoryRoute_OnTheHostedGateway_IsBehindTheGate_ExceptTheRuledFew()
    {
        var routes = await FactoryRoutesAsync();

        // A table that found no factory routes proves nothing: name some that must be there, gated.
        foreach (var expected in new[]
                 {
                     "GET /factory-memory/notes", "PUT /factory-definitions/{factory}/draft", "POST /factory-definitions/create",
                     "GET /factory-definitions/{factory}/versions/{version}/kit", "POST /factory-records/{factory}/journal",
                     "GET /factory-briefs/{factory}/{seat}", "PUT /gateway/factory-email", "GET /directors/{directorId}/triggers",
                     "GET /gateway/factory/activity",
                 })
            Assert.Contains((expected, true), routes);

        var ungated = routes.Where(r => !r.Gated).Select(r => r.Route).ToList();
        var unruled = ungated.Where(r => !OutsideOnPurpose.ContainsKey(r)).ToList();
        Assert.True(unruled.Count == 0,
            "factory routes outside the gate with no ruling here:\n  " + string.Join("\n  ", unruled));
    }
}
