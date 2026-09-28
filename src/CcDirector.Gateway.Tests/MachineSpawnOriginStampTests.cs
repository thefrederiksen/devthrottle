using System.Net.Http.Json;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The GATEWAY-AUTHORITATIVE half of session origin (devthrottle_internal issue #982), at
/// <c>POST /machines/{machine}/sessions</c> - the one spawn route reachable from outside the owner's own
/// machines, and therefore the one where a client's claim about its own origin cannot be the record.
///
/// Two rules, and the second matters more than the first:
///  - a caller holding a verified PHONE or BROWSER device key is a person, and is recorded as one
///    whatever the request body said;
///  - EVERY other caller is left exactly as it arrived. A remote agent spawn reaches this route relayed
///    by its own Director over the tunnel, carrying that Director's "workstation" key; the Director
///    already stamped the truth on its loopback floor, and overwriting here would erase agent lineage on
///    precisely the cross-machine spawns that make lineage worth having.
///
/// Boots the real machine routes with a stub spawner that captures the create request, and a middleware
/// that stamps the device type exactly as <see cref="AuthMiddleware"/> does after verifying a key.
/// </summary>
public sealed class MachineSpawnOriginStampTests : IDisposable
{
    private readonly string _dir;

    public MachineSpawnOriginStampTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-spawn-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>A resolver that always finds a Director, so the relay reaches the create step.</summary>
    private sealed class AlwaysFound : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
            => Task.FromResult(new DirectorTargetResult("dir-1", null));
    }

    private async Task<(WebApplication app, HttpClient http, Func<NewSessionRequest?> captured)> StartAsync(string? deviceType)
    {
        NewSessionRequest? seen = null;
        var spawner = new MachineSessionSpawner(new AlwaysFound(), (directorId, req, ct) =>
        {
            seen = req;
            return Task.FromResult<(bool, SessionDto?, string?)>(
                (true, new SessionDto { SessionId = Guid.NewGuid().ToString() }, null));
        });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        // Stand in for AuthMiddleware having verified a key: it stashes the device type under this key,
        // and that stash is the ONLY thing the stamper reads.
        app.Use(async (ctx, next) =>
        {
            if (deviceType is not null) ctx.Items[AuthMiddleware.DeviceTypeItemKey] = deviceType;
            await next();
        });

        // Self-host-only harness: this host never runs hosted, so there is no boundary to pass. The
        // parameter is required (finding CR-7), so the absence is stated rather than defaulted.
        MachineEndpoints.Map(app, new LauncherRegistry(), spawner, boundary: null, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, () => seen);
    }

    private static NewSessionRequest AgentSpawnBody(string parentSessionId) => new()
    {
        RepoPath = @"D:\repos\devthrottle",
        Agent = "ClaudeCode",
        Origin = SessionOriginKinds.Agent,
        OriginSurface = SessionOriginSurfaces.Cli,
        ParentSessionId = parentSessionId,
    };

    [Theory]
    [InlineData("phone", SessionOriginSurfaces.Phone)]
    [InlineData("browser", SessionOriginSurfaces.Cockpit)]
    public async Task A_signed_in_device_is_recorded_as_a_person_whatever_the_body_claimed(string deviceType, string expectedSurface)
    {
        var (app, http, captured) = await StartAsync(deviceType);
        try
        {
            // The body lies: it claims an agent on the command line. The verified key says otherwise.
            var resp = await http.PostAsJsonAsync("/machines/SOREN_NORTH/sessions", AgentSpawnBody(Guid.NewGuid().ToString()));
            resp.EnsureSuccessStatusCode();

            var req = captured();
            Assert.NotNull(req);
            Assert.Equal(SessionOriginKinds.Human, req!.Origin);
            Assert.Equal(expectedSurface, req.OriginSurface);
            // A phone is nobody's child: the claimed parent goes with the claimed origin.
            Assert.Null(req.ParentSessionId);
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("workstation")]   // what a Director enrols as - the relayed-agent-spawn case
    [InlineData("gateway")]
    [InlineData(null)]            // no device key resolved at all
    public async Task A_relayed_spawn_keeps_the_origin_its_director_already_stamped(string? deviceType)
    {
        // THE NEGATIVE CONTROL, and the reason the stamper returns early instead of defaulting. If any
        // of these overwrote, every cross-machine agent spawn - one session driving work on another
        // computer, the exact shape the lineage tree is for - would be recorded as a human's doing.
        var parent = Guid.NewGuid().ToString();
        var (app, http, captured) = await StartAsync(deviceType);
        try
        {
            var resp = await http.PostAsJsonAsync("/machines/SOREN_NORTH/sessions", AgentSpawnBody(parent));
            resp.EnsureSuccessStatusCode();

            var req = captured();
            Assert.NotNull(req);
            Assert.Equal(SessionOriginKinds.Agent, req!.Origin);
            Assert.Equal(SessionOriginSurfaces.Cli, req.OriginSurface);
            Assert.Equal(parent, req.ParentSessionId);
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }
    }
}
