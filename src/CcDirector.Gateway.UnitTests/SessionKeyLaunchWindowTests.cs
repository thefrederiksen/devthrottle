using System.Diagnostics;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LAUNCH WINDOW: the one gap phase 1b recorded rather than hid, fault-injected here in phase 2 at the
/// Architect's instruction.
///
/// WHAT THE GAP IS. The Director mints a session's Gateway key inside the environment build and sends the
/// registration up the tunnel WITHOUT AWAITING IT - session creation must never block on the network. So in
/// principle a sufficiently slow Gateway and a sufficiently fast agent produce one refused first command:
/// the agent presents a key the Gateway has not yet been told about. Phase 1b could not test this, because
/// nothing read the key yet. Phase 2 is the first consumer, so this is where it gets settled.
///
/// WHAT THESE TESTS ESTABLISH, and what they deliberately do not.
///
///  * The window is REAL, not theoretical. A key presented before its registration lands is refused - and
///    refused LOUDLY, with a 401 naming the credential, never silently downgraded to some other authority.
///    That refusal posture is the part worth having: it means the failure is visible and attributable.
///  * The window CLOSES on its own. The same key, unchanged, works the instant the registration is applied.
///    So this is a race, not a break - which is exactly why measuring it matters more than fearing it.
///  * The window's real WIDTH is not measured here, and cannot be: it is the difference between a hub
///    invoke on an already-open connection and the time an operating system takes to start a process plus
///    the time an agent takes to boot and issue its first command. The second is measured against a live
///    Director and Gateway, and the numbers are in PHASE-2-REPORT.md. What this file pins is the SHAPE -
///    that a refusal is possible, that it is loud, and that it is transient.
///
/// WHY THERE IS NO RETRY HERE, AND MUST NOT BE. The obvious "fix" is to have the command line retry a
/// refused call. That would reintroduce exactly what this mission removes: a second path, taken when the
/// first fails, which is a fallback wearing a different hat. It would also make a genuinely invalid key -
/// a revoked one, a reaped session's - indistinguishable from an early one, so every real refusal would be
/// re-tried before being reported. The registration is sent before the process is even launched; if that
/// ordering is ever not enough, the answer is to make the ordering stronger, never to paper over it.
/// </summary>
public sealed class SessionKeyLaunchWindowTests : IDisposable
{
    private const string SharedToken = "shared-machine-token";
    private static readonly TenantId Account = new("tenant-a");

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private SessionKeyRegistry Registry() => new(_harness.Open());

    private static AuthMiddleware.RequireToken Config(SessionKeyRegistry sessions)
        => new() { Hosted = false, Token = SharedToken, Devices = null, Sessions = sessions };

    private static HttpContext Request(string path, string bearer)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(bool Continued, int Status, string Body)> RunAsync(HttpContext ctx, AuthMiddleware.RequireToken cfg)
    {
        var continued = false;
        await AuthMiddleware.Run(ctx, cfg, () => { continued = true; return Task.CompletedTask; });
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (continued, ctx.Response.StatusCode, body);
    }

    /// <summary>
    /// The launch as the Director actually performs it: the key is minted and handed to the session
    /// IMMEDIATELY, and the registration is a task that completes some time later. The delay stands in for
    /// a slow Gateway; nothing else about the ordering is altered.
    /// </summary>
    private (string Key, Guid Session, Task Registration) LaunchWithSlowGateway(SessionKeyRegistry registry, TimeSpan gatewayDelay)
    {
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();
        var hash = GatewaySessionKey.Hash(key);

        // Fire-and-forget, exactly as ControlApiHost does: the session gets its key now, whatever the
        // Gateway is doing.
        var registration = Task.Run(async () =>
        {
            await Task.Delay(gatewayDelay);
            registry.Register(Account, "director-1", session.ToString(), hash, DateTime.UtcNow.AddHours(12));
        });

        return (key, session, registration);
    }

    [Fact]
    public async Task A_key_presented_before_its_registration_lands_is_REFUSED()
    {
        // The fault injection: a Gateway slow enough that the agent unquestionably gets there first.
        // This is the window, made reachable on purpose so its behaviour can be looked at rather than
        // reasoned about.
        var registry = Registry();
        var (key, _, registration) = LaunchWithSlowGateway(registry, TimeSpan.FromSeconds(30));

        var (continued, status, _) = await RunAsync(Request("/sessions", key), Config(registry));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);

        // The registration is left running; the harness disposes underneath it. Nothing here waits 30
        // seconds - the point was made the moment the call was refused.
        Assert.False(registration.IsCompleted);
    }

    [Fact]
    public async Task The_refusal_is_LOUD_and_never_a_quiet_downgrade_to_the_machine_token()
    {
        // The failure mode that would be genuinely dangerous is not the refusal - it is a refusal that
        // fell through to some other authority and succeeded. Then the launch window would be invisible
        // AND every session would silently hold more than its own key.
        var registry = Registry();
        var (key, _, _) = LaunchWithSlowGateway(registry, TimeSpan.FromSeconds(30));

        var (continued, status, body) = await RunAsync(Request("/sessions", key), Config(registry));

        Assert.False(continued);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.DoesNotContain(SharedToken, body);
    }

    [Fact]
    public async Task The_SAME_key_works_the_moment_the_registration_lands()
    {
        // A race, not a break. Nothing is re-minted and nothing is re-sent by the caller: the identical
        // credential the session was given at launch becomes valid when the Gateway catches up. This is
        // what makes the window survivable, and it is also why a retry in the command line would be
        // solving the problem in the wrong place.
        var registry = Registry();
        var (key, _, registration) = LaunchWithSlowGateway(registry, TimeSpan.FromMilliseconds(50));

        var (earlyContinued, _, _) = await RunAsync(Request("/sessions", key), Config(registry));

        await registration;
        var (lateContinued, _, _) = await RunAsync(Request("/sessions", key), Config(registry));

        Assert.False(earlyContinued);   // before
        Assert.True(lateContinued);     // after, same key
    }

    [Fact]
    public async Task With_no_injected_delay_the_registration_is_already_there()
    {
        // The control arm, and the reason the window is narrow in practice: with nothing slowing the
        // Gateway down, registering is a database write measured in milliseconds, while the other side of
        // the race is an operating system starting a process and an agent booting. This test does not
        // claim the window is unreachable - it establishes the order of magnitude that makes the live
        // measurement in PHASE-2-REPORT.md worth trusting.
        var registry = Registry();
        var session = Guid.NewGuid();
        var key = GatewaySessionKey.Mint();

        var sw = Stopwatch.StartNew();
        registry.Register(Account, "director-1", session.ToString(), GatewaySessionKey.Hash(key),
            DateTime.UtcNow.AddHours(12));
        sw.Stop();

        var (continued, _, _) = await RunAsync(Request("/sessions", key), Config(registry));

        Assert.True(continued);
        // Deliberately generous - this is a shape assertion, not a benchmark, and a loaded build agent
        // must not be able to turn a timing observation into a red test. A registration that took longer
        // than this is not slow, it is broken.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"registering a session key took {sw.ElapsedMilliseconds}ms, which is not a database write");
    }
}
