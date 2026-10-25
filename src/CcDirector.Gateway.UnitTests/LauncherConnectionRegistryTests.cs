using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="LauncherConnectionRegistry"/> (launcher-persistent-join). The launcher twin of
/// the connection-tracking half of <see cref="PushedSessionStore"/>: a thread-safe machine-name to
/// connection-id map whose one delicate behaviour is that a superseded connection's LATE disconnect must not
/// wipe a newer connection registered for the same machine (atomic compare-remove).
/// </summary>
public sealed class LauncherConnectionRegistryTests
{
    private static LauncherConnectionRegistry NewRegistry() => new();

    [Fact]
    public void RegisterConnection_ThenGetActiveConnectionId_ReturnsIt_AndIsStreamConnectedIsTrue()
    {
        // Arrange
        var registry = NewRegistry();

        // Act
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-1");

        // Assert
        Assert.Equal("conn-1", registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
        Assert.True(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
    }

    [Fact]
    public void GetActiveConnectionId_IsCaseInsensitiveOnMachineName()
    {
        // Arrange
        var registry = NewRegistry();
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "Machine-A", "conn-1");

        // Assert - the map is keyed case-insensitively (OrdinalIgnoreCase).
        Assert.Equal("conn-1", registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-a"));
        Assert.True(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "MACHINE-A"));
    }

    [Fact]
    public void Unregister_ClearsTheActiveConnection()
    {
        // Arrange
        var registry = NewRegistry();
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-1");

        // Act
        registry.Unregister("conn-1");

        // Assert
        Assert.False(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
        Assert.Null(registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
    }

    [Fact]
    public void Reconnect_SecondConnectionSameMachine_Supersedes()
    {
        // Arrange - a launcher restart / reconnect: a new connection for the same machine wins.
        var registry = NewRegistry();
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-1");

        // Act
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-2");

        // Assert
        Assert.Equal("conn-2", registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
    }

    [Fact]
    public void LateUnregisterOfSupersededConnection_DoesNotWipeNewerConnection()
    {
        // Arrange - a reconnect overlap: conn-2 becomes the active connection for the machine, THEN conn-1
        // (the superseded old connection) disconnects late. The atomic compare-remove must ignore the stale
        // disconnect so the newer connection keeps owning the machine.
        var registry = NewRegistry();
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-1");
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-2");

        // Act
        registry.Unregister("conn-1");

        // Assert - the newer connection still owns the machine.
        Assert.True(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
        Assert.Equal("conn-2", registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
    }

    [Fact]
    public void UnregisterUnknownConnection_IsANoOp()
    {
        // Arrange
        var registry = NewRegistry();
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-1");

        // Act - a connection id the registry never held.
        registry.Unregister("conn-does-not-exist");

        // Assert - the active connection is untouched.
        Assert.True(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
        Assert.Equal("conn-1", registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
    }

    [Fact]
    public void GetActiveConnectionId_ForUnknownMachine_ReturnsNull()
    {
        var registry = NewRegistry();
        Assert.Null(registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "nobody"));
        Assert.False(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "nobody"));
    }

    [Fact]
    public void TwoMachines_DoNotCrossContaminate()
    {
        // Arrange - two machines each with their own launcher connection.
        var registry = NewRegistry();
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-A", "conn-A");
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "machine-B", "conn-B");

        // Act - one disconnects.
        registry.Unregister("conn-A");

        // Assert - the other is unaffected.
        Assert.False(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "machine-A"));
        Assert.True(registry.IsStreamConnected(CcDirector.Core.Tenancy.TenantId.Local, "machine-B"));
        Assert.Equal("conn-B", registry.GetActiveConnectionId(CcDirector.Core.Tenancy.TenantId.Local, "machine-B"));
    }

    // Issue #4423: the Gateway host writes a cleared stream onto the launcher's registration, so a launcher that is
    // heartbeating with no stream can be told apart from one that never had a stream.
    [Fact]
    public void Unregister_ClearingTheActiveStream_ReportsTheClose()
    {
        // Arrange
        var registry = NewRegistry();
        var closed = new List<string>();
        registry.StreamClosed = (_, machine, _) => closed.Add(machine);
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "Studio-Mac", "conn-1");

        // Act
        registry.Unregister("conn-1");

        // Assert
        Assert.Equal(new[] { "studio-mac" }, closed);
    }

    [Fact]
    public void Unregister_SupersededConnection_ReportsNoClose()
    {
        // Arrange - the machine still holds a stream, so nothing closed.
        var registry = NewRegistry();
        var closed = new List<string>();
        registry.StreamClosed = (_, machine, _) => closed.Add(machine);
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "studio-mac", "conn-1");
        registry.RegisterConnection(CcDirector.Core.Tenancy.TenantId.Local, "studio-mac", "conn-2");

        // Act
        registry.Unregister("conn-1");

        // Assert
        Assert.Empty(closed);
    }
}
