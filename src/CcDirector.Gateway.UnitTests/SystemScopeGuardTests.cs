using CcDirector.TestInfrastructure;
using System.Reflection;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Guards the fleet-wide access boundary - the "system administrator" access mode. A cross-tenant read of
/// live in-memory state must be a single, auditable decision, never something a request handler can reach
/// for by accident. Two invariants that fail the BUILD if broken:
///
///   1. <c>SystemScope.Grant()</c> - which mints the fleet-wide capability - is called ONLY in the
///      composition root (<c>GatewayHost.cs</c>). Every other component receives the token by injection.
///   2. The fleet-wide <c>DirectorRegistry</c> listing requires a <see cref="SystemScope"/>. There is no
///      ungated no-argument overload - that shape was where cross-tenant leaks kept appearing.
///
/// As more fleet-wide accessors are sealed behind <see cref="SystemScope"/>, add their no-arg-absence
/// checks here so the guard grows with the boundary.
/// </summary>
public sealed class SystemScopeGuardTests
{
    [Fact]
    public void SystemScope_Grant_is_called_only_in_the_composition_root()
    {
        var gatewaySrc = LocateGatewaySource();
        var offenders = new List<string>();
        foreach (var file in RepositorySourceIndex.Under(gatewaySrc, ".cs"))
        {
            var name = Path.GetFileName(file);
            if (name == "SystemScope.cs") continue; // the declaration of Grant() itself
            if (name == "GatewayHost.cs") continue; // the one sanctioned composition-root call site
            var text = File.ReadAllText(file);
            if (text.Contains("SystemScope.Grant("))
                offenders.Add(Path.GetRelativePath(gatewaySrc, file));
        }

        Assert.True(offenders.Count == 0,
            "SystemScope.Grant() mints fleet-wide, cross-tenant access and may be called ONLY in the composition " +
            "root (GatewayHost.cs). A system pass that needs it must receive the token by injection, not mint its " +
            "own. Found calls in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DirectorRegistry_has_no_ungated_fleet_wide_list()
    {
        // A public no-argument ListDirectors() would be an ungated cross-tenant reach that any code could call.
        var noArg = typeof(DirectorRegistry).GetMethod(
            "ListDirectors", BindingFlags.Public | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null);
        Assert.True(noArg is null,
            "DirectorRegistry.ListDirectors() (no arguments) is an ungated fleet-wide accessor. The fleet-wide " +
            "overload must require a SystemScope; serving a client is ListDirectors(TenantId).");

        // The sanctioned fleet-wide overload exists and is gated by the capability token.
        var gated = typeof(DirectorRegistry).GetMethod(
            "ListDirectors", BindingFlags.Public | BindingFlags.Instance, binder: null, types: new[] { typeof(SystemScope) }, modifiers: null);
        Assert.True(gated is not null, "The fleet-wide ListDirectors(SystemScope) overload should exist.");

        // The tenant-scoped overload a handler actually uses stays available.
        var scoped = typeof(DirectorRegistry).GetMethod(
            "ListDirectors", BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { typeof(CcDirector.Core.Tenancy.TenantId) }, modifiers: null);
        Assert.True(scoped is not null, "The tenant-scoped ListDirectors(TenantId) overload should exist.");
    }

    private static string LocateGatewaySource() => Path.Combine(RepositorySourceIndex.Root, "src", "CcDirector.Gateway");
}
