using System.Runtime.CompilerServices;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Assembly-wide test environment. The turn-brief pipeline (issue #185) is disabled via
/// its kill switch for every test-spun GatewayHost: the TurnEndWatcher would otherwise
/// poll each test's fake Directors on its own 2s cadence and disturb request-count
/// assertions. The brief pipeline itself is tested directly through the agent's seam
/// constructor (GatewayTurnBriefTests), never through a live host poll.
///
/// Tailscale serve provisioning is disabled the same way (issues #179/#197/#200): every
/// test-spawned GatewayHost runs the REAL TailscaleServeProvisioner, which asserts the
/// 443 front door at the test host's own EPHEMERAL port and sweeps the machine's live
/// Director mappings as orphans. On a dev machine with Tailscale installed, running
/// Gateway.Tests clobbered the production serve table every fixture (rogue 443 backends,
/// vanished Director ports) until the production Gateway's watch healed it - the
/// long-standing #179/#200 "mystery clobberer". CI never saw it (no tailscale.exe).
/// CC_GATEWAY_NO_TAILSCALE=1 is the product's own kill switch, honored by both the
/// Gateway provisioner and the Director self-provisioner. Provisioner lifecycle tests
/// opt back in per-instance via internal seams (TailscaleServeSelfProvisioner.Enabled)
/// with all CLI calls faked - the real serve table is never touched from this process.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("CC_TURNBRIEFS", "0");
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
    }
}
