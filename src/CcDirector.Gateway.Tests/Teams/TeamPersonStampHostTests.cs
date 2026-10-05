using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Teams;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams;

/// <summary>
/// The person stamp through the PRODUCTION wiring (devthrottle_internal#2305, review P2). The unit tests prove the
/// recorder and the prompt endpoint each do the right thing with the delegates they are given; this proves the real
/// <see cref="GatewayHost"/> gives them the right ones. A wire-level push is not possible here - a team key is answered
/// 402 by the hosted access lease before it reaches a route - so the test drives the host's own recorder and the host's
/// own caller lookup, which are exactly the objects the Director hub and <c>POST /prompts</c> are built with.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TeamPersonStampHostTests
{
    private const string Owner = "sub-owner";
    private const string Alice = "sub-alice";
    private const string Solo = "sub-solo";

    [Fact]
    public async Task GatewayHost_TeamsReleased_ATeamSessionCarriesTheKeysPerson_AndAPersonalOneCarriesNone()
    {
        await WithHostedHost(gateway =>
        {
            var team = new TenantId(gateway.TeamRegistry.CreateTeam(Owner, "Acme").Team!.TeamId);
            Assert.True(gateway.TeamRegistry.AddMember(team.Value, Alice, TeamRole.Developer).IsDone);
            Connect(gateway, team, Alice, "director-alice");

            var personal = gateway.TenantRegistry.MintOrLookupBySubject(Solo, "solo@example.com");
            Connect(gateway, personal, Solo, "director-solo");

            Observe(gateway, team, "director-alice", "session-alice");
            Observe(gateway, personal, "director-solo", "session-solo");

            Assert.Equal(Alice, PersonOn(gateway, "session-alice"));
            Assert.Null(PersonOn(gateway, "session-solo"));
        });
    }

    [Fact]
    public async Task GatewayHost_TeamsReleased_APromptPushedIntoATeamIsStampedWithTheKeysPerson()
    {
        await WithHostedHost(gateway =>
        {
            var team = new TenantId(gateway.TeamRegistry.CreateTeam(Owner, "Acme").Team!.TeamId);
            Assert.True(gateway.TeamRegistry.AddMember(team.Value, Alice, TeamRole.Developer).IsDone);
            var aliceKey = Connect(gateway, team, Alice, "director-alice");

            var ctx = new DefaultHttpContext();
            ctx.Items[Util.AuthMiddleware.AuthenticatedDeviceItemKey] = aliceKey;

            Assert.Equal(Alice, gateway.TeamPromptCaller(ctx, team));
        });
    }

    /// <summary>A member's Director, enrolled into the tenant on its own key and connected to the host.</summary>
    private static Pairing.DeviceCredentialIdentity Connect(GatewayHost gateway, TenantId tenant, string subject, string directorId)
    {
        var deviceId = tenant.Value + "|" + directorId;
        var key = gateway.Devices.RegisterForTenant(tenant, subject, deviceId, "M").DeviceKey;
        var identity = gateway.Devices.ResolveCredential(key).Identity!;
        gateway.Registry.RegisterFromStream(directorId, "M", "u", "1.0", 1, DateTime.UtcNow, tenant, directorId, "device:" + deviceId);
        return identity;
    }

    private static void Observe(GatewayHost gateway, TenantId tenant, string directorId, string sessionId)
    {
        using var scope = gateway.TenantBoundaryForTests.EnterScope(tenant);
        gateway.SessionHistoryRecorderForTests.Observe(tenant, directorId, new SessionDto
        {
            SessionId = sessionId, Number = 1, RepoPath = @"D:\repos\acme", RepoName = "acme/acme", Agent = "ClaudeCode",
            MachineName = "", CreatedAt = DateTime.UtcNow.AddMinutes(-5), ActivityState = "Working", Status = "Running",
        });
    }

    private static string? PersonOn(GatewayHost gateway, string sessionId)
    {
        using var ctx = gateway.GatewayDatabaseForTests.CreateUnscopedContext();
        return ctx.SessionHistory.Single(e => e.SessionId == sessionId).PersonSubject;
    }

    private static async Task WithHostedHost(Action<GatewayHost> act)
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-person-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var prior = new[] { "CC_GATEWAY_HOSTED", "CC_DIRECTOR_ROOT", "CC_GATEWAY_NO_TAILSCALE" };
        var priorValues = Array.ConvertAll(prior, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
        GatewayHost? gateway = null;
        try
        {
            gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
                instancesDirectory: root, keyVaultPath: Path.Combine(root, "test-vault.json"),
                workListsPath: Path.Combine(root, "worklists", "worklists.json"),
                snoozePath: Path.Combine(root, "snooze", "snooze.json"),
                streamMode: true, teamsReleased: true);
            await gateway.StartAsync();
            act(gateway);
        }
        finally
        {
            if (gateway is not null)
                await gateway.StopAsync();
            for (var i = 0; i < prior.Length; i++)
                Environment.SetEnvironmentVariable(prior[i], priorValues[i]);
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { /* a file still held by the stopped host; the folder is in temp */ }
        }
    }
}
