using System.Net;
using CcDirector.Core.Account;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Pairing;
using Xunit;
using CcDirector.Gateway.Tests.Data;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the two load-bearing guardrails of the sign-in enrollment path (issue #1069):
/// the AUTH gate (POST /devices/enroll-signed-in only mints for a proven-loopback caller when the
/// Gateway is signed in) and the ONE-KEY-PER-DEVICE rule (a device that already holds a key ends up with
/// one registry entry and exactly one key that validates). The second pins the guardrail against the
/// #1136 auto-mint key leak: enrolling the same device repeatedly must never MULTIPLY working keys.
/// </summary>
public sealed class SignedInEnrollmentTests
{
    private static readonly AccountIdentity Signed = new("person@example.com", "google");

    // ---- Auth gate (SignedInEnrollmentEndpoint.Evaluate) ---------------------------------------

    [Fact]
    public void Evaluate_LoopbackAndSignedIn_Allows()
    {
        Assert.Equal(EnrollGateDecision.Allow,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Loopback, isSignedIn: true, Signed));
        Assert.Equal(EnrollGateDecision.Allow,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.IPv6Loopback, isSignedIn: true, Signed));
    }

    [Fact]
    public void Evaluate_NonLoopbackCaller_IsRejectedAsNotSameMachine()
    {
        // A tailnet / LAN address must never reach the mint, even when the Gateway is signed in.
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Parse("100.86.144.11"), isSignedIn: true, Signed));
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Parse("192.168.1.50"), isSignedIn: true, Signed));
    }

    [Fact]
    public void Evaluate_UnknownCaller_IsRejected_NeverAssumedSameMachine()
    {
        // A null remote address is NOT treated as same-machine here (this mints a credential), unlike the
        // conservative sign-in routing default.
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(null, isSignedIn: true, Signed));
    }

    [Fact]
    public void Evaluate_LoopbackButNotSignedIn_IsRejected()
    {
        Assert.Equal(EnrollGateDecision.NotSignedIn,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Loopback, isSignedIn: false, identity: null));
    }

    [Fact]
    public void Evaluate_LoopbackSignedInButNoIdentity_IsRejected()
    {
        // Signed-in flag without a resolved identity (stale credential) must not mint - there is nothing
        // to bind the key to.
        Assert.Equal(EnrollGateDecision.NotSignedIn,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Loopback, isSignedIn: true, identity: null));
    }

    [Fact]
    public void Evaluate_SameMachineCheckOutranksSignedIn()
    {
        // A non-loopback caller is rejected as not-same-machine even if also not signed in - the transport
        // proof is checked first so a remote caller never learns the sign-in state.
        Assert.Equal(EnrollGateDecision.NotSameMachine,
            SignedInEnrollmentEndpoint.Evaluate(IPAddress.Parse("10.0.0.5"), isSignedIn: false, identity: null));
    }

    // ---- One entry, exactly one valid key (DeviceRegistry.RegisterIfAbsent) --------------------
    //
    // Before issue #1878 the registry stored the plaintext key and handed the SAME key back on a repeat
    // enrollment. It now stores only a hash and keeps NO plaintext at all (issue #1899), so it has nothing to
    // hand back and deliberately keeps nothing: a repeat enrollment ROTATES - a fresh key is issued and the
    // previous one is retired. The #1136 property these tests assert is unchanged: enrollment can never
    // ACCUMULATE credentials - one device identity, one registry entry, and exactly one key that validates at
    // any moment.

    [Fact]
    public void RegisterIfAbsent_SameDeviceTwice_LeavesOneEntryAndOneValidKey()
    {
        using var harness = new GatewayDbTestHarness();
        var registry = harness.OpenDevices();

        var first = registry.RegisterIfAbsent("device-1", "MACHINE_A", "windows", "workstation");
        var second = registry.RegisterIfAbsent("device-1", "MACHINE_A", "windows", "workstation");

        Assert.False(string.IsNullOrEmpty(first.DeviceKey));
        Assert.False(string.IsNullOrEmpty(second.DeviceKey));
        Assert.NotEqual(first.DeviceKey, second.DeviceKey); // a repeat enrollment rotates, never replays
        Assert.Equal(1, registry.Count); // exactly one device, no duplicates
        Assert.True(registry.IsValidDeviceKey(second.DeviceKey));
        Assert.False(registry.IsValidDeviceKey(first.DeviceKey), "the previous key must be retired, not left working alongside the new one");
    }

    [Fact]
    public void RegisterIfAbsent_SameDeviceTwice_RotatesAndNeverReplaysTheOldKey()
    {
        // The retry-safety property post-#1899: a repeat enrollment hands back a FRESH working key rather than
        // re-revealing the old one from a plaintext cache. The client writes whichever key it receives, so a
        // sequential retry always ends up holding a valid credential; no plaintext is retained to replay.
        using var harness = new GatewayDbTestHarness();
        var registry = harness.OpenDevices();

        var first = registry.RegisterIfAbsent("device-1", "MACHINE_A", "windows", "workstation");
        var second = registry.RegisterIfAbsent("device-1", "MACHINE_A", "windows", "workstation");

        Assert.NotEqual(first.DeviceKey, second.DeviceKey);
        Assert.True(registry.IsValidDeviceKey(second.DeviceKey), "the freshly returned key must authenticate");
        Assert.False(registry.IsValidDeviceKey(first.DeviceKey), "the superseded key must not keep working");
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void RegisterIfAbsent_ManyCalls_NeverAccumulateDevicesOrKeys()
    {
        // The poll loop is guarded to never call enroll repeatedly, but the server holds the line anyway:
        // even ten calls yield one device and one working key (the #1136 guardrail). Every call rotates.
        using var harness = new GatewayDbTestHarness();
        var registry = harness.OpenDevices();

        var issued = new List<string> { registry.RegisterIfAbsent("device-1", "MACHINE_A").DeviceKey };
        for (var i = 0; i < 10; i++)
            issued.Add(registry.RegisterIfAbsent("device-1", "MACHINE_A").DeviceKey);

        Assert.Equal(1, registry.Count);
        var live = issued.Where(registry.IsValidDeviceKey).ToList();
        Assert.Single(live);
        Assert.Equal(issued[^1], live[0]);
    }

    [Fact]
    public void RegisterIfAbsent_DistinctDevices_GetDistinctKeys()
    {
        using var harness = new GatewayDbTestHarness();
        var registry = harness.OpenDevices();

        var a = registry.RegisterIfAbsent("device-a", "MACHINE_A").DeviceKey;
        var b = registry.RegisterIfAbsent("device-b", "MACHINE_B").DeviceKey;

        Assert.NotEqual(a, b);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void RegisterIfAbsent_MintedKeyValidates()
    {
        using var harness = new GatewayDbTestHarness();
        var registry = harness.OpenDevices();

        var key = registry.RegisterIfAbsent("device-1", "MACHINE_A").DeviceKey;

        Assert.True(registry.IsValidDeviceKey(key));
    }

    [Fact]
    public void RegisterIfAbsent_BlankDeviceId_Throws()
    {
        using var harness = new GatewayDbTestHarness();
        var registry = harness.OpenDevices();

        Assert.Throws<ArgumentException>(() => registry.RegisterIfAbsent("  ", "MACHINE_A"));
    }

}
