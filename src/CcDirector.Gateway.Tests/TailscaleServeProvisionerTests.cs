using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tailscale;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pure decision-logic tests for the Tailscale Serve provisioner (issue #179): which
/// Directors get a serve mapping, and what the self-healing reconcile asserts/sweeps.
/// No tailscale.exe and no network involved - these run keyless in CI.
/// </summary>
public class TailscaleServeProvisionerTests
{
    private const string LocalMachine = "MACHINE-A";

    private static DirectorDto Dto(string control, string machine = LocalMachine, string? tailnet = null) => new()
    {
        DirectorId = "d-test",
        ControlEndpoint = control,
        MachineName = machine,
        TailnetEndpoint = tailnet,
    };

    // ---------------------------------------------------------------- ShouldMap

    [Fact]
    public void ShouldMap_LocalLoopbackDirector_InFixedRange_Maps()
    {
        var ok = TailscaleServeProvisioner.ShouldMap(Dto("http://127.0.0.1:7884"), LocalMachine, out var port);

        Assert.True(ok);
        Assert.Equal(7884, port);
    }

    [Fact]
    public void ShouldMap_LocalMachineName_TailnetEndpoint_InFixedRange_Maps()
    {
        // A local Director that registered over HTTP advertising its tailnet URL (not loopback).
        var d = Dto("https://machine-a.tail0123.ts.net:7886", machine: "machine-a");

        var ok = TailscaleServeProvisioner.ShouldMap(d, LocalMachine, out var port);

        Assert.True(ok); // machine-name match is case-insensitive
        Assert.Equal(7886, port);
    }

    [Fact]
    public void ShouldMap_RemoteMachineDirector_DoesNotMap()
    {
        // The laptop's Director: mapping it here would proxy machine-a:7879 to a dead
        // local port. This was the orphan churn source in issue #179.
        var d = Dto("https://laptop-b.tail0123.ts.net:7879", machine: "LAPTOP-B");

        Assert.False(TailscaleServeProvisioner.ShouldMap(d, LocalMachine, out _));
    }

    [Fact]
    public void ShouldMap_LocalEphemeralPortDirector_DoesNotMap()
    {
        // Hosted-agent Directors register with ephemeral ports; they are reached through
        // the Gateway, never via a per-port serve mapping (issue #179 pile-up source).
        var d = Dto("http://127.0.0.1:50682");

        Assert.False(TailscaleServeProvisioner.ShouldMap(d, LocalMachine, out _));
    }

    [Fact]
    public void ShouldMap_NoUsablePort_DoesNotMap()
    {
        Assert.False(TailscaleServeProvisioner.ShouldMap(Dto("not-a-url", machine: ""), LocalMachine, out var port));
        Assert.Equal(-1, port);
    }

    // ------------------------------------------------- ComputeReconcileActions

    private const int GatewayPort = 7878;

    private static string StatusJson(params (int httpsPort, string proxy)[] entries)
    {
        var web = string.Join(",", entries.Select(e =>
            $"\"machine-a.tail0123.ts.net:{e.httpsPort}\": {{ \"Handlers\": {{ \"/\": {{ \"Proxy\": \"{e.proxy}\" }} }} }}"));
        return $"{{ \"TCP\": {{}}, \"Web\": {{ {web} }} }}";
    }

    [Fact]
    public void Reconcile_ConsistentTable_NoActions()
    {
        var json = StatusJson((443, "http://localhost:7878"), (7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.False(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_FrontDoorMissing_Asserts()
    {
        // The live incident: 443 vanished from the serve table while Director mappings survived.
        var json = StatusJson((7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.True(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_FrontDoorWrongBackend_Asserts()
    {
        var json = StatusJson((443, "http://localhost:7470"), (7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.True(a.AssertFrontDoor);
    }

    [Fact]
    public void Reconcile_LiveDirectorMappingMissing_ReAsserts()
    {
        // The live incident's second shape: a Director mapping vanished ("handler does not
        // exist" on our own removal four minutes after a successful map).
        var json = StatusJson((443, "http://localhost:7878"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7879, 7884]);

        Assert.Equal([7879, 7884], a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_OrphanedEphemeralMappings_RemovedOnAnyPort()
    {
        // The pile-up: provisioner-shaped mappings (same-port localhost proxy) far outside
        // the fixed Director range must be swept once no live Director claims them.
        var json = StatusJson(
            (443, "http://localhost:7878"),
            (7884, "http://localhost:7884"),
            (50682, "http://localhost:50682"),
            (61602, "http://127.0.0.1:61602"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.False(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Equal([50682, 61602], a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_ForeignMappings_NeverTouched()
    {
        // A mapping whose backend port differs from its HTTPS port was not created by the
        // provisioner (except 443, handled separately) - leave it alone.
        var json = StatusJson((443, "http://localhost:7878"), (8080, "http://localhost:3000"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, []);

        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_EmptyTable_AssertsFrontDoorAndAllDesired()
    {
        var a = TailscaleServeProvisioner.ComputeReconcileActions("{}", GatewayPort, [7884, 7886]);

        Assert.True(a.AssertFrontDoor);
        Assert.Equal([7884, 7886], a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_BlankStatus_AssertsFrontDoor()
    {
        var a = TailscaleServeProvisioner.ComputeReconcileActions("", GatewayPort, []);

        Assert.True(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }
}
