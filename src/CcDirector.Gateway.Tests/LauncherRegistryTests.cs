using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="LauncherRegistry"/>: upsert, heartbeat, sweep, listing.
/// Issue #331.
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
        var req = MakeReq("MACHINE-A", 7900);

        reg.Upsert(req);

        var dto = reg.Get("MACHINE-A");
        Assert.NotNull(dto);
        Assert.Equal("MACHINE-A", dto.MachineName);
        Assert.Equal(7900, dto.Port);
    }

    [Fact]
    public void Upsert_IsCaseInsensitive()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("machine-b", 7901));

        Assert.NotNull(reg.Get("MACHINE-B"));
        Assert.NotNull(reg.Get("Machine-B"));
    }

    [Fact]
    public void Upsert_UpdatesExistingEntry()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("MACHINE-C", 7902, version: "1.0.0"));
        reg.Upsert(MakeReq("MACHINE-C", 7902, version: "1.0.1"));

        var dto = reg.Get("MACHINE-C");
        Assert.NotNull(dto);
        Assert.Equal("1.0.1", dto!.Version);
    }

    [Fact]
    public void Upsert_ReturnsDto_TokenNotInDto()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-D", 7903);
        req.Token = "SECRET-TOKEN";

        var dto = reg.Upsert(req);

        // Token MUST NOT be in the public DTO.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("SECRET-TOKEN", json);
    }

    // -------------------------------------------------------------------------
    // Token retrieval (for relay calls)
    // -------------------------------------------------------------------------

    [Fact]
    public void GetToken_ReturnsStoredToken()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-E", 7904);
        req.Token = "my-relay-token";
        reg.Upsert(req);

        Assert.Equal("my-relay-token", reg.GetToken("MACHINE-E"));
    }

    [Fact]
    public void GetToken_UnknownMachine_ReturnsNull()
    {
        var reg = new LauncherRegistry();
        Assert.Null(reg.GetToken("NOBODY"));
    }

    // -------------------------------------------------------------------------
    // Heartbeat
    // -------------------------------------------------------------------------

    [Fact]
    public void Heartbeat_KnownMachine_ReturnsTrue()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("MACHINE-F", 7905));

        Assert.True(reg.Heartbeat("MACHINE-F"));
    }

    [Fact]
    public void Heartbeat_UnknownMachine_ReturnsFalse()
    {
        var reg = new LauncherRegistry();
        Assert.False(reg.Heartbeat("NOBODY"));
    }

    // -------------------------------------------------------------------------
    // Remove
    // -------------------------------------------------------------------------

    [Fact]
    public void Remove_ExistingEntry_IsGone()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("MACHINE-G", 7906));

        reg.Remove("MACHINE-G");

        Assert.Null(reg.Get("MACHINE-G"));
    }

    [Fact]
    public void Remove_NonExistentEntry_DoesNotThrow()
    {
        var reg = new LauncherRegistry();
        var ex = Record.Exception(() => reg.Remove("NOBODY"));
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // List
    // -------------------------------------------------------------------------

    [Fact]
    public void ListLaunchers_ReturnsAllEntries()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("MACHINE-H", 7907));
        reg.Upsert(MakeReq("MACHINE-I", 7908));

        var list = reg.ListLaunchers();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, l => l.MachineName == "MACHINE-H");
        Assert.Contains(list, l => l.MachineName == "MACHINE-I");
    }

    [Fact]
    public void ListLaunchers_EmptyWhenNoneRegistered()
    {
        var reg = new LauncherRegistry();
        Assert.Empty(reg.ListLaunchers());
    }

    // -------------------------------------------------------------------------
    // NetworkAddress (AC2 - cross-machine relay address)
    // -------------------------------------------------------------------------

    [Fact]
    public void Upsert_WithNetworkAddress_StoredInDto()
    {
        var reg = new LauncherRegistry();
        var req = MakeReq("MACHINE-NA", 7920, networkAddress: "sorenlaptop.taildb08ed.ts.net");

        reg.Upsert(req);

        var dto = reg.Get("MACHINE-NA");
        Assert.NotNull(dto);
        Assert.Equal("sorenlaptop.taildb08ed.ts.net", dto!.NetworkAddress);
    }

    [Fact]
    public void GetNetworkAddress_ReturnsStoredAddress()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("MACHINE-NB", 7921, networkAddress: "soren-north.tailnet.ts.net"));

        Assert.Equal("soren-north.tailnet.ts.net", reg.GetNetworkAddress("MACHINE-NB"));
    }

    [Fact]
    public void GetNetworkAddress_EmptyWhenNotSet()
    {
        var reg = new LauncherRegistry();
        reg.Upsert(MakeReq("MACHINE-NC", 7922)); // no networkAddress

        // Empty string = co-located, loopback applies.
        Assert.Equal("", reg.GetNetworkAddress("MACHINE-NC"));
    }

    [Fact]
    public void GetNetworkAddress_NullForUnknownMachine()
    {
        var reg = new LauncherRegistry();
        Assert.Null(reg.GetNetworkAddress("NOBODY-NA"));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static LauncherRegistrationRequest MakeReq(
        string machine, int port, string version = "1.0.0", string networkAddress = "") =>
        new()
        {
            MachineName = machine,
            Port = port,
            NetworkAddress = networkAddress,
            Token = "tok-" + machine,
            Pid = 9999,
            Version = version,
            StartedAt = DateTime.UtcNow,
        };
}
