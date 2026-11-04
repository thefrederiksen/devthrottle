using CcDirector.Setup.Cli;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

public class InstallScopeTests
{
    // Issue #4567: a test install with --root repointed the machine's own Start Menu shortcut at the test copy.
    [Fact]
    public void FinalizesTheMachine_RootGiven_False()
    {
        Assert.False(InstallScope.FinalizesTheMachine(perUserTouched: true, rootOption: @"C:\temp\scratch-root"));
    }

    [Fact]
    public void FinalizesTheMachine_NoRoot_AndSomethingPlaced_True()
    {
        Assert.True(InstallScope.FinalizesTheMachine(perUserTouched: true, rootOption: null));
    }

    [Fact]
    public void FinalizesTheMachine_NothingPlaced_False()
    {
        Assert.False(InstallScope.FinalizesTheMachine(perUserTouched: false, rootOption: null));
    }

    // The headline regression guard: a full Gateway install is a true superset of a Workstation install,
    // so it MUST also install the per-user Python tools bundle. (A stale workstation-only gate once
    // dropped the cc-* tools on a Gateway install; this locks that it never returns.)
    [Fact]
    public void InstallsPythonTools_GatewayInstall_True()
    {
        Assert.True(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: false, componentScoped: false));
    }

    [Fact]
    public void InstallsPythonTools_WorkstationInstall_True()
    {
        Assert.True(InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false, componentScoped: false));
    }

    [Theory]
    [InlineData(InstallRole.Gateway)]
    [InlineData(InstallRole.Workstation)]
    public void InstallsPythonTools_BothRolesAgree(InstallRole role)
    {
        // Role-independent by design: whatever Workstation does, Gateway does too.
        var gateway = InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: false, componentScoped: false);
        var workstation = InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false, componentScoped: false);
        Assert.Equal(workstation, gateway);
        // The parameterized role still resolves to the same decision.
        Assert.True(InstallScope.InstallsPythonTools(role, installMode: true, dryRun: false, componentScoped: false));
    }

    [Fact]
    public void InstallsPythonTools_DryRun_False()
    {
        Assert.False(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: true, componentScoped: false));
    }

    [Fact]
    public void InstallsPythonTools_UpdateMode_False()
    {
        // A plain `update` pass (installMode: false) refreshes apps/tools already present; it does not
        // run the full bundle install step.
        Assert.False(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: false, dryRun: false, componentScoped: false));
    }

    [Fact]
    public void InstallsPythonTools_PlatformIndependent_TrueOnInstall()
    {
        // The decision is platform-independent: PythonToolsInstaller selects the right bundle
        // assets per platform itself, and the release ships macOS bundles. The old Windows-only
        // gate silently skipped the cc-* tools on a macOS install (issue #1445).
        Assert.True(InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false, componentScoped: false));
    }

    // A component-scoped install (--component <id>) is narrowed to that one component; the Python tools
    // bundle is not a narrowable component, so it must NOT be provisioned. This is what keeps the GUI's
    // Gateway sub-install fast. Revert-proof: drop the componentScoped term from InstallsPythonTools ->
    // this reds.
    [Fact]
    public void InstallsPythonTools_ComponentScoped_False()
    {
        Assert.False(InstallScope.InstallsPythonTools(InstallRole.Gateway, installMode: true, dryRun: false, componentScoped: true));
        Assert.False(InstallScope.InstallsPythonTools(InstallRole.Workstation, installMode: true, dryRun: false, componentScoped: true));
    }

    [Theory]
    [InlineData(null, false)]        // no --component -> full install -> not scoped
    [InlineData("", false)]
    [InlineData("all", false)]       // --component all is the explicit "everything" -> not scoped
    [InlineData("ALL", false)]
    [InlineData("gateway", true)]    // the GUI's Gateway sub-install -> scoped
    [InlineData("director", true)]
    public void IsComponentScoped_Cases(string? option, bool expected)
    {
        Assert.Equal(expected, InstallScope.IsComponentScoped(option));
    }

    // Production-path proof for the exact invocation the WPF Gateway GUI shells
    // (GatewayTrayLauncher: `install --role gateway --component gateway`): the parsed args map to a
    // component-scoped install whose tools gate is FALSE, so the GUI's Gateway sub-install does NOT pay the
    // bundle install. A normal full CLI install (no --component) still provisions the tools.
    // Revert-proof: make InstallsPythonTools ignore componentScoped, or make IsComponentScoped ignore the
    // option -> the Gateway assertion reds.
    [Fact]
    public void GatewayGuiSubInstall_DoesNotProvisionTools_ButFullInstallDoes()
    {
        var gatewayGui = CliArgs.Parse(["install", "--role", "gateway", "--component", "gateway"]);
        var gatewayScoped = InstallScope.IsComponentScoped(gatewayGui.Option("component"));
        Assert.True(gatewayScoped);
        Assert.False(InstallScope.InstallsPythonTools(
            InstallRole.Gateway, installMode: true, dryRun: gatewayGui.HasFlag("dry-run"), componentScoped: gatewayScoped));

        var fullInstall = CliArgs.Parse(["install", "--role", "gateway"]);
        var fullScoped = InstallScope.IsComponentScoped(fullInstall.Option("component"));
        Assert.False(fullScoped);
        Assert.True(InstallScope.InstallsPythonTools(
            InstallRole.Gateway, installMode: true, dryRun: fullInstall.HasFlag("dry-run"), componentScoped: fullScoped));
    }
}
