using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// A Director's own folder is never an install root.
///
/// <see cref="InstallLayout.Default"/> is what the installer, the tool reconciler, the tool updater and
/// the setup command line all use to decide where the product goes, and it used to take the
/// CC_DIRECTOR_ROOT setting at face value. Every Director points that setting at its own folder and
/// hands it to every session it starts, so an install or a tools repair run from inside a Director - or
/// from a checkout inside one of its sessions, which is how it actually happened on 17 September 2026 -
/// installed a complete second copy of the product into that Director's data folder: the launchers, the
/// interpreter, the shared environment, and the Director executable and launcher beside them. One
/// computer finished with seven copies.
///
/// The other half is asserted here too: a rig root of its own is left exactly as it is. A fix that
/// climbed out of every root would send an isolated proof's install onto the real machine, which is a
/// worse failure than the one being fixed.
/// </summary>
[Collection(MachineRootCollection.Name)]
public sealed class InstallLayoutMachineRootTests
{
    private const string RootVariable = "CC_DIRECTOR_ROOT";

    [Fact]
    public void An_install_asked_from_inside_a_Director_lands_at_the_machine_root()
    {
        var machine = Path.Combine(Path.GetTempPath(), "cc-machine-" + Guid.NewGuid().ToString("N"));
        var director = Path.Combine(machine, "instances", "slot-5");

        using var _ = new PinnedRootVariable(director);
        var layout = InstallLayout.Default();

        Assert.Equal(machine, layout.LocalRoot);
        Assert.Equal(Path.Combine(machine, "bin"), layout.BinDir);
        Assert.Equal(Path.Combine(machine, "pyenv"), layout.PyenvDir);
        Assert.Equal(Path.Combine(machine, "python"), layout.PythonDir);
        Assert.Equal(Path.Combine(machine, "app"), layout.AppDir);
        Assert.Equal(Path.Combine(machine, "launcher"), layout.LauncherDir);
        Assert.Equal(
            Path.Combine(machine, "config", "setup", "installed.json"),
            layout.InstalledManifestPath);
    }

    [Fact]
    public void A_nested_Director_folder_is_climbed_all_the_way_out()
    {
        // The leak made these: a repair that ran from inside a session left an
        // instances/default/instances/default on the computer that prompted the work.
        var machine = Path.Combine(Path.GetTempPath(), "cc-machine-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(machine, "instances", "default", "instances", "default");

        using var _ = new PinnedRootVariable(nested);

        Assert.Equal(machine, InstallLayout.Default().LocalRoot);
    }

    [Fact]
    public void A_rig_root_of_its_own_still_installs_into_itself()
    {
        var rig = Path.Combine(Path.GetTempPath(), "cc-rig-" + Guid.NewGuid().ToString("N"));

        using var _ = new PinnedRootVariable(rig);
        var layout = InstallLayout.Default();

        Assert.Equal(rig, layout.LocalRoot);
        Assert.Equal(Path.Combine(rig, "bin"), layout.BinDir);
        Assert.Equal(Path.Combine(rig, "pyenv"), layout.PyenvDir);
    }

    [Fact]
    public void The_tool_reconciler_reconciles_the_machines_install()
    {
        // The reconciler runs unattended at Director start and takes InstallLayout.Default() when it is
        // given none. That default is the whole reason a Director could reconcile a copy of the tools
        // into its own folder and then treat that copy as the install from then on.
        var machine = Path.Combine(Path.GetTempPath(), "cc-machine-" + Guid.NewGuid().ToString("N"));
        var director = Path.Combine(machine, "instances", "slot-5");

        using var _ = new PinnedRootVariable(director);

        Assert.Equal(machine, new ToolReconciler().Layout.LocalRoot);
    }

    private sealed class PinnedRootVariable : IDisposable
    {
        private readonly string? _previous;

        public PinnedRootVariable(string value)
        {
            _previous = Environment.GetEnvironmentVariable(RootVariable);
            Environment.SetEnvironmentVariable(RootVariable, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(RootVariable, _previous);
    }
}

/// <summary>
/// Serializes every class that points the process-wide CC_DIRECTOR_ROOT setting somewhere. It is one
/// variable for the whole test process, so two classes changing it at once would each read the other's
/// value and both would assert something nobody wrote.
/// </summary>
[CollectionDefinition(Name)]
public sealed class MachineRootCollection
{
    public const string Name = "MachineRoot";
}
