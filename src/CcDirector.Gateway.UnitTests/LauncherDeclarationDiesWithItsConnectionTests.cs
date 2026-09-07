using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A launcher's declaration is stored WITH its connection and dies WITH it - issue #2720.
///
/// WHY THAT IS THE WHOLE DESIGN. The declaration and the connection were going to be two entries: a
/// connection map and a declaration map. That shape can express a state which must never exist - a
/// declaration with no connection, which is a launcher that has gone still vouching for itself out of a
/// record nobody cleared. A capability query reading that would certify a machine that is not there,
/// which is the exact failure the whole phase exists to prevent, arriving by a different door.
///
/// Stored as one object, the impossible state cannot be written. These tests hold that, and they hold
/// the two-tenant and superseded-connection cases where a stale declaration would be worst.
/// </summary>
public sealed class LauncherDeclarationDiesWithItsConnectionTests
{
    private static readonly TenantId Tenant = TenantId.Local;

    private static LauncherCapabilityDeclaration Declaration(string version) => new()
    {
        Commands = new List<string> { LauncherCapabilities.DirectorRestart },
        RestartSignalArmed = true,
        ServingRootKey = version,
        ServingRootIsInstanceHome = false,
    };

    [Fact]
    public void A_declaration_is_read_back_with_the_connection_that_carried_it()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "machine-A", "conn-1", Declaration("root-a"));

        var connection = registry.GetActiveConnection(Tenant, "machine-A");

        Assert.NotNull(connection);
        Assert.Equal("conn-1", connection!.ConnectionId);
        Assert.Equal("root-a", connection.Declaration!.ServingRootKey);
    }

    /// <summary>
    /// A LAUNCHER OLDER THAN THE HANDSHAKE. It joins, says nothing about itself, and is perfectly
    /// reachable. Null is stored and read back as null - a REACHABLE launcher that declared nothing,
    /// which the verdict fold reports as its own state and never as "too old" or "not there".
    /// </summary>
    [Fact]
    public void A_launcher_that_declares_nothing_is_stored_as_connected_with_no_declaration()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "machine-A", "conn-1");

        var connection = registry.GetActiveConnection(Tenant, "machine-A");

        Assert.NotNull(connection);
        Assert.Null(connection!.Declaration);
        Assert.True(registry.IsStreamConnected(Tenant, "machine-A"));
    }

    /// <summary>
    /// THE ONE THAT MATTERS. When the launcher goes, its promises go with it. A declaration surviving a
    /// disconnect would let the capability query answer for a machine that is no longer there - and it
    /// would answer YES, because everything in that record was true when it was written.
    /// </summary>
    [Fact]
    public void A_disconnect_takes_the_declaration_with_it()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "machine-A", "conn-1", Declaration("root-a"));

        registry.Unregister("conn-1");

        Assert.Null(registry.GetActiveConnection(Tenant, "machine-A"));
        Assert.False(registry.IsStreamConnected(Tenant, "machine-A"));
    }

    /// <summary>
    /// A LAUNCHER UPDATED IN PLACE. The new process opens a new connection and sends its own Hello, and
    /// the old build's promises must not answer for it. This is the ordinary shape of every launcher
    /// update, not an edge case - and the failure would be the wrong direction: an old build's
    /// declaration outliving it says the machine can do things the running build cannot.
    /// </summary>
    [Fact]
    public void A_superseding_connection_replaces_the_declaration_as_well_as_the_connection()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "machine-A", "conn-old", Declaration("root-old"));

        registry.RegisterConnection(Tenant, "machine-A", "conn-new", Declaration("root-new"));

        var connection = registry.GetActiveConnection(Tenant, "machine-A");
        Assert.Equal("conn-new", connection!.ConnectionId);
        Assert.Equal("root-new", connection.Declaration!.ServingRootKey);
    }

    /// <summary>
    /// And the superseded connection's LATE disconnect removes nothing - the existing atomic
    /// compare-remove, now carrying a declaration with it. Without this the new build's promises would
    /// be wiped by the old process finally noticing it had been replaced.
    /// </summary>
    [Fact]
    public void A_late_disconnect_from_a_superseded_connection_does_not_wipe_the_new_declaration()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "machine-A", "conn-old", Declaration("root-old"));
        registry.RegisterConnection(Tenant, "machine-A", "conn-new", Declaration("root-new"));

        registry.Unregister("conn-old");

        var connection = registry.GetActiveConnection(Tenant, "machine-A");
        Assert.NotNull(connection);
        Assert.Equal("root-new", connection!.Declaration!.ServingRootKey);
    }

    /// <summary>
    /// The composite key holds for declarations too. A machine name is unique only within a tenant, so
    /// one subscriber's launcher declaration must never answer for another subscriber's machine of the
    /// same bare name.
    /// </summary>
    [Fact]
    public void Two_tenants_naming_one_machine_hold_two_separate_declarations()
    {
        var registry = new LauncherConnectionRegistry();
        var other = new TenantId("account-two");
        registry.RegisterConnection(Tenant, "SOREN-NORTH", "conn-1", Declaration("root-local"));
        registry.RegisterConnection(other, "SOREN-NORTH", "conn-2", Declaration("root-other"));

        Assert.Equal("root-local", registry.GetActiveConnection(Tenant, "SOREN-NORTH")!.Declaration!.ServingRootKey);
        Assert.Equal("root-other", registry.GetActiveConnection(other, "SOREN-NORTH")!.Declaration!.ServingRootKey);

        // And one going does not take the other's promises.
        registry.Unregister("conn-2");
        Assert.NotNull(registry.GetActiveConnection(Tenant, "SOREN-NORTH"));
        Assert.Null(registry.GetActiveConnection(other, "SOREN-NORTH"));
    }

    /// <summary>The machine name is keyed case-insensitively for the declaration exactly as it is for the
    /// connection - they are one lookup, so they cannot disagree.</summary>
    [Fact]
    public void The_declaration_is_found_by_the_machine_name_in_any_case()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "Machine-A", "conn-1", Declaration("root-a"));

        Assert.Equal("root-a", registry.GetActiveConnection(Tenant, "MACHINE-a")!.Declaration!.ServingRootKey);
    }

    /// <summary>The older accessor still answers, so nothing that only needs a connection id had to
    /// change - and it answers from the SAME record, so it can never disagree with the declaration.</summary>
    [Fact]
    public void The_connection_id_accessor_reads_the_same_record()
    {
        var registry = new LauncherConnectionRegistry();
        registry.RegisterConnection(Tenant, "machine-A", "conn-1", Declaration("root-a"));

        Assert.Equal("conn-1", registry.GetActiveConnectionId(Tenant, "machine-A"));
        Assert.Equal(registry.GetActiveConnectionId(Tenant, "machine-A"),
            registry.GetActiveConnection(Tenant, "machine-A")!.ConnectionId);
    }
}
