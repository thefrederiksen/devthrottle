using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.UnitTests.Drain;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// Issue #3377: a Director that is connected to a Gateway AFTER it started must stamp the Gateway's
/// address onto its <see cref="SessionManager"/>, because that is the gate on the whole session-credential
/// block - with it null, <see cref="SessionManager"/> mints no session key and every session it starts is
/// launched with neither CC_GATEWAY_URL nor CC_GATEWAY_SESSION_KEY.
///
/// This is the ordinary first run, not an edge case. The Director opens before anyone has signed in, so its
/// start-up reads a configuration with no Gateway in it; the setup wizard signs in seconds later and the
/// host takes its replacement path. On devthrottle-mac-mini on 20 October 2026 that left every session on a
/// freshly installed machine reporting "CC_GATEWAY_URL is not set" on its first fleet command, while the
/// Director beside it was healthy and connected, and the Director's log held no SessionGatewayKeys Mint line
/// for any real session.
///
/// WHAT THESE TESTS DO NOT COVER: they prove the address is stamped, which is the gate. They do not watch a
/// key being minted and registered, because that needs a live tunnel to a real Gateway and this suite starts
/// no network service. The other half of the chain - that a stamped address plus a credential source puts
/// both variables into a session's environment - is already held by ApiModeSessionCreationTests.
///
/// The address used is port 0 on loopback, so the host's dial fails at once without a name lookup and
/// without leaving the machine. Nothing here asserts on whether that dial succeeded - the value is only
/// ever compared as a string. Port 0 rather than the discard port because a fixed non-zero port in a gate
/// test makes the test depend on the machine, which GateTestsDoNotDependOnTheMachineTests refuses.
/// </summary>
[Collection(CcDirector.Gateway.UnitTests.Restart.DirectorGatesCollection.Name)]
public sealed class SessionCredentialAfterLateEnrollTests
{
    private const string GatewayAddress = "http://127.0.0.1:0";

    [Fact]
    public async Task ReapplyGateway_AfterStartingWithNoGateway_StampsTheAddressOnTheSessionManager()
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            // The state the Director is really in when its window opens on a machine nobody has signed in on.
            Assert.True(string.IsNullOrEmpty(sessions.GatewayUrl));

            await host.ReapplyGatewayAsync(() => new GatewayConfig { Url = GatewayAddress });

            Assert.Equal(GatewayAddress, sessions.GatewayUrl);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReapplyGateway_WhenTheGatewayIsRemoved_ClearsTheAddressOnTheSessionManager()
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            await host.ReapplyGatewayAsync(() => new GatewayConfig { Url = GatewayAddress });
            Assert.Equal(GatewayAddress, sessions.GatewayUrl);

            // Disconnecting is the same path with a configuration that has no address. A stale address left
            // behind here would stamp sessions with a Gateway this Director is no longer attached to.
            await host.ReapplyGatewayAsync(() => new GatewayConfig());

            Assert.True(string.IsNullOrEmpty(sessions.GatewayUrl));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
