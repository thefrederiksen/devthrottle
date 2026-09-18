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
    /// <summary>The throwaway per-process Director instance-discovery directory the whole test
    /// assembly is pinned to (issue #322).</summary>
    internal static string InstancesDir { get; } =
        Path.Combine(Path.GetTempPath(), "cc-director-tests", "instances-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Init()
    {
        CcDirector.Gateway.Briefing.TurnEndWatcher.SweepEnabled = false;
        // The Message Load mission, slice 2: a test-spun host must not ring a fake Director on its own heartbeat.
        CcDirector.Gateway.GatewayHost.FleetDoorbellHeartbeatEnabled = false;
        // The same isolation for the Fleet Manager events reconcile: a host test decides when it runs.
        CcDirector.Gateway.Fleet.FleetManagerEventSweep.Enabled = false;
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");

        // Issue #322: pin the Director instance-discovery directory to a throwaway per-process temp
        // directory so no test can ever write an instance file into the REAL
        // %LOCALAPPDATA%\cc-director\config\director\instances\ directory. A test that spins a
        // ControlApiHost / InstanceRegistration WITHOUT passing an isolated instancesDirectory (e.g.
        // ChatEndpointTests) previously wrote a "1.0.0-test" instance file into the live directory; the
        // production Gateway's file watcher then discovered it, probed the dead ephemeral loopback port,
        // and painted a phantom amber "unreachable" Director in the real Cockpit until the sweeper
        // evicted it. Pinning just this directory by default (via the CC_DIRECTOR_INSTANCES_DIR override
        // that CcStorage.DirectorInstances honors) makes that pollution impossible even when a call site
        // forgets its per-instance override - the same "guard at the process level, not per call site"
        // approach already used for CC_GATEWAY_NO_TAILSCALE above. It is deliberately narrow: the whole
        // storage root is left alone, so tests that read other real storage (e.g. dictation session
        // logs) are unaffected. This runs before any test touches the path-caching statics
        // (DirectorRegistry.InstancesDirectory, InstanceRegistration.InstancesDirectory), so their first
        // resolution lands under the temp directory.
        Environment.SetEnvironmentVariable("CC_DIRECTOR_INSTANCES_DIR", InstancesDir);
        try { Directory.CreateDirectory(InstancesDir); } catch { /* first real use will surface a failure */ }
    }
}
