using System.Runtime.CompilerServices;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Assembly-wide test environment. Issue #549 retired the always-on turn-brief pipeline, but
/// the TurnEndWatcher stays (now voice-only) and runs in every test-spun GatewayHost. Its
/// Director-polling sweep is turned OFF here via the TurnEndWatcher.SweepEnabled test seam so a
/// test host never polls its fake Directors on the 15s cadence and disturbs request-count
/// assertions - the same isolation the retired CC_TURNBRIEFS=0 flag used to provide. The
/// push-fed Observe path (the watcher's boundary detection) is unaffected and is tested
/// directly in GatewayTurnBriefTests / TurnEndWatcherVoiceRefreshTests.
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
        CcDirector.Gateway.Briefing.TurnEndWatcher.SweepEnabled = false;
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
    }
}
