using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="LauncherRegistry"/>: upsert, heartbeat, sweep, listing.
/// Issue #331. Phase 6 of the remove-the-network-port mission made the registry
/// presence-and-identity only: no port, no token, no network address - there is no launcher REST
/// interface left to dial, so there is nothing for the registry to hold a dial-back for.
/// </summary>
public sealed class LauncherRegistryTests
{
    // -------------------------------------------------------------------------
    // Upsert
    // -------------------------------------------------------------------------

    [Fact]
    public void Upsert_AddsEntry_CanBeRetrieved()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-A", pid: 4211);

        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, req);

        var dto = reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-A");
        Assert.NotNull(dto);
        Assert.Equal("MACHINE-A", dto.MachineName);
        Assert.Equal(4211, dto.Pid);
    }

    [Fact]
    public void Upsert_IsCaseInsensitive()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("machine-b"));

        Assert.NotNull(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-B"));
        Assert.NotNull(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "Machine-B"));
    }

    [Fact]
    public void Upsert_UpdatesExistingEntry()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-C", version: "1.0.0"));
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-C", version: "1.0.1"));

        var dto = reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-C");
        Assert.NotNull(dto);
        Assert.Equal("1.0.1", dto!.Version);
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    [Fact]
    public void Heartbeat_KnownMachine_ReturnsTrue()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-F"));

        Assert.True(reg.Heartbeat(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-F"));
    }

    [Fact]
    public void Heartbeat_UnknownMachine_ReturnsFalse()
    {
        var reg = new LauncherRegistry();
        Assert.False(reg.Heartbeat(CcDirector.Core.Tenancy.TenantId.Local, "NOBODY"));
    }

    // -------------------------------------------------------------------------
    // Remove
    // -------------------------------------------------------------------------

    [Fact]
    public void Remove_ExistingEntry_IsGone()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-G"));

        reg.Remove(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-G");

        Assert.Null(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-G"));
    }

    [Fact]
    public void Remove_NonExistentEntry_DoesNotThrow()
    {
        var reg = new LauncherRegistry();
        var ex = Record.Exception(() => reg.Remove(CcDirector.Core.Tenancy.TenantId.Local, "NOBODY"));
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    [Fact]
    public void ListLaunchers_ReturnsAllEntries()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-H"));
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-I"));

        var list = reg.ListLaunchers(CcDirector.Core.Tenancy.TenantId.Local);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, l => l.MachineName == "MACHINE-H");
        Assert.Contains(list, l => l.MachineName == "MACHINE-I");
    }

    [Fact]
    public void ListLaunchers_EmptyWhenNoneRegistered()
    {
        var reg = new LauncherRegistry();
        Assert.Empty(reg.ListLaunchers(CcDirector.Core.Tenancy.TenantId.Local));
    }

    // -------------------------------------------------------------------------
    // The dial-back surface is GONE - pinned so it cannot quietly return.
    // -------------------------------------------------------------------------

    // A registry row must carry nothing a future caller could dial: no port, no token, no address.
    // This is the phase 6 shape assertion at the DTO level - the wire twin of the listener guard.
    [Fact]
    public void LauncherDto_CarriesNoDialBackSurface()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("MACHINE-J", pid: 77, version: "2.0.0"));

        var json = System.Text.Json.JsonSerializer.Serialize(
            reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-J"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.DoesNotContain("port", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("networkAddress", json, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static LauncherRegistrationRequest MakeReq(
        string machine, int pid = 9999, string version = "1.0.0") =>
        new()
        {
            MachineName = machine,
            Pid = pid,
            Version = version,
            StartedAt = DateTime.UtcNow,
        };

    // Issue #4423: the new launcher process registers BEFORE it opens its stream, so the old stream's close must
    // survive that registration - it is the evidence that "heartbeating, no stream" means restarting.
    [Fact]
    public void Upsert_AfterNoteStreamClosed_KeepsTheClose()
    {
        var reg = new LauncherRegistry();
        var closedAt = new DateTime(2026, 10, 24, 19, 31, 0, DateTimeKind.Utc);
        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("STUDIO-MAC", pid: 101));
        reg.NoteStreamClosed(CcDirector.Core.Tenancy.TenantId.Local, "studio-mac", closedAt);

        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("STUDIO-MAC", pid: 202));

        var dto = reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "STUDIO-MAC");
        Assert.Equal(202, dto!.Pid);
        Assert.Equal(closedAt, dto.StreamClosedAt);
    }

    // Review finding 1: a launcher that quits cleanly closes its stream and then DELETEs its registration, and the new
    // process registers before it opens its own stream. The close must survive the DELETE.
    [Fact]
    public void Upsert_AfterTheStreamClosedAndTheRowWasRemoved_StillCarriesTheClose()
    {
        var tenant = CcDirector.Core.Tenancy.TenantId.Local;
        var reg = new LauncherRegistry();
        var closedAt = new DateTime(2026, 10, 24, 19, 31, 0, DateTimeKind.Utc);
        reg.Upsert(tenant, MakeReq("studio-mac", pid: 101, version: "2.28.0"));
        reg.NoteStreamClosed(tenant, "studio-mac", closedAt);
        reg.Remove(tenant, "studio-mac");

        reg.Upsert(tenant, MakeReq("studio-mac", pid: 202, version: "2.30.0"));

        Assert.Equal(closedAt, reg.Get(tenant, "studio-mac")!.StreamClosedAt);
    }

    [Fact]
    public void NoteStreamClosed_BeforeTheMachineRegisters_IsCarriedByItsRegistration()
    {
        var tenant = CcDirector.Core.Tenancy.TenantId.Local;
        var reg = new LauncherRegistry();
        var closedAt = new DateTime(2026, 10, 24, 19, 31, 0, DateTimeKind.Utc);

        reg.NoteStreamClosed(tenant, "studio-mac", closedAt);

        Assert.Null(reg.Get(tenant, "studio-mac"));
        reg.Upsert(tenant, MakeReq("STUDIO-MAC", pid: 202));
        Assert.Equal(closedAt, reg.Get(tenant, "studio-mac")!.StreamClosedAt);
    }

    // Review round 2: the close is kept for good, so it is usually old. A launcher starting this morning after its
    // machine was off overnight is reconnecting for the moment until its stream opens - not "not taking commands".
    [Fact]
    public void Classify_LauncherStartingAfterALongStop_IsReconnectingNotStreamDown()
    {
        var tenant = CcDirector.Core.Tenancy.TenantId.Local;
        var reg = new LauncherRegistry();
        var lastNight = DateTime.UtcNow.AddHours(-10);
        reg.Upsert(tenant, MakeReq("studio-mac", pid: 101, version: "2.30.0"));
        reg.NoteStreamClosed(tenant, "studio-mac", lastNight);
        reg.Remove(tenant, "studio-mac");
        var thisMorning = DateTime.UtcNow;
        reg.Upsert(tenant, MakeReq("studio-mac", pid: 202, version: "2.30.0"));

        var reach = CcDirector.Gateway.Api.LauncherReachability.Classify(reg.Get(tenant, "studio-mac"), streamConnected: false, thisMorning.AddSeconds(2));

        Assert.Equal(LauncherReach.Reconnecting, reach);
    }

    [Fact]
    public void Classify_RegisteredLongAgoAndStreamLongDown_IsStreamDown()
    {
        var registered = new LauncherDto
        {
            MachineName = "studio-mac",
            RegisteredAt = DateTime.UtcNow.AddHours(-3),
            LastSeenAt = DateTime.UtcNow.AddSeconds(-5),
            StreamClosedAt = DateTime.UtcNow.AddHours(-1),
        };

        var reach = CcDirector.Gateway.Api.LauncherReachability.Classify(registered, streamConnected: false, DateTime.UtcNow);

        Assert.Equal(LauncherReach.StreamDown, reach);
    }

    [Fact]
    public void Upsert_MachineThatNeverStreamed_CarriesNoClose()
    {
        var reg = new LauncherRegistry();

        reg.Upsert(CcDirector.Core.Tenancy.TenantId.Local, MakeReq("old-box"));

        Assert.Null(reg.Get(CcDirector.Core.Tenancy.TenantId.Local, "old-box")!.StreamClosedAt);
    }
}
